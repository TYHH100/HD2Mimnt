using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 观察 HellForge 会话进度：以 play --wait yes 启动会话并逐行解析 CLI 的 JSON 事件
/// （started / status / mod-problems / steam-input / ended），对外发布状态快照。
/// 会话本身由 HellForge 的会话运行器负责；关闭本观察不会停止游戏。
/// MOD SYNC 要求重启时（result.json 的 restart=mod-sync），官方 CLI 的 play 不接收 --mod-sync，
/// 因此按 play --dry-run 给出的会话运行器命令行补上参数后自行启动，并跟随新的 result.json。
/// </summary>
public sealed class HellForgeSessionService : IDisposable
{
    /// <summary>同一轮启动里最多自动重启几次，防止房主模组反复变化导致无限重启。</summary>
    private const int MaxAutomaticRestarts = 2;

    private readonly HellForgeService _cli;
    private readonly ILogger<HellForgeSessionService>? _logger;
    private readonly object _gate = new();
    private HellForgeSessionProgress _state = HellForgeSessionProgress.Idle;
    private CancellationTokenSource? _lifetime;
    private Process? _playProcess;
    private bool _disposed;
    private bool _stopRequested;
    private bool _restartInProgress;
    private bool _modSyncPollingActive;
    private int _modSyncGeneration;
    private int _restartCount;
    private string? _pendingRestartResult;

    public event EventHandler? StateChanged;
    /// <summary>play 发出 started（会话运行器已接管）时触发，可能来自后台线程。</summary>
    public event EventHandler? SessionStarted;

    public HellForgeSessionService(HellForgeService cli, ILogger<HellForgeSessionService>? logger = null)
    {
        _cli = cli;
        _logger = logger;
    }

    public HellForgeSessionProgress State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>
    /// 启动会话并等待 started 事件后返回；观察在后台持续到会话结束。
    /// 启动失败（已有会话、副本未就绪等）抛出 CLI 的错误信息。
    /// </summary>
    public async Task StartAsync(CancellationToken launchCancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HellForgeSessionService));
            if (_state.IsActive || _restartInProgress) throw new InvalidOperationException("已有 HellForge 会话正在运行，请先停止。");
        }

        var startInfo = _cli.CreateCliStartInfo("play", "--wait", "yes");
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 HellForge CLI 进程。");
        var lifetime = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _lifetime?.Dispose();
            _lifetime = lifetime;
            _playProcess = process;
            _stopRequested = false;
            _restartCount = 0;
            _pendingRestartResult = null;
            _restartInProgress = false;
        }

        _ = Task.Run(() => ObserveAsync(process, started, lifetime.Token));
        using var registration = launchCancellationToken.Register(() => started.TrySetCanceled());
        await started.Task;
    }

    /// <summary>停止当前会话并等待清理完成；无会话时抛出 CLI 的错误信息。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // 与官方启动器一致：玩家自己要停，就不再按房主模组重启。
        lock (_gate) _stopRequested = true;
        await _cli.StopSessionAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// 用 status 命令校准状态：本管理器正在观察的会话以事件流为准，
    /// 外部启动的会话只能得到运行中/最近一次结果。
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var status = await _cli.GetStatusAsync(cancellationToken);
        lock (_gate)
        {
            if (_state.IsActive && _state.Tracked) return;
            if (_restartInProgress) return;
            _state = status.Session.Running
                ? new HellForgeSessionProgress { Phase = HellForgeSessionPhase.Running, RunId = Path.GetFileName(status.Session.LastRun ?? "") }
                : new HellForgeSessionProgress
                {
                    Phase = HellForgeSessionProgress.PhaseFromStatus(status.Session.LastStatus),
                    RunId = Path.GetFileName(status.Session.LastRun ?? ""),
                    Failure = status.Session.LastFailure
                };
        }
        CancellationToken pollingToken;
        lock (_gate)
        {
            if (_state.IsActive) _lifetime ??= new CancellationTokenSource();
            pollingToken = _lifetime?.Token ?? CancellationToken.None;
        }
        if (_state.IsActive) BeginModSyncPolling(pollingToken);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>应答游戏外置编辑器未覆盖的 MOD SYNC 提示；版本号防止把旧提示的单击发到新提示。</summary>
    public async Task AnswerModSyncOfferAsync(string answer, string? modId = null,
        CancellationToken cancellationToken = default)
    {
        var offer = State.ModSyncOffer;
        if (offer == null) throw new InvalidOperationException("当前没有可应答的 MOD SYNC 提示。");
        lock (_gate) _modSyncGeneration++;
        var result = await _cli.AnswerModSyncOfferAsync(answer, offer.Version, modId, cancellationToken);
        lock (_gate) _modSyncGeneration++;
        Update(state => state with
        {
            ModSyncOffer = result.InGamePrompt ? null : result.Offer
        });
    }

    private void BeginModSyncPolling(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed || _modSyncPollingActive || !_state.IsActive) return;
            _modSyncPollingActive = true;
        }
        _ = Task.Run(() => PollModSyncAsync(cancellationToken));
    }

    private async Task PollModSyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!State.IsActive) return;
                int generation;
                lock (_gate) generation = _modSyncGeneration;
                HellForgeModSyncOfferResult? result;
                try { result = await _cli.GetModSyncOfferAsync(cancellationToken); }
                catch (OperationCanceledException) { return; }
                catch (InvalidOperationException error)
                {
                    _logger?.LogDebug(error, "读取 HellForge MOD SYNC 提示失败");
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }
                if (result == null || !State.IsActive) return;
                lock (_gate) if (generation != _modSyncGeneration) continue;
                Update(state => state with
                {
                    ModSyncOffer = result.InGamePrompt ? null : result.Offer
                });
                if (!State.IsActive) return;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate) _modSyncPollingActive = false;
        }
    }

    private async Task ObserveAsync(Process process, TaskCompletionSource started, CancellationToken cancellationToken)
    {
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    ApplyEvent(document.RootElement, started, cancellationToken);
                }
                catch (JsonException)
                {
                    // CLI 偶尔混入非 JSON 行时跳过，不影响事件流。
                }
            }
            await process.WaitForExitAsync(cancellationToken);
            var stderr = (await stderrTask).Trim();
            string? restart;
            lock (_gate) restart = _pendingRestartResult;
            if (restart != null)
            {
                // MOD SYNC 停止的会话由同样的运行器继续，不按 CLI 退出码记为失败。
                await RestartForModSyncAsync(restart, cancellationToken);
                return;
            }
            var failure = stderr.Length > 0 ? stderr : $"HellForge CLI 退出码 {process.ExitCode}。";
            started.TrySetException(new InvalidOperationException(failure));
            lock (_gate)
            {
                if (_state.IsActive)
                    _state = _state with { Phase = HellForgeSessionPhase.Failed, Failure = failure };
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            started.TrySetCanceled();
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "观察 HellForge 会话失败");
            started.TrySetException(error);
            lock (_gate)
            {
                if (_state.IsActive)
                    _state = _state with { Phase = HellForgeSessionPhase.Failed, Failure = error.Message };
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ApplyEvent(JsonElement element, TaskCompletionSource started, CancellationToken cancellationToken)
    {
        var eventName = element.TryGetProperty("event", out var name) ? name.GetString() : null;
        switch (eventName)
        {
            case "started":
                Update(state => state with
                {
                    Phase = HellForgeSessionPhase.Preparing,
                    RunId = Text(element, "runId"),
                    Tracked = true,
                    Failure = null,
                    Problems = Array.Empty<string>(),
                    Notice = null,
                    ModSyncOffer = null,
                    Restarting = false,
                    RestartIssue = HellForgeRestartIssue.None,
                    RestartIssueDetail = null
                });
                SessionStarted?.Invoke(this, EventArgs.Empty);
                started.TrySetResult();
                BeginModSyncPolling(cancellationToken);
                break;
            case "status":
                var status = Text(element, "status");
                var failure = Text(element, "failure");
                Update(state => state with
                {
                    Phase = HellForgeSessionProgress.PhaseFromStatus(status),
                    Failure = failure
                });
                break;
            case "mod-problems":
                if (element.TryGetProperty("problems", out var problems) && problems.ValueKind == JsonValueKind.Array)
                {
                    var list = problems.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString()!)
                        .ToList();
                    Update(state => state with { Problems = list });
                }
                break;
            case "steam-input":
                Update(state => state with { Notice = Text(element, "note") });
                break;
            case "ended":
                var finalStatus = Text(element, "status");
                var resultPath = Text(element, "result");
                var restart = resultPath != null && !cancellationToken.IsCancellationRequested && !_stopRequested
                    && WantsModSyncRestart(resultPath);
                if (restart)
                {
                    lock (_gate)
                    {
                        if (!_disposed)
                        {
                            _pendingRestartResult = resultPath;
                            _restartInProgress = true;
                        }
                        else restart = false;
                    }
                }
                Update(state => state with
                {
                    Phase = finalStatus == "observed" ? HellForgeSessionPhase.Ended : HellForgeSessionPhase.Failed,
                    Failure = state.Failure,
                    ModSyncOffer = null,
                    Restarting = restart
                });
                break;
        }
    }

    /// <summary>
    /// MOD SYNC 的自动重启：按 play --dry-run 给出运行器与参数，追加 --mod-sync 指向
    /// &lt;安装根&gt;\temp\mod-sync\session.json，随后按 result.json 的 status 更新进度。
    /// 新一轮仍要求重启时继续，最多 MaxAutomaticRestarts 次。
    /// </summary>
    private async Task RestartForModSyncAsync(string resultPath, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (_gate) if (_disposed) { _restartInProgress = false; return; }
                if (!WantsModSyncRestart(resultPath)) { ClearRestarting(HellForgeSessionPhase.Ended); return; }
                if (_restartCount >= MaxAutomaticRestarts) { SetRestartIssue(HellForgeRestartIssue.TooManyRestarts, null); return; }
                if (!OperatingSystem.IsWindows()) { SetRestartIssue(HellForgeRestartIssue.UnsupportedPlatform, null); return; }
                if (!_cli.TryGetInstallationRoot(out var root))
                {
                    SetRestartIssue(HellForgeRestartIssue.StartFailed, "尚未配置有效的 HellForge 安装目录。");
                    return;
                }
                var syncFile = Path.Combine(root, "temp", "mod-sync", "session.json");
                if (!File.Exists(syncFile))
                {
                    SetRestartIssue(HellForgeRestartIssue.MissingSyncFile, syncFile);
                    return;
                }

                HellForgePlayDryRun dryRun;
                try { dryRun = await _cli.GetPlayDryRunAsync(cancellationToken); }
                catch (OperationCanceledException) { return; }
                catch (Exception error) { SetRestartIssue(HellForgeRestartIssue.DryRunFailed, error.Message); return; }

                var previousRunId = ReadRunId(Path.Combine(root, "logs", "play-session.json"));
                Process process;
                try { process = StartSessionRunner(root, dryRun, syncFile); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    SetRestartIssue(HellForgeRestartIssue.StartFailed, error.Message);
                    return;
                }

                _restartCount++;
                var stderr = new StringBuilder();
                _ = DrainAsync(process.StandardOutput);
                _ = DrainAsync(process.StandardError, stderr);
                _logger?.LogInformation("MOD SYNC: 用房主的模组重启会话（第 {Count} 次）", _restartCount);
                Update(state => state with
                {
                    Phase = HellForgeSessionPhase.Preparing,
                    RunId = null,
                    Failure = null,
                    Problems = Array.Empty<string>(),
                    Notice = null,
                    Restarting = true,
                    RestartIssue = HellForgeRestartIssue.None,
                    RestartIssueDetail = null
                });
                BeginModSyncPolling(cancellationToken);

                var run = await WaitForNewRunAsync(root, previousRunId, process, cancellationToken);
                if (run == null)
                {
                    var detail = process.HasExited ? stderr.ToString().Trim() : string.Empty;
                    DisposeQuietly(process);
                    SetRestartIssue(HellForgeRestartIssue.NewRunTimeout, detail.Length > 0 ? detail : null);
                    return;
                }
                Update(state => state with { RunId = Path.GetFileName(run), Phase = HellForgeSessionPhase.Preparing });
                var next = await FollowRestartedRunAsync(process, run, cancellationToken);
                if (next == null) return;
                resultPath = next;
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭管理器或停止会话：不再继续重启；已启动的会话运行器保持运行。
        }
        finally
        {
            lock (_gate) _restartInProgress = false;
        }
    }

    /// <summary>跟随重启后会话的 result.json，直到运行器退出；返回要求再次重启的 result 路径，否则 null。</summary>
    private async Task<string?> FollowRestartedRunAsync(Process process, string run, CancellationToken cancellationToken)
    {
        var resultPath = Path.Combine(run, "result.json");
        var lastStatus = string.Empty;
        try
        {
            while (true)
            {
                if (TryReadResult(resultPath) is { } state)
                {
                    var status = Text(state, "status");
                    var phase = HellForgeSessionProgress.PhaseFromStatus(status);
                    if (!string.IsNullOrEmpty(status))
                    {
                        lastStatus = status;
                        // 终态留给运行器退出后再判定（result.json 的 restart 可能比 status 晚一点落盘），
                        // 期间保持 Restarting，避免界面先提示“已结束”。
                        if (phase is not (HellForgeSessionPhase.Idle or HellForgeSessionPhase.Ended or HellForgeSessionPhase.Failed))
                            Update(s => s with
                            {
                                Phase = phase,
                                Failure = Text(state, "failure"),
                                Problems = ReadProblems(state),
                                Restarting = true
                            });
                    }
                }
                if (process.HasExited) break;
                await Task.Delay(1000, cancellationToken);
            }
        }
        finally
        {
            process.Dispose();
        }

        var final = TryReadResult(resultPath);
        var finalStatus = final is { } value ? Text(value, "status") : lastStatus;
        if (finalStatus != "observed")
        {
            Update(s => s with
            {
                Phase = HellForgeSessionPhase.Failed,
                Failure = final is { } failed && Text(failed, "failure") is { Length: > 0 } reason ? reason : s.Failure,
                ModSyncOffer = null,
                Restarting = false
            });
            return null;
        }
        if (WantsModSyncRestart(resultPath)) return resultPath;
        Update(s => s with { Phase = HellForgeSessionPhase.Ended, ModSyncOffer = null, Restarting = false });
        return null;
    }

    /// <summary>等新会话在 logs\play-session.json 里出现（会话运行器先写该文件再准备）；90 秒未出现即失败。</summary>
    private static async Task<string?> WaitForNewRunAsync(string root, string? previousRunId, Process process, CancellationToken cancellationToken)
    {
        var sessionFile = Path.Combine(root, "logs", "play-session.json");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(sessionFile))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(sessionFile));
                    var runId = document.RootElement.TryGetProperty("runId", out var id) ? id.GetString() : null;
                    var run = document.RootElement.TryGetProperty("run", out var value) ? value.GetString() : null;
                    if (!string.IsNullOrEmpty(run) && !string.Equals(runId, previousRunId, StringComparison.Ordinal)) return run;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            if (process.HasExited) return null;
            await Task.Delay(250, cancellationToken);
        }
        return null;
    }

    /// <summary>
    /// 按官方启动器的做法启动会话运行器（工作目录=安装根，TEMP/TMP 指向安装内 temp），
    /// 参数来自 play --dry-run，末尾追加 --mod-sync 与同步文件。
    /// </summary>
    private static Process StartSessionRunner(string root, HellForgePlayDryRun dryRun, string syncFile)
    {
        var runner = dryRun.SessionRunner;
        if (string.IsNullOrWhiteSpace(runner) || !File.Exists(runner))
            throw new InvalidOperationException($"未找到 HellForge 会话运行器：{runner}");
        var start = new ProcessStartInfo(runner)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in dryRun.Arguments) start.ArgumentList.Add(argument);
        if (!dryRun.Arguments.Contains("--mod-sync", StringComparer.Ordinal))
        {
            start.ArgumentList.Add("--mod-sync");
            start.ArgumentList.Add(syncFile);
        }
        var temp = Path.Combine(root, "temp");
        Directory.CreateDirectory(temp);
        start.Environment["TEMP"] = start.Environment["TMP"] = temp;
        if (dryRun.CrashDump)
            start.Environment["HELLFORGE_CRASH_DUMP"] = Path.Combine(root, "app", "cli", "HellForge.Cli.exe");
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动 HellForge 会话运行器。");
    }

    private static bool WantsModSyncRestart(string resultPath)
    {
        // result.json 可能在 status 之后才写全；读失败时短暂重试，避免漏掉 mod-sync。
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
                return document.RootElement.TryGetProperty("restart", out var restart) && restart.GetString() == "mod-sync";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }

    private static JsonElement? TryReadResult(string resultPath)
    {
        try
        {
            if (!File.Exists(resultPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadRunId(string sessionFile)
    {
        try
        {
            if (!File.Exists(sessionFile)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(sessionFile));
            return document.RootElement.TryGetProperty("runId", out var id) ? id.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadProblems(JsonElement state)
        => state.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Object &&
           mods.TryGetProperty("problems", out var problems) && problems.ValueKind == JsonValueKind.Array
            ? problems.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToList()
            : Array.Empty<string>();

    /// <summary>排空子进程输出，避免管道写满阻塞；出错（进程关闭）时静默结束。</summary>
    private static async Task DrainAsync(StreamReader reader, StringBuilder? sink = null)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
                sink?.Append(buffer, 0, read);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void ClearRestarting(HellForgeSessionPhase phase)
    {
        Update(state => state with
        {
            Phase = state.IsActive ? phase : state.Phase,
            ModSyncOffer = null,
            Restarting = false
        });
        lock (_gate) _restartInProgress = false;
    }

    private void SetRestartIssue(HellForgeRestartIssue issue, string? detail)
    {
        Update(state => state with
        {
            Phase = state.IsActive ? HellForgeSessionPhase.Ended : state.Phase,
            Failure = state.Failure,
            ModSyncOffer = null,
            Restarting = false,
            RestartIssue = issue,
            RestartIssueDetail = detail
        });
        lock (_gate) _restartInProgress = false;
        _logger?.LogWarning("MOD SYNC 自动重启未完成：{Issue} {Detail}", issue, detail);
    }

    private static void DisposeQuietly(Process process)
    {
        try { process.Dispose(); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void Update(Func<HellForgeSessionProgress, HellForgeSessionProgress> change)
    {
        lock (_gate) _state = change(_state);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime?.Cancel();
            // 只结束观察进程（play --wait），不停止游戏会话；MOD SYNC 重启的会话运行器同样保持运行。
            // Kill(entireProcessTree) 在子进程已退出时抛 AggregateException（包裹 Win32Exception），须一并吞掉。
            try { _playProcess?.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
            _lifetime?.Dispose();
            _lifetime = null;
            _playProcess = null;
            _pendingRestartResult = null;
            _restartInProgress = false;
        }
    }
}
