using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HD2Mimnt.UI.Services;

public interface IWebsiteDownloadLauncher
{
    void Open(string url);
}

/// <summary>向 Windows 登记当前应用，然后打开平台文件网页。</summary>
public sealed class WebsiteDownloadLauncher : IWebsiteDownloadLauncher
{
    public void Open(string url)
    {
        RegisterCurrentApplication();
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static void RegisterCurrentApplication()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定应用执行路径。");
        Register(Registry.CurrentUser, BuildCommand(executable, typeof(App).Assembly.Location), executable);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    public static string BuildCommand(string executable, string assembly)
    {
        if (executable.Contains('"') || assembly.Contains('"')) throw new ArgumentException("应用路径无效。");
        var arguments = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $" \"{assembly}\"" : "";
        return $"\"{executable}\"{arguments} \"%1\"";
    }

    // root 参数允许测试使用隔离子键，不改真实关联。已有 nxm 默认处理器不被覆盖。
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void Register(RegistryKey root, string command, string? executable = null)
    {
        const string progId = "HD2Mimnt.nxm";
        using var previousHandler = root.OpenSubKey(@"Software\Classes\" + progId + @"\shell\open\command");
        var previousCommand = previousHandler?.GetValue("") as string;
        using (var application = root.CreateSubKey(@"Software\Classes\" + progId))
        {
            application.SetValue("", "HD2Mimnt Nexus Mods 下载链接");
            application.SetValue("URL Protocol", "");
            using var shell = application.CreateSubKey(@"shell\open\command");
            shell.SetValue("", command);
            using var details = application.CreateSubKey("Application");
            details.SetValue("ApplicationName", "HD2Mimnt");
            details.SetValue("ApplicationDescription", "Helldivers 2 模组管理器");
            if (executable != null)
            {
                using var icon = application.CreateSubKey("DefaultIcon");
                icon.SetValue("", $"\"{executable}\",0");
                details.SetValue("ApplicationIcon", $"\"{executable}\",0");
            }
        }
        using (var capabilities = root.CreateSubKey(@"Software\HD2Mimnt\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", "HD2Mimnt");
            capabilities.SetValue("ApplicationDescription", "Helldivers 2 模组管理器");
            using var associations = capabilities.CreateSubKey("URLAssociations");
            associations.SetValue("nxm", progId);
        }
        using (var applications = root.CreateSubKey(@"Software\RegisteredApplications"))
            applications.SetValue("HD2Mimnt", @"Software\HD2Mimnt\Capabilities");
        using (var candidates = root.CreateSubKey(@"Software\Classes\nxm\OpenWithProgids"))
            candidates.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        // dotnet.exe 是共享宿主，不能将它作为 HD2Mimnt 登记到应用选择器。
        if (executable != null && !Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            using var app = root.CreateSubKey(@"Software\Classes\Applications\" + Path.GetFileName(executable));
            app.SetValue("FriendlyAppName", "HD2Mimnt");
            using var appCommand = app.CreateSubKey(@"shell\open\command");
            appCommand.SetValue("", command);
            using var protocols = app.CreateSubKey("SupportedProtocols");
            protocols.SetValue("nxm", "");
        }
        using var existing = root.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
        using var machineDefault = Registry.LocalMachine.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
        var existingCommand = existing?.GetValue("") as string;
        if (existingCommand != null && existingCommand != previousCommand && existingCommand != command) return;
        if (existingCommand == null && machineDefault?.GetValue("") != null) return;
        using var protocol = root.CreateSubKey(@"Software\Classes\nxm");
        protocol.SetValue("", "URL:Nexus Mods Protocol");
        protocol.SetValue("URL Protocol", "");
        using var handler = protocol.CreateSubKey(@"shell\open\command");
        handler.SetValue("", command);
    }
}
