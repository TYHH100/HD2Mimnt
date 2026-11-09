using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginSDK;
using HD2Mimnt.UI.ViewModels;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// 声明式界面贡献：manifest 解析、安装校验、权限状态合并与界面更新应用。
/// </summary>
public class PluginUiContributionTests : IDisposable
{
    private readonly Mock<IPluginRepository> _pluginRepo = new();
    private readonly Mock<IPluginPermissionRepository> _permissionRepo = new();
    private readonly Mock<IPluginExecutionRepository> _executionRepo = new();
    private readonly Mock<IPluginHostManager> _hostManager = new();
    private readonly PluginService _service;
    private readonly string _directory;

    public PluginUiContributionTests()
    {
        _service = new PluginService(
            _pluginRepo.Object,
            _permissionRepo.Object,
            _executionRepo.Object,
            _hostManager.Object);

        _directory = Path.Combine(Path.GetTempPath(), "hd2mimnt-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Manifest_DeserializesDeclarativeUi()
    {
        const string json = """
        {
          // 注释与 camelCase 都应被接受
          "id": "com.test.ui", "name": "UI", "version": "1.0.0",
          "assembly": "Ui.dll", "entryType": "Ui.Plugin",
          "ui": {
            "pages": [{ "id": "home", "title": "首页", "blocks": [
              { "type": "markdown", "text": "**hi**" },
              { "type": "input", "input": { "id": "text", "multiline": true } }
            ]}],
            "dialogs": [{ "id": "quick", "title": "快", "blocks": [] }],
            "menus": [{ "id": "m1", "placement": "mainMenu", "label": "打开", "dialog": "quick" }],
            "themes": [{ "id": "t1", "name": "主题", "tokens": { "accent": "#4C8DFF" } }]
          }
        }
        """;

        var manifest = PluginManifestJson.Deserialize(json);

        Assert.NotNull(manifest);
        Assert.Single(manifest!.Ui.Pages);
        Assert.Equal(2, manifest.Ui.Pages[0].Blocks.Count);
        Assert.True(manifest.Ui.Pages[0].Blocks[1].Input!.Multiline);
        Assert.Equal("quick", manifest.Ui.Menus[0].Dialog);
        Assert.Equal("#4C8DFF", manifest.Ui.Themes[0].Tokens["accent"]);
        Assert.True(PluginCommandPlacements.IsMainMenu(manifest.Ui.Menus[0].Placement));
    }

    [Fact]
    public async Task ValidateManifestAsync_RejectsMenuWithoutTarget()
    {
        var manifest = new PluginManifest
        {
            Id = "com.test.bad",
            Name = "Bad",
            Version = "1.0.0",
            Assembly = "Bad.dll",
            EntryType = "Bad.Plugin"
        };
        manifest.Ui.Menus.Add(new PluginMenuContribution
        {
            Id = "broken",
            Placement = PluginCommandPlacements.MainMenu,
            Label = "坏菜单",
            Dialog = "does-not-exist"
        });

        var path = await WriteManifestAsync(manifest, "Bad.dll");

        var result = await _service.ValidateManifestAsync(path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("不存在"));
    }

    [Fact]
    public async Task GetUiContributionsAsync_MergesGrantedPermissions()
    {
        var manifest = new PluginManifest
        {
            Id = "com.test.ui2",
            Name = "界面插件",
            Version = "1.0.0",
            Assembly = "Ui2.dll",
            EntryType = "Ui2.Plugin",
            RequestedPermissions = { "ui.theme" }
        };
        manifest.Ui.Pages.Add(new PluginPageContribution { Id = "p", Title = "页面" });

        var path = await WriteManifestAsync(manifest, "Ui2.dll");

        _pluginRepo.Setup(r => r.GetEnabledAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Plugin>
            {
                new() { Id = manifest.Id, Name = manifest.Name, ManifestPath = path, PermissionMode = "Sandbox", Enabled = true }
            });
        _permissionRepo.Setup(r => r.GetByPluginIdAsync(manifest.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HD2Mimnt.Core.Entities.PluginPermission>
            {
                new() { Id = "1", PluginId = manifest.Id, Permission = "ui.theme", Granted = true }
            });

        var bundles = await _service.GetUiContributionsAsync();

        var bundle = Assert.Single(bundles);
        Assert.Single(bundle.Ui.Pages);
        Assert.True(bundle.HasPermission("ui.theme"));
        Assert.True(bundle.HasPermission(null));
    }

    [Fact]
    public void Surface_AppliesReplaceAndAppendUpdates()
    {
        var bundle = new PluginUiBundle
        {
            PluginId = "com.test.ui3",
            PluginName = "UI",
            Ui = new PluginUiContributions(),
            GrantedPermissions = Array.Empty<string>()
        };

        var surface = new PluginUiSurfaceViewModel(
            bundle, "page", "标题", null, null,
            new[] { new PluginUiBlock { Id = "result", Type = PluginUiBlockTypes.Text, Text = "旧" } },
            isPage: true,
            pluginService: null);

        surface.Apply(new PluginUiUpdate
        {
            Mode = PluginUiUpdateModes.Replace,
            TargetId = "result",
            Blocks = { new PluginUiBlock { Id = "result", Type = PluginUiBlockTypes.Markdown, Text = "新" } }
        });

        Assert.Single(surface.Blocks);
        Assert.Equal("新", surface.Blocks[0].Text);

        surface.Apply(new PluginUiUpdate
        {
            Mode = PluginUiUpdateModes.Append,
            Blocks = { new PluginUiBlock { Type = PluginUiBlockTypes.Text, Text = "追加" } }
        });

        Assert.Equal(2, surface.Blocks.Count);
        Assert.Equal("追加", surface.Blocks[1].Text);
    }

    private async Task<string> WriteManifestAsync(PluginManifest manifest, string assemblyName)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(manifest, PluginManifestJson.Options));
        await File.WriteAllTextAsync(Path.Combine(_directory, assemblyName), "assembly");
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
