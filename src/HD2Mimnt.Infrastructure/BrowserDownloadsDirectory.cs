using System.Runtime.InteropServices;

namespace HD2Mimnt.Infrastructure;

public static class BrowserDownloadsDirectory
{
    public static string GetPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path);
            try
            {
                if (result == 0 && Marshal.PtrToStringUni(path) is { Length: > 0 } value) return value;
            }
            finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
