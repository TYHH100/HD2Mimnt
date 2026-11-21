using System.Runtime.InteropServices;

namespace HD2Mimnt.Cli;

/// <summary>
/// 以完全不继承句柄的方式启动分离进程。.NET 的 Process.Start（bInheritHandles=TRUE）会让
/// 子进程继承父进程所有可继承句柄——CLI 从 bash 管道/重定向启动时，serve 持有管道写端会
/// 让上游永远等不到 EOF。CreateProcessW 的 bInheritHandles=FALSE 从根上切断。
/// </summary>
internal static class DetachedProcess
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    public static bool Start(string exe, string arguments, string? workingDirectory)
    {
        var si = new STARTUPINFOW { Cb = Marshal.SizeOf<STARTUPINFOW>() };
        PROCESS_INFORMATION pi = default;
        try
        {
            return CreateProcessW(exe, $"\"{exe}\" {arguments}", IntPtr.Zero, IntPtr.Zero,
                bInheritHandles: false, CreateNoWindow | CreateUnicodeEnvironment,
                IntPtr.Zero, workingDirectory, ref si, out pi);
        }
        finally
        {
            // 进程/线程句柄用不到（分离式启动，不等退出）；不管成败都要关闭，避免泄漏内核句柄。
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int Cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
