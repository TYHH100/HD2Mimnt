using HD2Mimnt.UI.Services;
using Microsoft.Win32;

namespace HD2Mimnt.Tests.UI;

public sealed class BrowserProtocolHostTests
{
    private const string Link = "nxm://helldivers2/mods/123/files/456?key=test-key&expires=4102444800&user_id=789";

    [Fact]
    public async Task ProtocolArguments_ShouldQueueUntilReadyAndForwardToOriginalInstance()
    {
        var scope = "protocol-test-" + Guid.NewGuid();
        using (var primary = new BrowserProtocolHost(scope))
        {
            Assert.True(primary.IsPrimary);
            primary.Receive(BrowserProtocolHost.GetProtocolArgument(new[] { "--other", Link }));
            using var secondary = new BrowserProtocolHost(scope);
            Assert.False(secondary.IsPrimary);
            Assert.True(await secondary.ForwardAsync(Link));
            var received = new List<string>();
            primary.SetReceiver(value => { lock (received) received.Add(value); });
            Assert.Equal(new[] { Link, Link }, received);
            Assert.True(await secondary.ForwardAsync(""));
            Assert.Equal("", received[^1]);
            Assert.False(await secondary.ForwardAsync("https://example.com"));
            Assert.Empty(BrowserProtocolHost.GetProtocolArgument(new[] { "nxm://" + new string('x', 9000) }));
        }
        using var restarted = new BrowserProtocolHost(scope);
        Assert.True(restarted.IsPrimary);
    }

    [Fact]
    public void Registration_WhenCurrent_ShouldSkipWritesAndRepairMissingEntries()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = @"Software\HD2Mimnt-Protocol-Test-" + Guid.NewGuid().ToString("N");
        const string executable = @"C:\Mod Tools\HD2Mimnt.exe";
        var command = WebsiteDownloadLauncher.BuildCommand(executable, "");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            using (var existing = root.CreateSubKey(@"Software\Classes\nxm\shell\open\command"))
                existing.SetValue("", "existing-manager %1");
            Assert.True(WebsiteDownloadLauncher.Register(root, command, executable));
            // 只读根键确保重复检查不会尝试 CreateSubKey/SetValue。
            using (var readOnly = Registry.CurrentUser.OpenSubKey(path, writable: false))
                Assert.False(WebsiteDownloadLauncher.Register(readOnly!, command, executable));
            root.DeleteSubKeyTree(@"Software\HD2Mimnt\Capabilities\URLAssociations");
            Assert.True(WebsiteDownloadLauncher.Register(root, command, executable));
            Assert.False(WebsiteDownloadLauncher.Register(root, command, executable));
            using var handler = root.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
            Assert.Equal("existing-manager %1", handler!.GetValue(""));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    [Fact]
    public void Registration_ShouldOfferApplicationWithoutOverwritingAnotherNxmHandler()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = @"Software\HD2Mimnt-Protocol-Test-" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            using (var existing = root.CreateSubKey(@"Software\Classes\nxm\shell\open\command"))
                existing.SetValue("", "existing-manager %1");
            var command = WebsiteDownloadLauncher.BuildCommand(@"C:\Mod Tools\HD2Mimnt.exe", "");
            WebsiteDownloadLauncher.Register(root, command, @"C:\Mod Tools\HD2Mimnt.exe");
            using var candidates = root.OpenSubKey(@"Software\Classes\nxm\OpenWithProgids");
            Assert.Contains("HD2Mimnt.nxm", candidates!.GetValueNames());
            using var appEntry = root.OpenSubKey(@"Software\Classes\Applications\HD2Mimnt.exe");
            Assert.Equal("HD2Mimnt", appEntry!.GetValue("FriendlyAppName"));
            using var protocolEntry = appEntry.OpenSubKey("SupportedProtocols");
            Assert.Contains("nxm", protocolEntry!.GetValueNames());
            using var appCommand = appEntry.OpenSubKey(@"shell\open\command");
            Assert.Equal(command, appCommand!.GetValue(""));
            using var association = root.OpenSubKey(@"Software\HD2Mimnt\Capabilities\URLAssociations");
            Assert.Equal("HD2Mimnt.nxm", association!.GetValue("nxm"));
            using var handler = root.OpenSubKey(@"Software\Classes\HD2Mimnt.nxm\shell\open\command");
            Assert.Equal("\"C:\\Mod Tools\\HD2Mimnt.exe\" \"%1\"", handler!.GetValue(""));
            using var existingHandler = root.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
            Assert.Equal("existing-manager %1", existingHandler!.GetValue(""));
            // 当前用户先前已选择本应用时，移动后的执行路径应随注册更新。
            using (var owned = root.OpenSubKey(@"Software\Classes\nxm\shell\open\command", writable: true))
                owned!.SetValue("", command);
            var movedCommand = WebsiteDownloadLauncher.BuildCommand(@"C:\New Folder\HD2Mimnt.exe", "");
            Assert.True(WebsiteDownloadLauncher.Register(root, movedCommand, @"C:\New Folder\HD2Mimnt.exe"));
            Assert.False(WebsiteDownloadLauncher.Register(root, movedCommand, @"C:\New Folder\HD2Mimnt.exe"));
            Assert.Equal(movedCommand, existingHandler.GetValue(""));
            Assert.Equal(movedCommand, handler.GetValue(""));
            Assert.Equal(movedCommand, appCommand.GetValue(""));
            using var icon = root.OpenSubKey(@"Software\Classes\HD2Mimnt.nxm\DefaultIcon");
            Assert.Equal("\"C:\\New Folder\\HD2Mimnt.exe\",0", icon!.GetValue(""));
            Assert.Equal("\"C:\\dotnet.exe\" \"C:\\Mod Tools\\HD2Mimnt.UI.dll\" \"%1\"",
                WebsiteDownloadLauncher.BuildCommand(@"C:\dotnet.exe", @"C:\Mod Tools\HD2Mimnt.UI.dll"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }
}
