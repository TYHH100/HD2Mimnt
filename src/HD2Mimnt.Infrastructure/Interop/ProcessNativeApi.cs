using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HD2Mimnt.Infrastructure.Interop;

/// <summary>进程句柄、映像路径与 Affinity 的 Win32 绑定。</summary>
internal static class ProcessNativeApi
{
    public const uint ProcessQueryInformation = 0x0400;
    public const uint ProcessSetInformation = 0x0200;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>查询 + 设置所需的最小组合；设置值 0x0200 同时覆盖 SET_INFORMATION 与 SET_LIMITED_INFORMATION。</summary>
    public const uint QueryAndSetAccess = ProcessQueryLimitedInformation | ProcessSetInformation;

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

    public static bool TryOpenProcess(int processId, out IntPtr handle, out int error)
    {
        handle = OpenProcess(QueryAndSetAccess, false, processId);
        error = handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        return handle != IntPtr.Zero;
    }

    public static void CloseHandleSafe(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    public static bool TryGetImagePath(int processId, out string? path, out int error)
    {
        path = null;
        error = 0;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            var buffer = new StringBuilder(32768);
            var size = (uint)buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            path = buffer.ToString(0, (int)size);
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static (ulong ProcessMask, ulong SystemMask) GetAffinity(IntPtr process)
    {
        if (!GetProcessAffinityMask(process, out var processAffinity, out var systemAffinity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessAffinityMask failed.");

        return (processAffinity.ToUInt64(), systemAffinity.ToUInt64());
    }

    public static void SetAffinity(IntPtr process, ulong mask)
    {
        if (!SetProcessAffinityMask(process, new UIntPtr(mask)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetProcessAffinityMask failed.");
    }
}