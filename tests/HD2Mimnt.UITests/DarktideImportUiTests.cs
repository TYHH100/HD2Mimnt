using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.Darktide;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels.Darktide;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Xunit;

namespace HD2Mimnt.UITests;

/// <summary>暗潮模组库的文件夹导入与“从游戏 mods 导入”入口。</summary>
public sealed class DarktideImportUiTests
{
    [AvaloniaFact]
    public async Task ImportFromGame_SingleInstalledMod_ImportsIntoLibraryWithoutDialog()
    {
        await using var scope = await TestScope.CreateAsync();
        var manager = scope.Services.GetRequiredService<DarktideManagerService>();
        var main = scope.Services.GetRequiredService<DarktideMainWindowViewModel>();
        await main.InitializeAsync();
        var library = main.Library;
        Assert.True(library.LoaderReady);
        Assert.Contains(library.Mods, mod => mod.InternalName == "solo" && mod.Installed && !mod.InLibrary);

        // 只有一个候选时不弹选择框，命令应直接完成收编。
        await library.ImportFromGameCommand.ExecuteAsync(null);

        var entry = (await manager.ScanAsync()).Mods.Single(mod => mod.InternalName == "solo");
        Assert.True(entry.InLibrary);
        Assert.True(entry.Registered);
        Assert.True(File.Exists(Path.Combine(scope.GameRoot, "mods", "solo", "solo.mod")));
        var archive = (await scope.Repository.GetLibraryAsync()).Single();
        Assert.True(File.Exists(archive.ArchivePath));
    }

    [AvaloniaFact]
    public async Task ImportFolder_WithPickedFolder_InstallsAndRegisters()
    {
        await using var scope = await TestScope.CreateAsync();
        var source = Path.Combine(scope.Root, "source", "FolderMod");
        WriteMod(source, "FolderMod");
        scope.Dialogs.FolderToPick = Path.Combine(scope.Root, "source");
        var main = scope.Services.GetRequiredService<DarktideMainWindowViewModel>();
        await main.InitializeAsync();
        var library = main.Library;

        await library.ImportFolderCommand.ExecuteAsync(null);

        var mod = (await scope.Manager.ScanAsync()).Mods.Single(item => item.InternalName == "FolderMod");
        Assert.True(mod.Installed);
        Assert.True(mod.Registered);
        Assert.True(mod.InLibrary);
        Assert.True(File.Exists(Path.Combine(scope.GameRoot, "mods", "FolderMod", "FolderMod.mod")));
        // 页面能正常加载新按钮所在的视图。
        var view = new DarktideLibraryView { DataContext = library };
        Assert.NotNull(view);
    }

    private static void WriteMod(string folder, string name)
    {
        Directory.CreateDirectory(Path.Combine(folder, "scripts", "mods", name));
        File.WriteAllText(Path.Combine(folder, name + ".mod"), "return { run = function() end }");
        File.WriteAllText(Path.Combine(folder, "scripts", "mods", name, name + ".lua"), "return {}");
        File.WriteAllText(Path.Combine(folder, "info.json"),
            $"{{\"name\":\"{name}\",\"version\":\"1.0.0\",\"author\":\"Tester\",\"description\":\"{name}\"}}");
    }

    private sealed class FakeDialogs : IDarktideDialogService
    {
        public string? FolderToPick { get; set; }
        public void SetOwner(Avalonia.Controls.Window owner) { }
        public Task<string?> PickArchiveAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string titleKey = "Darktide.Game.ChooseFolderTitle")
            => Task.FromResult(FolderToPick);
        public Task<string?> PickImageAsync() => Task.FromResult<string?>(null);
        public Task<string?> PromptAsync(string titleKey, string labelKey, string initialValue = "")
            => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<DarktideArchiveCandidate>?> PickCandidatesAsync(
            IReadOnlyList<DarktideArchiveCandidate> candidates, string allLabelKey = "Darktide.Library.InstallAllCandidates")
            => Task.FromResult<IReadOnlyList<DarktideArchiveCandidate>?>(candidates);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public void OpenFolder(string path) { }
        public void OpenFile(string path) { }
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-DarktideImport-{Guid.NewGuid():N}");
        public ServiceProvider Services { get; private set; } = null!;
        public string Root => _root;
        public string GameRoot => Path.Combine(_root, "game");
        public FakeDialogs Dialogs { get; } = new();
        public DarktideManagerService Manager => Services.GetRequiredService<DarktideManagerService>();
        public HD2Mimnt.Infrastructure.Repositories.DarktideRepository Repository
            => Services.GetRequiredService<HD2Mimnt.Infrastructure.Repositories.DarktideRepository>();

        public static async Task<TestScope> CreateAsync()
        {
            var scope = new TestScope();
            CreateGame(scope.GameRoot);
            WriteMod(Path.Combine(scope.GameRoot, "mods", "solo"), "solo");
            var originalData = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
            try
            {
                Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", Path.Combine(scope._root, "data"));
                var collection = new ServiceCollection();
                typeof(App).GetMethod("ConfigureServices", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(Avalonia.Application.Current, new object[] { collection });
                var dialogs = collection.Single(descriptor => descriptor.ServiceType == typeof(IDarktideDialogService));
                collection.Remove(dialogs);
                collection.AddSingleton<IDarktideDialogService>(scope.Dialogs);
                scope.Services = collection.BuildServiceProvider();
            }
            finally { Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", originalData); }

            await scope.Services.GetRequiredService<HD2Mimnt.Infrastructure.Data.HD2MimntDbContext>()
                .InitializeDatabaseAsync();
            await scope.Services.GetRequiredService<SettingsService>()
                .SetSettingAsync(DarktideSettingsKeys.GamePath, scope.GameRoot);
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
        Directory.CreateDirectory(Path.Combine(gameRoot, "tools"));
        Directory.CreateDirectory(Path.Combine(gameRoot, "mods", "base"));
        Directory.CreateDirectory(Path.Combine(gameRoot, "mods", "dmf"));
        File.WriteAllText(Path.Combine(gameRoot, "binaries", "Darktide.exe"), "");
        File.WriteAllText(Path.Combine(gameRoot, "binaries", "mod_loader"), "return {}");
        File.WriteAllText(Path.Combine(gameRoot, "mods", "base", "mod_manager.lua"), "return {}");
        File.WriteAllText(Path.Combine(gameRoot, "mods", "dmf", "dmf.mod"), "return {}");
        File.WriteAllText(Path.Combine(gameRoot, "tools", "dtkit-patch.exe"), "");
        File.WriteAllText(Path.Combine(gameRoot, "toggle_darktide_mods.bat"), "@echo off\r\n");
        File.WriteAllText(DarktidePaths.LoadOrderFile(gameRoot), "-- header\r\n", new UTF8Encoding(false));
    }
}