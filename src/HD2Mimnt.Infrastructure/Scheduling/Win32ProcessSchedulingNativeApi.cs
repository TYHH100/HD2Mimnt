using System.Diagnostics;
using HD2Mimnt.Infrastructure.Interop;

namespace HD2Mimnt.Infrastructure.Scheduling;

public sealed class Win32ProcessSchedulingNativeApi : IProcessSchedulingNativeApi
{
    public bool TryOpenProcess(int processId, out IntPtr handle, out int error)
        => ProcessNativeApi.TryOpenProcess(processId, out handle, out error);

    public void CloseHandle(IntPtr handle) => ProcessNativeApi.CloseHandleSafe(handle);

    public uint[] GetProcessDefaultCpuSets(IntPtr handle) => CpuSetNativeApi.GetProcessDefaultCpuSets(handle);

    public void SetProcessDefaultCpuSets(IntPtr handle, IReadOnlyCollection<uint> cpuSetIds)
        => CpuSetNativeApi.SetProcessDefaultCpuSets(handle, cpuSetIds);

    public (ulong ProcessMask, ulong SystemMask) GetProcessAffinityMask(IntPtr handle)
        => ProcessNativeApi.GetAffinity(handle);

    public void SetProcessAffinityMask(IntPtr handle, ulong mask) => ProcessNativeApi.SetAffinity(handle, mask);

    public long TryGetStartTimeUtcTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }
}