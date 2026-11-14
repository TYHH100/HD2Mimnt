using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 观察 HellForge 会话进度：以 play --wait yes 启动会话并逐行解析 CLI 的 JSON 事件
/// （started / status / mod-problems / steam-input / ended），对外发布状态快照。
/// 会话本身由 HellForge 的会话运行器负责；关闭本观察不会停止游戏。
/// </summary>
public sealed class HellForgeSessionService : IDisposable
{
    private readonly HellForgeService _cli;
    private readonly ILogger<HellForgeSessionService>? _logger;
    private readonly object _gate = new();
    private HellForgeSessionProgress _state = HellForgeSessionProgress.Idle;
    private CancellationTokenSource? _lifetime;
    private Process? _playProcess;
    private bool _disposed;

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
            if (_state.IsActive) throw new InvalidOperationException("已有 HellForge 会话正在运行，请先停止。");
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
        }

        _ = Task.Run(() => ObserveAsync(process, started, lifetime.Token));
        using var registration = launchCancellationToken.Register(() => started.TrySetCanceled());
        await started.Task;
    }

    /// <summary>停止当前会话并等待清理完成；无会话时抛出 CLI 的错误信息。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
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
            _state = status.Session.Running
                ? new HellForgeSessionProgress { Phase = HellForgeSessionPhase.Running, RunId = Path.GetFileName(status.Session.LastRun ?? "") }
                : new HellForgeSessionProgress
                {
                    Phase = HellForgeSessionProgress.PhaseFromStatus(status.Session.LastStatus),
                    RunId = Path.GetFileName(status.Session.LastRun ?? ""),
                    Failure = status.Session.LastFailure
                };
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
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
                    ApplyEvent(document.RootElement, started);
                }
                catch (JsonException)
                {
                    // CLI 偶尔混入非 JSON 行时跳过，不影响事件流。
                }
            }
            await process.WaitForExitAsync(cancellationToken);
            var stderr = (await stderrTask).Trim();
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

    private void ApplyEvent(JsonElement element, TaskCompletionSource started)
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
                    Notice = null
                });
                SessionStarted?.Invoke(this, EventArgs.Empty);
                started.TrySetResult();
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
                Update(state => state with
                {
                    Phase = finalStatus == "observed" ? HellForgeSessionPhase.Ended : HellForgeSessionPhase.Failed,
                    Failure = state.Failure
                });
                break;
        }
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
            // 只结束观察进程（play --wait），不停止游戏会话。
            // Kill(entireProcessTree) 在子进程已退出时抛 AggregateException（包裹 Win32Exception），须一并吞掉。
            try { _playProcess?.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
            _lifetime?.Dispose();
            _lifetime = null;
            _playProcess = null;
        }
    }
}