using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.Darktide;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Darktide;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Darktide;

public sealed class DarktideManagerIntegrationTests
{
    [Fact]
    public async Task Scan_ReportsDependencyAndRegistrationProblems()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        env.AddMod("Alpha");
        env.AddMod("Beta", required: new[] { "Alpha" });
        env.AddMod("Disabled", register: false);
        env.WriteLoadOrder("-- header\r\nBeta\r\nAlpha\r\nAlpha\r\nMissing\r\n-- Disabled\r\n");
        using var context = await Context.CreateAsync(env);

        var scan = await context.Manager.ScanAsync();

        Assert.Equal(DarktideGameStatus.LoaderInstalled, scan.Game.Status);
        Assert.Contains(scan.Mods, mod => mod.InternalName == "Disabled" && mod.Installed && !mod.Registered);
        Assert.Contains(scan.Mods, mod => mod.InternalName == "Missing" && !mod.Installed && mod.Registered);
        Assert.Contains(scan.Diagnostics, item => item.Kind == DarktideIssueKind.DependencyOrder
                                                  && item.ModName == "Beta" && item.RelatedName == "Alpha");
        Assert.Contains(scan.Diagnostics, item => item.Kind == DarktideIssueKind.DuplicateRegistration
                                                  && item.ModName == "Alpha");
        Assert.Contains(scan.Diagnostics, item => item.Kind == DarktideIssueKind.MissingRegisteredMod
                                                  && item.ModName == "Missing");
        Assert.DoesNotContain(scan.Diagnostics, item => item.Kind == DarktideIssueKind.DmfMissing);
    }

    [Fact]
    public async Task Scan_OrdersLibraryByNameAndKeepsItStableAcrossRegistrationChanges()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        env.AddMod("Zeta");
        env.AddMod("Alpha");
        env.AddMod("Beta");
        env.AddMod("Mid", register: false);
        // 加载顺序文件里的位置与名称顺序刻意错开：库列表不得跟随它。
        env.WriteLoadOrder("Zeta\r\nAlpha\r\nBeta\r\n");
        using var context = await Context.CreateAsync(env);

        var scan = await context.Manager.ScanAsync();

        Assert.Equal(new[] { "Alpha", "Beta", "Mid", "Zeta" }, scan.Mods.Select(mod => mod.InternalName));
        Assert.Equal(new[] { "Zeta", "Alpha", "Beta" }, scan.ActiveOrder);

        // 开关登记不改变库列表的身份顺序，分组位点才不会被启停带偏。
        await context.Manager.SetEnabledAsync("Alpha", false);
        var afterDisable = await context.Manager.ScanAsync();
        Assert.Equal(new[] { "Alpha", "Beta", "Mid", "Zeta" }, afterDisable.Mods.Select(mod => mod.InternalName));
        Assert.Equal(new[] { "Zeta", "Beta" }, afterDisable.ActiveOrder);
    }

    [Fact]
    public async Task ModSeparators_FreezeBoundaryOnCollapseAndAbsorbOnRemoval()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        using var context = await Context.CreateAsync(env);
        var sequence = new[] { "a", "b", "c", "d" };

        await context.Manager.AddModSeparatorAsync("武器", 0, sequence);
        await context.Manager.AddModSeparatorAsync("护甲", 2, sequence);
        var added = await context.Manager.GetModSeparatorsAsync();
        Assert.Equal(new[] { "武器", "护甲" }, added.Select(item => item.Name));
        Assert.Equal(new int?[] { null, null }, added.Select(item => item.MemberCount));

        // 收起时把“到下一个分隔符为止”的隐式边界固化成成员数。
        var armour = added.Single(item => item.Name == "武器");
        await context.Manager.SetModSeparatorCollapsedAsync(armour.Id, true, sequence);
        var collapsed = await context.Manager.GetModSeparatorsAsync();
        Assert.Equal(2, collapsed.Single(item => item.Name == "武器").MemberCount);

        // 删除后一组时，其成员并入已有显式边界的上一组。
        var second = collapsed.Single(item => item.Name == "护甲");
        await context.Manager.RemoveModSeparatorAsync(second.Id, sequence);
        var afterRemove = Assert.Single(await context.Manager.GetModSeparatorsAsync());
        Assert.Equal("武器", afterRemove.Name);
        Assert.Equal(4, afterRemove.MemberCount);
        Assert.True(afterRemove.IsCollapsed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Manager.AddModSeparatorAsync("武器", 1, sequence));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            context.Manager.AddModSeparatorAsync("颜色", 0, sequence, "red"));
    }

    [Fact]
    public async Task SetEnabled_EditsOnlyTargetRegistrationAndPreservesComments()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        env.AddMod("Alpha", register: false);
        env.WriteLoadOrder("-- header\r\n-- Alpha\r\n-- footer\r\n");
        using var context = await Context.CreateAsync(env);

        await context.Manager.SetEnabledAsync("Alpha", true);
        var enabled = DarktideLoadOrderDocument.Load(DarktidePaths.LoadOrderFile(env.GameRoot));
        Assert.True(enabled.IsEnabled("Alpha"));
        var enabledText = await File.ReadAllTextAsync(DarktidePaths.LoadOrderFile(env.GameRoot));
        Assert.Contains("-- header", enabledText);
        Assert.Contains("-- footer", enabledText);

        await context.Manager.SetEnabledAsync("Alpha", false);
        var disabled = DarktideLoadOrderDocument.Load(DarktidePaths.LoadOrderFile(env.GameRoot));
        Assert.False(disabled.IsEnabled("Alpha"));
        Assert.Contains("-- Alpha", await File.ReadAllTextAsync(DarktidePaths.LoadOrderFile(env.GameRoot)));
    }

    [Fact]
    public async Task Import_InstallsRegistersUpdatesAndUninstalls()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        using var context = await Context.CreateAsync(env);
        var zip = env.CreateModPackage("NewMod.zip", "NewMod", version: "1.0.0");
        var inspection = context.Manager.InspectArchive(zip);
        var candidate = Assert.Single(inspection.Candidates);

        var installed = await context.Manager.ImportAsync(zip, candidate, allowReplace: false, register: true);

        Assert.True(installed.Registered);
        Assert.True(Directory.Exists(Path.Combine(env.GameRoot, "mods", "NewMod")));
        var document = DarktideLoadOrderDocument.Load(DarktidePaths.LoadOrderFile(env.GameRoot));
        Assert.True(document.IsEnabled("NewMod"));
        var library = await context.Repository.GetLibraryAsync();
        var entry = Assert.Single(library);
        Assert.Equal("NewMod", entry.InternalName);
        Assert.True(File.Exists(entry.ArchivePath));

        await Assert.ThrowsAsync<DarktideInstallConflictException>(() =>
            context.Manager.ImportAsync(zip, candidate, allowReplace: false, register: true));

        var updateZip = env.CreateModPackage("NewMod-2.zip", "NewMod", version: "2.0.0");
        var updateCandidate = Assert.Single(context.Manager.InspectArchive(updateZip).Candidates);
        var updated = await context.Manager.ImportAsync(updateZip, updateCandidate, allowReplace: true, register: true);
        Assert.True(updated.ReplacedExisting);
        document = DarktideLoadOrderDocument.Load(DarktidePaths.LoadOrderFile(env.GameRoot));
        Assert.Equal(1, document.CountActive("NewMod"));
        Assert.Contains("2.0.0", await File.ReadAllTextAsync(
            Path.Combine(env.GameRoot, "mods", "NewMod", "info.json")));

        await context.Manager.UninstallAsync("NewMod", removeRegistration: true, deleteLibraryArchive: true);
        Assert.False(Directory.Exists(Path.Combine(env.GameRoot, "mods", "NewMod")));
        Assert.Empty(await context.Repository.GetLibraryAsync());
        document = DarktideLoadOrderDocument.Load(DarktidePaths.LoadOrderFile(env.GameRoot));
        Assert.False(document.IsEnabled("NewMod"));
    }

    [Fact]
    public async Task Profiles_SaveApplyAndReportMissingMods()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        env.AddMod("Alpha");
        env.AddMod("Beta");
        using var context = await Context.CreateAsync(env);

        await context.Manager.CreateOrSaveProfileAsync("Main", overwrite: false);
        await context.Manager.SetEnabledAsync("Beta", false);
        var preview = await context.Manager.PreviewApplyProfileAsync("Main");
        Assert.Contains("Beta", preview.ToEnable);

        var applied = await context.Manager.ApplyProfileAsync("Main");
        Assert.Empty(applied.Missing);
        var document = DarktideLoadOrderDocument.Load(DarktidePaths.LoadOrderFile(env.GameRoot));
        Assert.True(document.IsEnabled("Beta"));

        Directory.Delete(Path.Combine(env.GameRoot, "mods", "Beta"), recursive: true);
        var missingPreview = await context.Manager.PreviewApplyProfileAsync("Main");
        Assert.Contains("Beta", missingPreview.Missing);
        var missingApplied = await context.Manager.ApplyProfileAsync("Main");
        Assert.Contains("Beta", missingApplied.Missing);
        Assert.True(Directory.Exists(Path.Combine(env.GameRoot, "mods", "Alpha")));

        var profiles = await context.Manager.GetProfilesAsync();
        Assert.True(profiles.Single(profile => profile.Name == "Main").IsActive);
        await context.Manager.DeleteProfileAsync("Main");
        Assert.Empty(await context.Manager.GetProfilesAsync());
    }

    [Fact]
    public async Task Profiles_WithDuplicateRegistrations_AreBlockedWithoutChangingFile()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        env.AddMod("Alpha");
        env.WriteLoadOrder("Alpha\r\nAlpha\r\n");
        using var context = await Context.CreateAsync(env);
        await context.Repository.UpsertProfileAsync(new DarktideProfile("Dup", DateTime.UtcNow, DateTime.UtcNow,
            false, new[] { new DarktideProfileEntry("Alpha", true, 0) }));
        var before = await File.ReadAllBytesAsync(DarktidePaths.LoadOrderFile(env.GameRoot));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Manager.ApplyProfileAsync("Dup"));
        Assert.Contains("重复登记", error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(DarktidePaths.LoadOrderFile(env.GameRoot)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Manager.CreateOrSaveProfileAsync("New", false));
    }

    [Fact]
    public async Task ImportDmf_InstallsFrameworkWithoutRegistrationOrLibraryEntry()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        Directory.Delete(Path.Combine(env.GameRoot, "mods", "dmf"), recursive: true);
        using var context = await Context.CreateAsync(env);
        var zip = env.CreateModPackage("DMF.zip", "dmf");
        var candidate = Assert.Single(context.Manager.InspectArchive(zip).Candidates);

        var result = await context.Manager.ImportAsync(zip, candidate, allowReplace: false, register: true);

        Assert.True(File.Exists(DarktidePaths.DmfMod(env.GameRoot)));
        Assert.False(result.Registered);
        Assert.Empty(await context.Repository.GetLibraryAsync());
        var document = DarktideLoadOrderDocument.Load(DarktidePaths.LoadOrderFile(env.GameRoot));
        Assert.False(document.IsEnabled("dmf"));
        Assert.True(context.Manager.GetGameState().HasDmf);
        await Assert.ThrowsAsync<DarktideInstallConflictException>(() =>
            context.Manager.ImportAsync(zip, candidate, allowReplace: false, register: true));
        var replaced = await context.Manager.ImportAsync(zip, candidate, allowReplace: true, register: true);
        Assert.False(replaced.Registered);
    }

    [Fact]
    public async Task Import_RejectsPackageThatNeedsItsOwnInstaller()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        using var context = await Context.CreateAsync(env);
        var zip = env.CreateMultiModPackage("custom.zip", new[]
        {
            ("CustomAssets", Array.Empty<string>()),
            ("PlayerModelHub", new[] { "CustomAssets" })
        }, withNativeExtras: true);
        var candidates = context.Manager.InspectArchive(zip).Candidates;
        var before = await File.ReadAllBytesAsync(DarktidePaths.LoadOrderFile(env.GameRoot));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Manager.ImportAsync(zip, candidates[0], allowReplace: false, register: true));

        Assert.Contains("作者自己的安装流程", error.Message);
        Assert.False(Directory.Exists(Path.Combine(env.GameRoot, "mods", "CustomAssets")));
        Assert.Equal(before, await File.ReadAllBytesAsync(DarktidePaths.LoadOrderFile(env.GameRoot)));
        Assert.Empty(await context.Repository.GetLibraryAsync());
    }

    [Fact]
    public async Task ImportBundle_InstallsEveryModAndOrdersByDependencies()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        env.AddMod("ExistingMod");
        using var context = await Context.CreateAsync(env);
        var zip = env.CreateMultiModPackage("bundle.zip", new[]
        {
            ("CustomAssets", Array.Empty<string>()),
            ("ModelReplacementRegistry", Array.Empty<string>()),
            ("PlayerModelHub", new[] { "CustomAssets", "ModelReplacementRegistry" })
        });
        var candidates = context.Manager.InspectArchive(zip).Candidates;
        // 故意按依赖的反序提交，验证登记顺序由 info.json 决定而不是压缩包顺序。
        var reversed = new[] { candidates[2], candidates[1], candidates[0] };

        var results = await context.Manager.ImportBundleAsync(zip, reversed, allowReplace: false);

        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.True(result.Registered));
        var order = DarktideLoadOrderDocument.Load(DarktidePaths.LoadOrderFile(env.GameRoot)).ActiveNames.ToList();
        var hub = order.IndexOf("PlayerModelHub");
        Assert.InRange(order.IndexOf("CustomAssets"), 0, hub - 1);
        Assert.InRange(order.IndexOf("ModelReplacementRegistry"), 0, hub - 1);
        Assert.Contains("ExistingMod", order);
        foreach (var name in new[] { "CustomAssets", "ModelReplacementRegistry", "PlayerModelHub" })
            Assert.True(File.Exists(Path.Combine(env.GameRoot, "mods", name, name + ".mod")));
        Assert.Equal(3, (await context.Repository.GetLibraryAsync()).Count);

        var scan = await context.Manager.ScanAsync();
        Assert.DoesNotContain(scan.Diagnostics, item => item.Kind == DarktideIssueKind.MissingDependency);
    }

    [Fact]
    public async Task LaunchGame_ResolvesExecutableFromConfiguredGameRoot()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        using var context = await Context.CreateAsync(env);

        Assert.Equal(Path.Combine(env.GameRoot, "binaries", "Darktide.exe"), context.Manager.GetGameExecutablePath());
    }

    [Fact]
    public async Task LaunchGame_WithoutConfiguredGame_ReportsError()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        using var context = await Context.CreateAsync(env);
        await context.Manager.SaveGamePathAsync("");

        var result = context.Manager.LaunchGame();

        Assert.False(result.Started);
        Assert.NotNull(result.Error);
        Assert.Null(context.Manager.GetGameExecutablePath());
    }

    private sealed class Context : IDisposable
    {
        private HD2MimntDbContext _db = null!;
        public SettingsService Settings { get; private set; } = null!;
        public DarktideRepository Repository { get; private set; } = null!;
        public DarktideManagerService Manager { get; private set; } = null!;

        public static async Task<Context> CreateAsync(DarktideTestEnvironment env)
        {
            var context = new Context();
            context._db = new HD2MimntDbContext(env.DatabasePath);
            await context._db.InitializeDatabaseAsync();
            context.Settings = new SettingsService(new SettingsRepository(env.DatabasePath), env.AppData);
            await context.Settings.SetSettingAsync(DarktideSettingsKeys.GamePath, env.GameRoot);
            context.Repository = new DarktideRepository(context._db);
            context.Manager = new DarktideManagerService(context.Settings, context.Repository,
                new DarktideGameLocator(), new DarktideModMetadataReader(), new DarktideArchiveInspector(),
                new DarktideModInstaller(), env.Storage);
            return context;
        }

        public void Dispose() => _db.Dispose();
    }
}
