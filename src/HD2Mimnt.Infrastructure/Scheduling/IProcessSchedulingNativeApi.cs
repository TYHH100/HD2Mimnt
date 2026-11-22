using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Scheduling;

/// <summary>调度控制器依赖的最小 Win32 表面；测试用内存实现替换。</summary>
public interface IProcessSchedulingNativeApi
{
    bool TryOpenProcess(int processId, out IntPtr handle, out int error);
    void CloseHandle(IntPtr handle);
    uint[] GetProcessDefaultCpuSets(IntPtr handle);
    void SetProcessDefaultCpuSets(IntPtr handle, IReadOnlyCollection<uint> cpuSetIds);

    /// <summary>按组掩码读取进程默认 CPU Sets；系统缺少该 API 时返回 false（error=127）。</summary>
    bool TryGetProcessDefaultCpuSetMasks(IntPtr handle, out ProcessorGroupMask[] masks, out int error);

    /// <summary>按组掩码设置进程默认 CPU Sets；空数组表示清除。系统缺少该 API 时返回 false（error=127）。</summary>
    bool TrySetProcessDefaultCpuSetMasks(IntPtr handle, ProcessorGroupMask[] masks, out int error);

    (ulong ProcessMask, ulong SystemMask) GetProcessAffinityMask(IntPtr handle);
    void SetProcessAffinityMask(IntPtr handle, ulong mask);
    long TryGetStartTimeUtcTicks(int processId);
}