using System.Runtime;
using System.Runtime.InteropServices;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// 关闭预览后主动回收主进程内存：预览场景/贴图解码会在大对象堆留下数万到数百万字节的
/// 数组，普通 GC 不保证把页归还给系统。这里在后台线程做一次 LOH 压实 + 强制回收，
/// 再收缩工作集，让任务管理器里的占用尽快回落。
/// </summary>
public static class PreviewMemoryReclaimer
{
    private static int _scheduled;

    public static void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                CompactLargeObjectHeap();
                TrimWorkingSet();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Preview memory reclaim failed: {0}", ex.Message);
            }
            finally
            {
                Volatile.Write(ref _scheduled, 0);
            }
        });
    }

    /// <summary>
    /// 重新预览前回收上一代场景/贴图：只压实大对象堆，不收缩工作集（预览仍在进行，
    /// 收缩只会让下一次渲染重新缺页）。调用前必须已断开旧场景的引用。
    /// </summary>
    public static Task CompactLargeObjectHeapAsync()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try
            {
                CompactLargeObjectHeap();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Preview LOH compaction failed: {0}", ex.Message);
            }
            finally
            {
                Volatile.Write(ref _scheduled, 0);
            }
        });
    }

    private static void CompactLargeObjectHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void TrimWorkingSet()
    {
        // -1/-1 表示让系统按当前活动页重新裁剪工作集；失败不影响下次预览。
        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize,
        IntPtr maximumWorkingSetSize);
}
