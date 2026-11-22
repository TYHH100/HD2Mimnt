using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 游戏运行时 CPU 调度的上层协调器：读取设置、识别拓扑、观察进程、应用/验证/恢复并发布状态。
/// 所有 Win32 细节都在 Infrastructure；本服务不依赖 UI。
/// </summary>
public sealed class GameCpuSchedulingService : IGameCpuSchedulingService, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly ICpuTopologyProvider _topologyProvider;
    private readonly IProcessSchedulingController _controller;
    private readonly IGameProcessLocator _locator;
    private readonly ILogger<GameCpuSchedulingService>? _logger;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly object _stateGate = new();

    private CpuTopology? _topology;
    private CpuSchedulingSettings _settings;
    private CpuSchedulingStatus _status = CpuSchedulingStatus.Disabled;
    private string _detail = string.Empty;
    private int _currentProcessId;
    private long _currentStartTicks;
    private int _recoveryAttempts;
    private bool _hasAppliedOnce;
    private CpuSchedulingMode _appliedMode = CpuSchedulingMode.Off;
    private int _applyRequested;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    public GameCpuSchedulingService(
        SettingsService settingsService,
        ICpuTopologyProvider topologyProvider,
        IProcessSchedulingController controller,
        IGameProcessLocator locator,
        ILogger<GameCpuSchedulingService>? logger = null)
    {
        _settingsService = settingsService;
        _topologyProvider = topologyProvider;
        _controller = controller;
        _locator = locator;
        _logger = logger;
        _settings = LoadSettings();
        _settingsService.SettingChanged += OnSettingChanged;
    }

    public CpuSchedulingStatus Status
    {
        get { lock (_stateGate) return _status; }
    }

    public string StatusDetail
    {
        get { lock (_stateGate) return _detail; }
    }

    public CpuTopology? Topology
    {
        get { lock (_stateGate) return _topology; }
    }

    public CpuSchedulingSettings Settings
    {
        get { lock (_stateGate) return _settings; }
    }

    public int? CurrentProcessId
    {
        get { lock (_stateGate) return _currentProcessId == 0 ? null : _currentProcessId; }
    }

    public event EventHandler<CpuSchedulingStatusChangedEventArgs>? StatusChanged;

    public void Start()
    {
        if (_disposed)
            return;

        lock (_stateGate)
        {
            if (_loopTask is not null)
                return;
            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
        }

        Wake();
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_stateGate)
        {
            cts = _loopCts;
            loop = _loopTask;
            _loopCts = null;
            _loopTask = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            cts.Dispose();
        }

        if (Settings.RestoreOriginal)
        {
            var restored = RestoreCurrent(setRestoredStatus: true);
            if (!restored.Success)
                return;
        }

        SetStatus(CpuSchedulingStatus.Disabled, string.Empty);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = LoadSettings();
            lock (_stateGate) { _settings = settings; }
            var applyRequested = Interlocked.Exchange(ref _applyRequested, 0) == 1;
            var topology = _topologyProvider.GetTopology();
            lock (_stateGate) { _topology = topology; }

            if (!settings.IsActive)
            {
                var restored = RestoreOrForget(settings.RestoreOriginal, setRestoredStatus: false);
                if (!restored.Success)
                    return;
                SetStatus(CpuSchedulingStatus.Disabled, string.Empty);
                return;
            }

            if (!topology.Supported || topology.PerformanceSets.Count == 0)
            {
                var restored = RestoreOrForget(settings.RestoreOriginal, setRestoredStatus: false);
                if (!restored.Success)
                    return;
                SetStatus(CpuSchedulingStatus.Unsupported, topology.Reason);
                return;
            }

            var gamePath = _settingsService.GetEffectiveGamePath();
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            {
                var restored = RestoreOrForget(settings.RestoreOriginal, setRestoredStatus: false);
                if (!restored.Success)
                    return;
                SetStatus(CpuSchedulingStatus.Unsupported, "game path is not configured");
                return;
            }

            var process = _locator.FindGameProcess(gamePath);
            if (process is null)
            {
                ForgetProcessState();
                SetStatus(CpuSchedulingStatus.WaitingForGame, string.Empty);
                return;
            }

            var sameProcess = CurrentProcessId == process.ProcessId
                              && ReadCurrentStartTicks() == process.StartTimeUtcTicks;
            if (!sameProcess)
            {
                var oldPid = CurrentProcessId;
                if (oldPid is { } pid)
                    _controller.Forget(pid);
                _appliedMode = CpuSchedulingMode.Off;
                SetCurrent(process.ProcessId, process.StartTimeUtcTicks);

                var autoApply = _hasAppliedOnce ? settings.ReapplyOnRestart : settings.ApplyOnLaunch;
                if (!autoApply && !applyRequested)
                {
                    SetStatus(CpuSchedulingStatus.WaitingForGame, "automatic apply is disabled");
                    return;
                }
            }
            else
            {
                var status = Status;
                if (!applyRequested && status is CpuSchedulingStatus.PermissionDenied or CpuSchedulingStatus.Ineffective)
                    return;
                if (!applyRequested && status == CpuSchedulingStatus.WaitingForGame)
                    return;

                if (_appliedMode != CpuSchedulingMode.Off && _appliedMode != settings.Mode)
                {
                    _controller.Restore(process.ProcessId, force: true);
                    _appliedMode = CpuSchedulingMode.Off;
                    _recoveryAttempts = 0;
                }
                else if (status == CpuSchedulingStatus.Applied && !applyRequested)
                {
                    if (!settings.MonitorChanges)
                        return;

                    var verification = _controller.Verify(process.ProcessId, settings.Mode);
                    if (verification.Matches)
                        return;
                    if (!verification.ProcessAlive)
                    {
                        ForgetCurrent();
                        SetStatus(CpuSchedulingStatus.WaitingForGame, verification.Detail ?? "process ended");
                        return;
                    }
                    if (verification.Win32Error == 5)
                    {
                        SetStatus(CpuSchedulingStatus.PermissionDenied, FormatDetail(verification.Detail, verification.Win32Error));
                        return;
                    }
                    if (_recoveryAttempts >= 3)
                    {
                        SetStatus(CpuSchedulingStatus.Ineffective, "recovery attempts exhausted");
                        return;
                    }

                    _recoveryAttempts++;
                    SetStatus(CpuSchedulingStatus.Changed, verification.Detail ?? "scheduling changed");
                    _controller.Restore(process.ProcessId, force: true);
                    _appliedMode = CpuSchedulingMode.Off;
                }
                else if (status == CpuSchedulingStatus.Applied && applyRequested)
                {
                    _controller.Restore(process.ProcessId, force: true);
                    _appliedMode = CpuSchedulingMode.Off;
                }
            }

            SetStatus(CpuSchedulingStatus.Applying, string.Empty);
            var result = _controller.Apply(process.ProcessId, topology, settings.Mode);
            ApplyResult(result, settings.Mode);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public Task RefreshTopologyAsync()
    {
        var topology = _topologyProvider.GetTopology();
        lock (_stateGate) { _topology = topology; }
        PublishCurrentStatus();
        return Task.CompletedTask;
    }

    public Task<CpuSchedulingSettings> LoadSettingsAsync() => Task.FromResult(LoadSettings());

    public async Task SaveSettingsAsync(CpuSchedulingSettings settings, CancellationToken cancellationToken = default)
    {
        await _settingsService.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.CpuSchedulingEnabled] = settings.Enabled,
            [SettingKeys.CpuSchedulingMode] = settings.Mode.ToString(),
            [SettingKeys.CpuSchedulingApplyOnLaunch] = settings.ApplyOnLaunch,
            [SettingKeys.CpuSchedulingReapplyOnRestart] = settings.ReapplyOnRestart,
            [SettingKeys.CpuSchedulingMonitorChanges] = settings.MonitorChanges,
            [SettingKeys.CpuSchedulingRestoreOriginal] = settings.RestoreOriginal
        }).ConfigureAwait(false);

        lock (_stateGate) { _settings = settings; }
        _recoveryAttempts = 0;
        Interlocked.Exchange(ref _applyRequested, 1);
        Wake();
    }

    public async Task<CpuSchedulingOperationResult> ApplyToCurrentGameAsync(
        CpuSchedulingMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == CpuSchedulingMode.Off)
            return CpuSchedulingOperationResult.Fail(CpuSchedulingFailureKind.Unsupported, "scheduling mode is off");

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var topology = _topologyProvider.GetTopology();
            lock (_stateGate) { _topology = topology; }
            if (!topology.Supported || topology.PerformanceSets.Count == 0)
            {
                SetStatus(CpuSchedulingStatus.Unsupported, topology.Reason);
                return CpuSchedulingOperationResult.Fail(CpuSchedulingFailureKind.Unsupported, topology.Reason);
            }

            var gamePath = _settingsService.GetEffectiveGamePath();
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            {
                SetStatus(CpuSchedulingStatus.Unsupported, "game path is not configured");
                return CpuSchedulingOperationResult.Fail(
                    CpuSchedulingFailureKind.Unsupported, "game path is not configured");
            }

            var process = _locator.FindGameProcess(gamePath);
            if (process is null)
            {
                ForgetProcessState();
                SetStatus(CpuSchedulingStatus.WaitingForGame, string.Empty);
                return CpuSchedulingOperationResult.Fail(
                    CpuSchedulingFailureKind.ProcessNotFound, "game process is not running");
            }

            var oldPid = CurrentProcessId;
            if (oldPid is { } pid && pid != process.ProcessId)
                _controller.Forget(pid);
            SetCurrent(process.ProcessId, process.StartTimeUtcTicks);

            SetStatus(CpuSchedulingStatus.Applying, string.Empty);
            var result = _controller.Apply(process.ProcessId, topology, mode);
            ApplyResult(result, mode);
            return result;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public Task<CpuSchedulingOperationResult> RestoreCurrentGameAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = RestoreCurrent(setRestoredStatus: true);
        return Task.FromResult(result);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "CPU scheduling refresh failed");
                SetStatus(CpuSchedulingStatus.Error, ex.Message);
            }

            try
            {
                await _wake.WaitAsync(PollInterval(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private TimeSpan PollInterval() => Status switch
    {
        CpuSchedulingStatus.WaitingForGame => TimeSpan.FromSeconds(2),
        CpuSchedulingStatus.Applied => TimeSpan.FromSeconds(10),
        CpuSchedulingStatus.Changed => TimeSpan.FromSeconds(5),
        _ => TimeSpan.FromSeconds(10)
    };

    private CpuSchedulingOperationResult RestoreOrForget(bool restore, bool setRestoredStatus)
    {
        if (restore)
            return RestoreCurrent(setRestoredStatus);

        var pid = CurrentProcessId;
        if (pid is { } value)
            _controller.Forget(value);
        ForgetCurrent();
        _appliedMode = CpuSchedulingMode.Off;
        _recoveryAttempts = 0;
        return CpuSchedulingOperationResult.Ok("no restore requested");
    }

    private CpuSchedulingOperationResult RestoreCurrent(bool setRestoredStatus)
    {
        var pid = CurrentProcessId;
        if (pid is null)
            return CpuSchedulingOperationResult.Ok("no game process");

        var result = _controller.Restore(pid.Value);
        if (result.Success)
        {
            _controller.Forget(pid.Value);
            ForgetCurrent();
            _appliedMode = CpuSchedulingMode.Off;
            _recoveryAttempts = 0;
            if (setRestoredStatus)
                SetStatus(CpuSchedulingStatus.Restored, result.Message);
        }
        else
        {
            SetStatus(result.Failure switch
            {
                CpuSchedulingFailureKind.PermissionDenied => CpuSchedulingStatus.PermissionDenied,
                CpuSchedulingFailureKind.Changed => CpuSchedulingStatus.Changed,
                CpuSchedulingFailureKind.Unsupported => CpuSchedulingStatus.Unsupported,
                _ => CpuSchedulingStatus.Error
            }, FormatDetail(result.Message, result.Win32Error));
        }

        return result;
    }

    private void ApplyResult(CpuSchedulingOperationResult result, CpuSchedulingMode mode)
    {
        if (result.Success)
        {
            _hasAppliedOnce = true;
            _appliedMode = mode;
            _recoveryAttempts = 0;
            SetStatus(CpuSchedulingStatus.Applied, string.Empty);
            return;
        }

        switch (result.Failure)
        {
            case CpuSchedulingFailureKind.PermissionDenied:
                SetStatus(CpuSchedulingStatus.PermissionDenied, FormatDetail(result.Message, result.Win32Error));
                break;
            case CpuSchedulingFailureKind.Unsupported:
                SetStatus(CpuSchedulingStatus.Unsupported, result.Message);
                break;
            case CpuSchedulingFailureKind.VerificationFailed:
                SetStatus(CpuSchedulingStatus.Ineffective, result.Message);
                break;
            case CpuSchedulingFailureKind.ProcessNotFound:
                ForgetProcessState();
                SetStatus(CpuSchedulingStatus.WaitingForGame, result.Message);
                break;
            case CpuSchedulingFailureKind.Changed:
                SetStatus(CpuSchedulingStatus.Changed, result.Message);
                break;
            default:
                SetStatus(CpuSchedulingStatus.Error, FormatDetail(result.Message, result.Win32Error));
                break;
        }
    }

    private CpuSchedulingSettings LoadSettings()
    {
        var modeText = _settingsService.GetSetting(SettingKeys.CpuSchedulingMode, nameof(CpuSchedulingMode.Off));
        var mode = Enum.TryParse<CpuSchedulingMode>(modeText, ignoreCase: true, out var parsed)
            ? parsed
            : CpuSchedulingMode.Off;
        return new CpuSchedulingSettings(
            _settingsService.GetSetting(SettingKeys.CpuSchedulingEnabled, false),
            mode,
            _settingsService.GetSetting(SettingKeys.CpuSchedulingApplyOnLaunch, true),
            _settingsService.GetSetting(SettingKeys.CpuSchedulingReapplyOnRestart, true),
            _settingsService.GetSetting(SettingKeys.CpuSchedulingMonitorChanges, true),
            _settingsService.GetSetting(SettingKeys.CpuSchedulingRestoreOriginal, true));
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (!e.Key.StartsWith("Game.CpuScheduling.", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(e.Key, SettingKeys.GamePath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(e.Key, SettingKeys.HellForgePath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(e.Key, SettingKeys.GameMode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_stateGate) { _settings = LoadSettings(); }
        _recoveryAttempts = 0;
        Interlocked.Exchange(ref _applyRequested, 1);
        Wake();
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void SetCurrent(int processId, long startTicks)
    {
        lock (_stateGate)
        {
            _currentProcessId = processId;
            _currentStartTicks = startTicks;
        }
    }

    private void ForgetProcessState()
    {
        var pid = CurrentProcessId;
        if (pid is { } value)
            _controller.Forget(value);
        ForgetCurrent();
        _appliedMode = CpuSchedulingMode.Off;
        _recoveryAttempts = 0;
    }

    private void ForgetCurrent()
    {
        lock (_stateGate)
        {
            _currentProcessId = 0;
            _currentStartTicks = 0;
        }
    }

    private long ReadCurrentStartTicks()
    {
        lock (_stateGate) return _currentStartTicks;
    }

    private void SetStatus(CpuSchedulingStatus status, string detail)
    {
        CpuSchedulingStatusChangedEventArgs args;
        lock (_stateGate)
        {
            if (_status == status && string.Equals(_detail, detail, StringComparison.Ordinal))
                return;
            _status = status;
            _detail = detail;
            args = new CpuSchedulingStatusChangedEventArgs
            {
                Status = status,
                Detail = detail,
                ProcessId = _currentProcessId == 0 ? null : _currentProcessId,
                Topology = _topology
            };
        }

        if (status is CpuSchedulingStatus.Applied or CpuSchedulingStatus.Changed
            or CpuSchedulingStatus.PermissionDenied or CpuSchedulingStatus.Unsupported
            or CpuSchedulingStatus.Ineffective or CpuSchedulingStatus.Error)
        {
            _logger?.LogInformation("CPU scheduling status {Status}: {Detail} (PID {ProcessId})",
                status, detail, args.ProcessId);
        }

        StatusChanged?.Invoke(this, args);
    }

    private void PublishCurrentStatus()
    {
        CpuSchedulingStatusChangedEventArgs args;
        lock (_stateGate)
        {
            args = new CpuSchedulingStatusChangedEventArgs
            {
                Status = _status,
                Detail = _detail,
                ProcessId = _currentProcessId == 0 ? null : _currentProcessId,
                Topology = _topology
            };
        }

        StatusChanged?.Invoke(this, args);
    }

    private static string FormatDetail(string? message, int? win32Error)
        => win32Error is null
            ? message ?? string.Empty
            : $"{message ?? "error"} (Win32 {win32Error})";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _settingsService.SettingChanged -= OnSettingChanged;

        CancellationTokenSource? cts;
        lock (_stateGate)
        {
            cts = _loopCts;
            _loopCts = null;
            _loopTask = null;
        }

        cts?.Cancel();
        cts?.Dispose();
        _refreshGate.Dispose();
        _wake.Dispose();
    }
}