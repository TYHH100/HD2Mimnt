using System;
using System.Threading.Tasks;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// async void 事件处理器与 fire-and-forget 任务的统一安全包装：
/// 取消视为正常结束，其余异常经 <see cref="CrashGuard"/> 记录并通知，
/// 防止事件处理器中的未捕获异常直接崩溃。
/// </summary>
public static class SafeAsync
{
    /// <summary>替换 async void 处理器主体：SafeAsync.Run(() => vm.ImportAsync(paths), "导入");</summary>
    public static async void Run(Func<Task> operation, string errorContext = "操作失败")
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashGuard.Report(ex, errorContext); }
    }

    /// <summary>观察 fire-and-forget 任务（替换裸 <c>_ = SomeAsync()</c>），异常不再无人观察。</summary>
    public static void FireAndForget(Task task, string errorContext = "后台操作")
    {
        _ = WatchAsync(task, errorContext);
    }

    private static async Task WatchAsync(Task task, string errorContext)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashGuard.Report(ex, errorContext); }
    }
}
