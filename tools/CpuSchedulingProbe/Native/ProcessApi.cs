using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CpuSchedulingProbe.Native;

internal static class ProcessApi
{
    public const uint ProcessQueryInformation = 0x0400;
    public const uint ProcessSetInformation = 0x0200;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint ProcessSetLimitedInformation = 0x0200;
    public const uint ThreadQueryLimitedInformation = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessorNumber
    {
        public ushort Group;
        public byte Number;
        public byte Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(
        IntPtr process, out UIntPtr processAffinityMask, out UIntPtr systemAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr processAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll")]
    public static extern void GetCurrentProcessorNumberEx(out ProcessorNumber processorNumber);

    public static IntPtr OpenProcessHandle(uint access, int processId)
        => OpenProcess(access, false, processId);

    public static bool Close(IntPtr handle) => handle != IntPtr.Zero && CloseHandle(handle);

    public static IntPtr OpenThreadHandle(uint access, int threadId)
        => OpenThread(access, false, (uint)threadId);

    public static string? GetImagePath(int processId)
    {
        var handle = OpenProcessHandle(ProcessQueryLimitedInformation, processId);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(32768);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString(0, (int)size)
                : null;
        }
        finally
        {
            Close(handle);
        }
    }

    public static bool TryGetAffinity(IntPtr process, out ulong processMask, out ulong systemMask)
    {
        processMask = 0;
        systemMask = 0;
        if (!GetProcessAffinityMask(process, out var processAffinity, out var systemAffinity))
            return false;

        processMask = processAffinity.ToUInt64();
        systemMask = systemAffinity.ToUInt64();
        return true;
    }

    public static void SetAffinity(IntPtr process, ulong mask)
    {
        if (!SetProcessAffinityMask(process, new UIntPtr(mask)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetProcessAffinityMask failed.");
    }

    public static long TryGetStartTimeUtcTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return 0;
        }
    }

    public static string DescribeError(int error) => error switch
    {
        0 => "0 (no error)",
        5 => "5 (ERROR_ACCESS_DENIED)",
        87 => "87 (ERROR_INVALID_PARAMETER)",
        122 => "122 (ERROR_INSUFFICIENT_BUFFER)",
        _ => error.ToString()
    };
}