using System.Diagnostics;
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
    }

    public static string BuildCommand(string executable, string assembly)
    {
        if (executable.Contains('"') || assembly.Contains('"')) throw new ArgumentException("应用路径无效。");
        var arguments = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $" \"{assembly}\"" : "";
        return $"\"{executable}\"{arguments} \"%1\"";
    }

    // root 参数允许测试使用隔离子键，不改真实关联。只登记应用候选，不抢占 nxm 默认处理器，
    // 也不广播关联刷新：Windows 11 会把静默改写默认处理器视为关联劫持并批量恢复默认应用。
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool Register(RegistryKey root, string command, string? executable = null)
    {
        if (IsRegistrationCurrent(root, command, executable)) return false;
        const string progId = "HD2Mimnt.nxm";
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
        RemoveOwnedDefaultHandler(root);
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

        // 旧版本写过的自有默认处理器会被清理；其它管理器的默认关联不属于本应用的注册检查。
        using var handler = root.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
        return handler?.GetValue("") is not string existing || !IsOwnCommand(existing);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemoveOwnedDefaultHandler(RegistryKey root)
    {
        using var handler = root.OpenSubKey(@"Software\Classes\nxm\shell\open\command", writable: true);
        if (handler?.GetValue("") is not string existing || !IsOwnCommand(existing)) return;
        handler.DeleteValue("", false);
        using var scheme = root.OpenSubKey(@"Software\Classes\nxm", writable: true);
        if (scheme is null) return;
        if (scheme.GetValue("") is "URL:Nexus Mods Protocol") scheme.DeleteValue("", false);
        if (scheme.GetValue("URL Protocol") is string) scheme.DeleteValue("URL Protocol", false);
    }

    private static bool IsOwnCommand(string value)
    {
        var name = typeof(WebsiteDownloadLauncher).Assembly.GetName().Name;
        for (var index = 0; index < value.Length; index++)
        {
            var start = value.IndexOf('"', index);
            if (start < 0) return false;
            var end = value.IndexOf('"', start + 1);
            if (end < 0) return false;
            if (string.Equals(Path.GetFileNameWithoutExtension(value[(start + 1)..end]), name, StringComparison.OrdinalIgnoreCase))
                return true;
            index = end;
        }
        return false;
    }
}
