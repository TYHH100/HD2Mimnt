using System.Diagnostics;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Worker;

/// <summary>
/// 把业务进度转发给主程序。部署/清理按文件回报很密集，按 100ms 节流，
/// 只保留最新值，避免 IPC 消息淹没管道。
/// </summary>
internal sealed class WorkerProgressReporter(
    IWorkerTaskHost host, CancellationToken cancellationToken, int intervalMilliseconds = 100)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long? _lastSent;
    private string? _lastStage;
    private bool _hasValue;
    private (double Progress, string Stage, string Item, string Message) _latest;

    public void Report(double progress, string stage, string? item = null, string? message = null)
    {
        _latest = (progress, stage, item ?? string.Empty, message ?? string.Empty);
        _hasValue = true;
        var now = _clock.ElapsedMilliseconds;
        // 阶段切换必须立即送达：长时间扫描/编译期间，界面不能停留在上一个阶段。
        // 同一阶段内仍按间隔节流，避免文件级回报淹没管道。
        var stageChanged = _lastStage != stage;
        if (!stageChanged && _lastSent is { } last && now - last < intervalMilliseconds) return;
        _lastStage = stage;
        _lastSent = now;
        Send();
    }

    /// <summary>发送最后一条被节流掉的状态（例如部署结束前的最终进度）。</summary>
    public void Flush()
    {
        if (_hasValue) Send();
    }

    private void Send() => host.SendProgressAsync(_latest.Progress, _latest.Stage, _latest.Item, _latest.Message,
        cancellationToken).GetAwaiter().GetResult();
}
