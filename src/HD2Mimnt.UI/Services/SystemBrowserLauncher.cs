using System.Diagnostics;

namespace HD2Mimnt.UI.Services;

public interface IBrowserLauncher { void Open(string url); }

/// <summary>Login and verification stay in the system browser. No browser credentials are read.</summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
