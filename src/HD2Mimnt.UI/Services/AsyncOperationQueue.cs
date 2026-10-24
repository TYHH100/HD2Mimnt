namespace HD2Mimnt.UI.Services;

/// <summary>串行执行事件触发的刷新，并提供退出时可等待的完成边界。</summary>
public sealed class AsyncOperationQueue
{
    private readonly object _sync = new();
    private Task _completion = Task.CompletedTask;

    public Task Enqueue(Func<Task> operation)
    {
        Task previous;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            previous = _completion;
            _completion = completion.Task;
        }
        _ = RunAsync(previous, operation, completion);
        return completion.Task;
    }

    private static async Task RunAsync(Task previous, Func<Task> operation, TaskCompletionSource completion)
    {
        try
        {
            try { await previous; } catch { /* 失败由完成任务报告，后续刷新仍可执行。 */ }
            await operation();
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    public async Task DrainAsync()
    {
        while (true)
        {
            Task current;
            lock (_sync) current = _completion;
            await current;
            lock (_sync) if (ReferenceEquals(current, _completion)) return;
        }
    }
}
