using System.Security.Cryptography;
using System.Text;

namespace HD2Mimnt.Infrastructure.Archive;

public static class ArchivePasswordProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HD2Mimnt.ArchivePasswords.v1");

    public static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Archive password storage requires Windows.");
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Unprotect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Archive password storage requires Windows.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
