namespace HD2Mimnt.Infrastructure.Scheduling;

/// <summary>调度控制器依赖的最小 Win32 表面；测试用内存实现替换。</summary>
public interface IProcessSchedulingNativeApi
{
    bool TryOpenProcess(int processId, out IntPtr handle, out int error);
    void CloseHandle(IntPtr handle);
    uint[] GetProcessDefaultCpuSets(IntPtr handle);
    void SetProcessDefaultCpuSets(IntPtr handle, IReadOnlyCollection<uint> cpuSetIds);
    (ulong ProcessMask, ulong SystemMask) GetProcessAffinityMask(IntPtr handle);
    void SetProcessAffinityMask(IntPtr handle, ulong mask);
    long TryGetStartTimeUtcTicks(int processId);
}