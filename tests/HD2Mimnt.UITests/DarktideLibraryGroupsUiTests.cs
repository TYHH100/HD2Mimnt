using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.Darktide;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels.Darktide;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Xunit;

namespace HD2Mimnt.UITests;

/// <summary>暗潮模组库的分组与顺序行为：只组织显示，不改加载顺序文件。</summary>
public sealed class DarktideLibraryGroupsUiTests
{
    [AvaloniaFact]
    public async Task LibraryGroups_OrderCollapseAndRemoveWithoutTouchingLoadOrder()
    {
        await using var scope = await TestScope.CreateAsync();
        var manager = scope.Services.GetRequiredService<DarktideManagerService>();
        var main = scope.Services.GetRequiredService<DarktideMainWindowViewModel>();
        await main.InitializeAsync();
        var library = main.Library;

        // 库列表按稳定名称序，不跟随 mod_load_order.txt 里的位置。
        Assert.Equal(new[] { "arsmorate", "bolt", "crucible", "eica", "plasma", "unregistered" },
            library.Mods.Select(item => item.InternalName));

        var sequence = library.Mods.Select(item => item.InternalName).ToList();
        await manager.AddModSeparatorAsync("武器外观", 0, sequence, "#B34A4A");
        await manager.AddModSeparatorAsync("界面与 HUD", 4, sequence);
        await library.RefreshAsync(default);

        Assert.Equal(new object[] { "武器外观", "arsmorate", "bolt", "crucible", "eica", "界面与 HUD", "plasma", "unregistered" },
            TitleOf(library));
        // 未启用模组保持原位，只是开关关闭，不沉底。
        var unregistered = library.Mods.Single(item => item.InternalName == "unregistered");
        Assert.False(unregistered.Registered);
        Assert.True(unregistered.IsRegisteredOff);

        var first = library.Rows.OfType<DarktideSeparatorRowViewModel>().Single(row => row.Title == "武器外观");
        await library.ToggleGroupCollapseCommand.ExecuteAsync(first);
        Assert.Equal(new object[] { "武器外观", "界面与 HUD", "plasma", "unregistered" }, TitleOf(library));
        Assert.Equal(4, library.Rows.OfType<DarktideSeparatorRowViewModel>()
            .Single(row => row.Title == "武器外观").MemberCount);

        // 删除首组后成员回到未分组区，仍排在下一组之前。
        await manager.RemoveModSeparatorAsync(first.Id, sequence);
        await library.RefreshAsync(default);
        Assert.Equal(new object[] { "arsmorate", "bolt", "crucible", "eica", "界面与 HUD", "plasma", "unregistered" },
            TitleOf(library));
        Assert.Equal(new[] { "dmf", "bolt", "crucible", "eica", "plasma", "arsmorate" },
            (await manager.ScanAsync()).ActiveOrder);
    }

    private static object[] TitleOf(DarktideLibraryViewModel library) => library.Rows.Select(row => row switch
    {
        DarktideSeparatorRowViewModel separator => separator.Title,
        DarktideModItemViewModel mod => mod.InternalName,
        _ => throw new InvalidOperationException($"未知行类型 {row.GetType().Name}")
    }).Cast<object>().ToArray();

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-DarktideGroups-{Guid.NewGuid():N}");
        public ServiceProvider Services { get; private set; } = null!;

        public static async Task<TestScope> CreateAsync()
        {
            var scope = new TestScope();
            var gameRoot = Path.Combine(scope._root, "game");
            CreateGame(gameRoot);
            var originalData = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
            try
            {
                Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", Path.Combine(scope._root, "data"));
                var collection = new ServiceCollection();
                typeof(App).GetMethod("ConfigureServices", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(Avalonia.Application.Current, new object[] { collection });
                scope.Services = collection.BuildServiceProvider();
            }
            finally { Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", originalData); }

            await scope.Services.GetRequiredService<HD2Mimnt.Infrastructure.Data.HD2MimntDbContext>()
                .InitializeDatabaseAsync();
            await scope.Services.GetRequiredService<SettingsService>()
                .SetSettingAsync(DarktideSettingsKeys.GamePath, gameRoot);
            return scope;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private static void CreateGame(string gameRoot)
    {
        Directory.CreateDirectory(Path.Combine(gameRoot, "binaries"));
        Directory.CreateDirectory(Path.Combine(gameRoot, "bundle"));
        Directory.CreateDirectory(Path.Combine(gameRoot, "mods", "base"));
        Directory.CreateDirectory(Path.Combine(gameRoot, "mods", "dmf"));
        File.WriteAllText(Path.Combine(gameRoot, "binaries", "Darktide.exe"), "");
        File.WriteAllText(Path.Combine(gameRoot, "binaries", "mod_loader"), "return {}");
        File.WriteAllText(Path.Combine(gameRoot, "mods", "base", "mod_manager.lua"), "return {}");
        File.WriteAllText(Path.Combine(gameRoot, "mods", "dmf", "dmf.mod"), "return {}");
        AddMod(gameRoot, "bolt");
        AddMod(gameRoot, "crucible");
        AddMod(gameRoot, "eica");
        AddMod(gameRoot, "plasma");
        AddMod(gameRoot, "arsmorate");
        AddMod(gameRoot, "unregistered");
        File.WriteAllText(DarktidePaths.LoadOrderFile(gameRoot),
            "dmf\nbolt\ncrucible\neica\nplasma\narsmorate\n", new UTF8Encoding(false));
    }

    private static void AddMod(string gameRoot, string name)
    {
        var folder = Path.Combine(gameRoot, "mods", name);
        Directory.CreateDirectory(Path.Combine(folder, "scripts", "mods", name));
        File.WriteAllText(Path.Combine(folder, name + ".mod"), "return { run = function() end }");
        File.WriteAllText(Path.Combine(folder, "scripts", "mods", name, name + ".lua"), "return {}");
        File.WriteAllText(Path.Combine(folder, "info.json"),
            $"{{\"name\":\"{name}\",\"version\":\"1.0.0\",\"author\":\"Tester\",\"description\":\"{name}\"}}");
    }
}
