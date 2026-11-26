using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Interop;
using Microsoft.Win32;

namespace HD2Mimnt.Infrastructure.Scheduling;

/// <summary>通过 GetSystemCpuSetInformation 读取拓扑，并把分类交给 Core 的纯函数。</summary>
public sealed class Win32CpuTopologyProvider : ICpuTopologyProvider
{
    public CpuTopology GetTopology()
        => CpuTopology.Analyze(CpuSetNativeApi.Enumerate(), ReadCpuName());

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}