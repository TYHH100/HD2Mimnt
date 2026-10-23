using Avalonia.Threading;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.UI.Services;

/// <summary>任务中心拥有取消和完成边界，UI 续体仍回到 Dispatcher，进度写库在后台串行执行。</summary>
public sealed class PlatformDownloadTaskRunner(TaskService tasks)
{
    public Task RunAsync(string name, Func<CancellationToken, Task> action,
        Func<TaskProgress> snapshot, CancellationToken cancellationToken)
        => tasks.RunTaskAsync(name, "PlatformDownload", async (progress, token) =>
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var operation = Dispatcher.UIThread.InvokeAsync(() => action(operationCancellation.Token));
            TaskProgress? previous = null;
            try
            {
                while (!operation.IsCompleted)
                {
                    var current = await Dispatcher.UIThread.InvokeAsync(snapshot);
                    if (previous == null || current.Progress != previous.Progress || current.Stage != previous.Stage ||
                        current.CurrentItem != previous.CurrentItem || current.Message != previous.Message)
                    {
                        progress.Report(current);
                        previous = current;
                    }
                    await Task.WhenAny(operation, Task.Delay(250));
                }
                await operation;
                progress.Report(await Dispatcher.UIThread.InvokeAsync(snapshot));
            }
            catch
            {
                operationCancellation.Cancel();
                try { await operation; } catch { /* 保留任务中心或原操作首先报告的错误。 */ }
                throw;
            }
        }, cancellationToken);
}
