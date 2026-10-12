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
        if (Register(Registry.CurrentUser, BuildCommand(executable, typeof(App).Assembly.Location), executable))
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
    public static bool Register(RegistryKey root, string command, string? executable = null)
    {
        if (IsRegistrationCurrent(root, command, executable)) return false;
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
        if (existingCommand != null && existingCommand != previousCommand && existingCommand != command) return true;
        if (existingCommand == null && machineDefault?.GetValue("") != null) return true;
        using var protocol = root.CreateSubKey(@"Software\Classes\nxm");
        protocol.SetValue("", "URL:Nexus Mods Protocol");
        protocol.SetValue("URL Protocol", "");
        using var handler = protocol.CreateSubKey(@"shell\open\command");
        handler.SetValue("", command);
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsRegistrationCurrent(RegistryKey root, string command, string? executable)
    {
        const string applicationPath = @"Software\Classes\HD2Mimnt.nxm";
        bool Matches(string path, string name, string expected)
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) is string value && value == expected;
        }

        if (!Matches(applicationPath, "", "HD2Mimnt Nexus Mods 下载链接") ||
            !Matches(applicationPath, "URL Protocol", "") ||
            !Matches(applicationPath + @"\shell\open\command", "", command) ||
            !Matches(applicationPath + @"\Application", "ApplicationName", "HD2Mimnt") ||
            !Matches(applicationPath + @"\Application", "ApplicationDescription", "Helldivers 2 模组管理器") ||
            !Matches(@"Software\HD2Mimnt\Capabilities", "ApplicationName", "HD2Mimnt") ||
            !Matches(@"Software\HD2Mimnt\Capabilities", "ApplicationDescription", "Helldivers 2 模组管理器") ||
            !Matches(@"Software\HD2Mimnt\Capabilities\URLAssociations", "nxm", "HD2Mimnt.nxm") ||
            !Matches(@"Software\RegisteredApplications", "HD2Mimnt", @"Software\HD2Mimnt\Capabilities"))
            return false;

        using var candidates = root.OpenSubKey(@"Software\Classes\nxm\OpenWithProgids");
        if (candidates?.GetValue("HD2Mimnt.nxm") is not byte[] { Length: 0 }) return false;
        if (executable != null)
        {
            var icon = $"\"{executable}\",0";
            if (!Matches(applicationPath + @"\DefaultIcon", "", icon) ||
                !Matches(applicationPath + @"\Application", "ApplicationIcon", icon)) return false;
            if (!Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var appPath = @"Software\Classes\Applications\" + Path.GetFileName(executable);
                if (!Matches(appPath, "FriendlyAppName", "HD2Mimnt") ||
                    !Matches(appPath + @"\shell\open\command", "", command) ||
                    !Matches(appPath + @"\SupportedProtocols", "nxm", "")) return false;
            }
        }

        // 其它管理器的默认关联不属于本应用的注册完整性检查。
        using var handler = root.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
        if (handler?.GetValue("") != null) return true;
        using var machineHandler = Registry.LocalMachine.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
        return machineHandler?.GetValue("") != null;
    }
}
