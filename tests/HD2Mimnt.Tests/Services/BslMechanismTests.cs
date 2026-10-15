using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public sealed class BslMechanismTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-Bsl-{Guid.NewGuid():N}");
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _mods;
    private readonly ProfileRepository _profiles;
    private readonly ProfileService _service;
    private readonly DeploymentService _deployment;

    public BslMechanismTests()
    {
        Directory.CreateDirectory(_root);
        _db = new HD2MimntDbContext(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _mods = new ModRepository(_db);
        _profiles = new ProfileRepository(_db);
        _service = new ProfileService(_profiles, _mods);
        _deployment = new DeploymentService(_profiles, _mods, new DeploymentRepository(_db), new[] { new CopyDeploymentMethod() });
    }

    [Fact]
    public async Task ImportOfficialLayout_ShouldRetainIdentityAndIncludeDataDirectory()
    {
        var zip = Path.Combine(_root, "renamed-loader.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                writer.Write("""
                    {"Version":1,"Guid":"612eaf70-d682-43c7-9efd-16dcc695f977","Name":"任意名称","Options":[{"Name":"Loader","Include":["data"]}]}
                    """);
            using (var patch = archive.CreateEntry("data/9ba626afa44a3aa3.patch_0").Open())
                patch.Write(new byte[] { 1, 2, 3 });
            archive.CreateEntry("data/9ba626afa44a3aa3.patch_0.stream");
            archive.CreateEntry("data/9ba626afa44a3aa3.patch_0.gpu_resources");
        }
        var import = new ModImportService(_mods, Path.Combine(_root, "library"));
        var id = await import.ImportModAsync(zip);
        var mod = (await _mods.GetByIdAsync(id))!;
        Assert.Equal(BslModRole.Loader, mod.BslRole);
        Assert.Equal(BslModDetector.LoaderGuid, mod.Manifest!.Guid);
        var profile = await _service.CreateProfileAsync("配置");
        await _service.AddModToProfileAsync(profile, id);
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var result = await _deployment.DeployProfileAsync(profile, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, game);
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(game, "data/9ba626afa44a3aa3.patch_0")));
    }

    [Theory]
    [InlineData("-- HD2-Addon: mods/test/new_addon\nreturn true", "mods/test/new_addon", BslModRole.Dependency)]
    [InlineData("-- HD2-Addon: mods/test/new_addon\nreturn true", "mods/test/unrelated", BslModRole.None)]
    [InlineData("-- HD2-Addon: mods/codex/loader\nreturn true", "mods/codex/loader", BslModRole.None)]
    [InlineData("return true", "mods/cowboybingus/vehicle_stability", BslModRole.Dependency)]
    [InlineData("return true", "mods/codex/gun_calibration", BslModRole.None)]
    [InlineData("-- HD2-Addon: mods/mdl/runtime\nreturn true", "mods/mdl/runtime", BslModRole.MdlLoader)]
    [InlineData("-- HD2-Addon: mods/codex/smoothboot\nreturn true", "mods/codex/smoothboot", BslModRole.SmoothBoot)]
    public async Task DetectLuaResource_ShouldUseResourceIdentityAndValidDeclaration(string script, string resource, BslModRole expected)
    {
        var mod = await AddModAsync("显示名称与内容无关");
        WriteArchive(mod.Path, resource, script);
        // sidecar 不能进入 TOC 扫描，且不得将同补丁前缀认定为 BSL。
        await File.WriteAllBytesAsync(Path.Combine(mod.Path, "9ba626afa44a3aa3.patch_0.stream"), new byte[] { 255 });
        Assert.Equal(expected, await BslModDetector.DetectAsync(mod));
        Assert.Equal(0x7251fdd9bb62480aUL, BslModDetector.ResourceHash("core/wwise/lua/wwise_flow_callbacks"));
    }

    [Fact]
    public async Task DetectProvenance_ShouldReadRequirementAndExcludeProvidesOrDisplayName()
    {
        var mod = await AddModAsync("Bingus Shared Loader");
        var provenance = Path.Combine(mod.Path, "Any-manifest.json");
        await File.WriteAllTextAsync(provenance, "{\"provides\":{\"shared_loader_api\":1}}");
        Assert.Equal(BslModRole.None, await BslModDetector.DetectAsync(mod));
        await File.WriteAllTextAsync(provenance, "{\"requires\":{\"shared_loader_api\":1}}");
        Assert.Equal(BslModRole.Dependency, await BslModDetector.DetectAsync(mod));
        await File.WriteAllTextAsync(provenance, "{\"requires\":{\"shared_loader_api\":false}}");
        Assert.Equal(BslModRole.None, await BslModDetector.DetectAsync(mod));
    }

    [Fact]
    public async Task DamagedOrCanceledScan_ShouldNotCacheASuccessfulClassification()
    {
        var mod = await AddModAsync("损坏包");
        await File.WriteAllBytesAsync(Path.Combine(mod.Path, "9ba626afa44a3aa3.patch_0"), new byte[] { 17, 0, 0, 240 });
        await BslProfilePolicy.EnsureDetectedAsync(mod, _mods, default);
        Assert.Null((await _mods.GetByIdAsync(mod.Id))!.BslRole);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BslModDetector.DetectAsync(mod, cts.Token));
    }

    [Fact]
    public async Task CancelPendingScan_ShouldCompleteAsCanceledWithoutThrowingInsideDetector()
    {
        var mod = await AddModAsync("取消扫描");
        // 大索引使任务在取消时仍未完成；空资源记录不会触发提前识别。
        const uint resourceCount = 1_000_000;
        using (var stream = File.Create(Path.Combine(mod.Path, "9ba626afa44a3aa3.patch_0")))
        {
            var header = new byte[72];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0xF0000011);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), resourceCount);
            stream.Write(header);
            stream.SetLength(72L + resourceCount * 80L);
        }

        using var cancellation = new CancellationTokenSource();
        var detectorExceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is OperationCanceledException
                && args.Exception.StackTrace?.Contains("BslModDetector", StringComparison.Ordinal) == true)
                detectorExceptions.Enqueue(args.Exception);
        }

        Task<BslModRole?> scan;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            scan = BslModDetector.DetectAsync(mod, cancellation.Token);
            await Task.Delay(10);
            Assert.False(scan.IsCompleted);
            cancellation.Cancel();
            Assert.Same(scan, await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.True(scan.IsCanceled);
            Assert.Empty(detectorExceptions);
        }
        finally
        {
            cancellation.Cancel();
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var saved = (await _mods.GetByIdAsync(mod.Id))!;
        Assert.Null(saved.BslRole);
        Assert.Equal(0, saved.BslDetectionVersion);
    }

    [Fact]
    public async Task CanceledKnownGuidDetection_ShouldNotChangeClassificationOrCacheVersion()
    {
        var mod = await AddModAsync("已知加载器");
        mod.Manifest = new ModManifest { Guid = BslModDetector.LoaderGuid };
        await _mods.UpdateAsync(mod);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BslProfilePolicy.EnsureDetectedAsync(mod, _mods, cancellation.Token));

        Assert.Null(mod.BslRole);
        Assert.Equal(0, mod.BslDetectionVersion);
        var saved = (await _mods.GetByIdAsync(mod.Id))!;
        Assert.Null(saved.BslRole);
        Assert.Equal(0, saved.BslDetectionVersion);
    }

    [Fact]
    public async Task ProfileMutations_ShouldAutoGroupProtectLoaderAndPreserveEnabledOptions()
    {
        var loader = await AddModAsync("已改名 Loader", BslModRole.Loader);
        var dependency = await AddModAsync("依赖", BslModRole.Dependency);
        var normal = await AddModAsync("普通");
        var profileId = await _service.CreateProfileAsync("配置");
        await _service.AddModToProfileAsync(profileId, loader.Id);
        await _service.SetModsEnabledAsync(profileId, new[] { dependency.Id, normal.Id }, true);
        await _service.SetModEnabledAsync(profileId, dependency.Id, false);
        var state = new ProfileModOptionsState();
        await _service.SetModOptionsAsync(profileId, dependency.Id, state);
        await _service.ReorderModsAsync(profileId, new() { loader.Id, dependency.Id, normal.Id });
        var profile = (await _service.GetProfileByIdAsync(profileId))!;
        Assert.Equal(new[] { normal.Id, dependency.Id, loader.Id }, profile.Mods.Select(item => item.ModId));
        Assert.False(profile.Mods[1].Enabled);
        Assert.Equal(state.ToJson(), profile.Mods[1].OptionsJson);
        var copy = await _service.DuplicateProfileAsync(profileId, "副本");
        Assert.Equal(profile.Mods.Select(item => item.ModId), copy.Mods.OrderBy(item => item.Order).Select(item => item.ModId));

        // 旧库的成员在第一次读取时识别、修复，修改顺序也不能将普通模组混入 BSL。
        dependency.BslRole = null;
        dependency.Manifest = new ModManifest { Description = "Requires Bingus Shared Loader v15 or newer / API 1." };
        await _mods.UpdateAsync(dependency);
        profile.Mods.Single(item => item.ModId == loader.Id).Order = -1;
        await _profiles.UpdateAsync(profile);
        profile = (await _service.GetProfileByIdAsync(profileId))!;
        Assert.Equal(loader.Id, profile.Mods.Last().ModId);
        Assert.Equal(BslModRole.Dependency, (await _mods.GetByIdAsync(dependency.Id))!.BslRole);
    }

    [Theory]
    [InlineData(DeploymentOrder.TopToBottom)]
    [InlineData(DeploymentOrder.BottomToTop)]
    public async Task UnlockedLoaders_ShouldKeepManualOrderThroughReloadDeploymentAndRelocking(DeploymentOrder direction)
    {
        var settings = new SettingsService(_root);
        await settings.SetSettingAsync(SettingKeys.ProfilesLockLoaders, false);
        var service = new ProfileService(_profiles, _mods, settings);
        var deployment = new DeploymentService(_profiles, _mods, new DeploymentRepository(_db),
            new[] { new CopyDeploymentMethod() }, settingsService: settings);
        var normal = await AddModAsync("普通");
        var dependency = await AddModAsync("依赖", BslModRole.Dependency);
        var loader = await AddModAsync("Loader", BslModRole.Loader);
        var smooth = await AddModAsync("SmoothBoot", BslModRole.SmoothBoot);
        var manual = new[] { smooth, loader, normal, dependency };
        foreach (var mod in manual)
            await File.WriteAllTextAsync(Path.Combine(mod.Path, "9ba626afa44a3aa3.patch_0"), mod.Name);
        var id = await service.CreateProfileAsync("手动顺序");
        await service.SetModsEnabledAsync(id, manual.Select(mod => mod.Id).ToArray(), true);
        await service.ReorderProfileRowsAsync(id, manual.Select(mod => mod.Id).ToArray(), manual.Select(mod => mod.Id).ToArray());
        var saved = (await service.GetProfileByIdAsync(id))!;
        Assert.Equal(manual.Select(mod => mod.Id), saved.Mods.Select(mod => mod.ModId));
        await service.SetModEnabledAsync(id, loader.Id, false);
        Assert.False((await service.GetProfileByIdAsync(id))!.Mods.Single(mod => mod.ModId == loader.Id).Enabled);
        await service.SetModEnabledAsync(id, loader.Id, true);
        var game = Path.Combine(_root, "manual-game");
        Directory.CreateDirectory(game);
        var result = await deployment.DeployProfileAsync(id, DeploymentMethod.Copy, direction, game);
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        var expected = direction == DeploymentOrder.TopToBottom ? manual : manual.Reverse().ToArray();
        for (var index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index].Name, await File.ReadAllTextAsync(Path.Combine(game, $"data/9ba626afa44a3aa3.patch_{index}")));

        await settings.SetSettingAsync(SettingKeys.ProfilesLockLoaders, true);
        saved = (await service.GetProfileByIdAsync(id))!;
        Assert.Equal(new[] { normal.Id, dependency.Id, loader.Id, smooth.Id }, saved.Mods.Select(mod => mod.ModId));
        Assert.All(saved.Mods, mod => Assert.True(mod.Enabled));
    }

    [Theory]
    [InlineData(DeploymentOrder.TopToBottom)]
    [InlineData(DeploymentOrder.BottomToTop)]
    public async Task DeployBothDirections_ShouldAssignHighestPatchToLoader(DeploymentOrder direction)
    {
        var normal1 = await AddModAsync("普通一");
        var normal2 = await AddModAsync("普通二");
        var dependency = await AddModAsync("依赖", BslModRole.Dependency);
        var loader = await AddModAsync("Loader", BslModRole.Loader);
        var all = new[] { normal1, normal2, dependency, loader };
        foreach (var mod in all) await File.WriteAllTextAsync(Path.Combine(mod.Path, "9ba626afa44a3aa3.patch_0"), mod.Name);
        var profileId = await _service.CreateProfileAsync("配置");
        await _service.SetModsEnabledAsync(profileId, all.Select(mod => mod.Id).ToArray(), true);
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var result = await _deployment.DeployProfileAsync(profileId, DeploymentMethod.Copy, direction, game);
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal("Loader", await File.ReadAllTextAsync(Path.Combine(game, "data/9ba626afa44a3aa3.patch_3")));
        Assert.Equal("依赖", await File.ReadAllTextAsync(Path.Combine(game, "data/9ba626afa44a3aa3.patch_2")));
        Assert.Equal(direction == DeploymentOrder.TopToBottom ? "普通一" : "普通二",
            await File.ReadAllTextAsync(Path.Combine(game, "data/9ba626afa44a3aa3.patch_0")));
        await _service.SetModEnabledAsync(profileId, loader.Id, false);
        result = await _deployment.DeployProfileAsync(profileId, DeploymentMethod.Copy, direction, game);
        Assert.NotEqual(DeploymentStatus.Completed, result.Status);
        Assert.Contains("Bingus Shared Loader", result.ErrorMessage);
        // 缺失依赖的预检不能先清理掉之前成功部署的文件。
        Assert.Equal("Loader", await File.ReadAllTextAsync(Path.Combine(game, "data/9ba626afa44a3aa3.patch_3")));
    }

    private async Task<Mod> AddModAsync(string name, BslModRole? role = null)
    {
        var id = Guid.NewGuid();
        var mod = new Mod { Id = id, Name = name, Path = Path.Combine(_root, id.ToString()), BslRole = role,
            BslDetectionVersion = role != null ? BslModDetector.DetectionVersion : 0,
            Manifest = role switch
            {
                BslModRole.Loader => new ModManifest { Guid = BslModDetector.LoaderGuid },
                BslModRole.MdlLoader => new ModManifest { Guid = BslModDetector.MdlGuid },
                BslModRole.SmoothBoot => new ModManifest { Guid = BslModDetector.SmoothBootGuid },
                _ => null
            } };
        Directory.CreateDirectory(mod.Path);
        await _mods.AddAsync(mod);
        return mod;
    }

    [Fact]
    public async Task CachedRoles_ShouldUpgradeMdlAndSmoothBootByGuidBeforeGenericDependency()
    {
        var mdl = await AddModAsync("已改名", BslModRole.MdlLoader);
        mdl.BslRole = BslModRole.None;
        await _mods.UpdateAsync(mdl);
        var sb = await AddModAsync("已改名", BslModRole.SmoothBoot);
        sb.BslRole = BslModRole.Dependency;
        sb.Manifest!.Description = "Requires Bingus Shared Loader v15 or newer / API 1.";
        await _mods.UpdateAsync(sb);
        await BslProfilePolicy.EnsureDetectedAsync(mdl, _mods, default);
        await BslProfilePolicy.EnsureDetectedAsync(sb, _mods, default);
        Assert.Equal(BslModRole.MdlLoader, (await _mods.GetByIdAsync(mdl.Id))!.BslRole);
        Assert.Equal(BslModRole.SmoothBoot, (await _mods.GetByIdAsync(sb.Id))!.BslRole);
        Assert.Equal(BslModRole.SmoothBoot, await BslModDetector.DetectAsync(sb));
    }

    [Theory]
    [InlineData("mods/mdl/runtime", "-- HD2-Addon: mods/mdl/runtime\nreturn true", BslModRole.MdlLoader)]
    [InlineData("mods/codex/smoothboot", "-- HD2-Addon: mods/codex/smoothboot\nreturn true", BslModRole.SmoothBoot)]
    [InlineData("core/wwise/lua/wwise_flow_callbacks", "CowboyBingusModLoader BingusSharedLoader.log mods/cowboybingus/vanilla_plus_megapack", BslModRole.Loader)]
    [InlineData("core/wwise/lua/wwise_flow_callbacks", "CowboyBingusModLoader MDL.log", BslModRole.None)]
    [InlineData("mods/test/unrelated", "CowboyBingusModLoader BingusSharedLoader.log mods/cowboybingus/vanilla_plus_megapack", BslModRole.None)]
    public async Task ChangedOrMissingGuid_ShouldUseResourceContractAndRefreshOldCache(string resource, string content, BslModRole expected)
    {
        var mod = await AddModAsync("任意名称");
        WriteArchive(mod.Path, resource, content);
        // 不相关资源中写同样文字不会认定为 Loader；只有真实依赖声明生效。
        mod.Manifest = new ModManifest { Guid = Guid.NewGuid() };
        Assert.Equal(expected, await BslModDetector.DetectAsync(mod));
        mod.Manifest = null;
        Assert.Equal(expected, await BslModDetector.DetectAsync(mod));
        mod.BslRole = BslModRole.None;
        mod.BslDetectionVersion = 0;
        await _mods.UpdateAsync(mod);
        await BslProfilePolicy.EnsureDetectedAsync(mod, _mods, default);
        var saved = (await _mods.GetByIdAsync(mod.Id))!;
        Assert.Equal(expected, saved.BslRole);
        Assert.Equal(BslModDetector.DetectionVersion, saved.BslDetectionVersion);
    }

    [Fact]
    public async Task SelectingLoader_ShouldPersistMutualExclusionAndRejectAmbiguousBatch()
    {
        var bsl = await AddModAsync("BSL", BslModRole.Loader);
        var mdl = await AddModAsync("MDL", BslModRole.MdlLoader);
        var profileId = await _service.CreateProfileAsync("配置");
        await _service.AddModToProfileAsync(profileId, bsl.Id);
        await _service.AddModToProfileAsync(profileId, mdl.Id);
        var profile = (await _profiles.GetByIdAsync(profileId))!;
        Assert.False(profile.Mods.Single(item => item.ModId == bsl.Id).Enabled);
        Assert.True(profile.Mods.Single(item => item.ModId == mdl.Id).Enabled);
        await _service.SetModEnabledAsync(profileId, bsl.Id, true);
        profile = (await _profiles.GetByIdAsync(profileId))!;
        Assert.True(profile.Mods.Single(item => item.ModId == bsl.Id).Enabled);
        Assert.False(profile.Mods.Single(item => item.ModId == mdl.Id).Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SetModsEnabledAsync(profileId, new[] { bsl.Id, mdl.Id }, true));
        profile = (await _profiles.GetByIdAsync(profileId))!;
        Assert.True(profile.Mods.Single(item => item.ModId == bsl.Id).Enabled);
        Assert.False(profile.Mods.Single(item => item.ModId == mdl.Id).Enabled);
        // 两个请求不能通过并发读改写把两种加载器同时恢复为启用。
        await Task.WhenAll(_service.SetModEnabledAsync(profileId, bsl.Id, true), _service.SetModEnabledAsync(profileId, mdl.Id, true));
        Assert.Single((await _profiles.GetByIdAsync(profileId))!.Mods, item => item.Enabled);
    }

    [Theory]
    [InlineData(DeploymentOrder.TopToBottom, BslModRole.Loader)]
    [InlineData(DeploymentOrder.BottomToTop, BslModRole.Loader)]
    [InlineData(DeploymentOrder.TopToBottom, BslModRole.MdlLoader)]
    [InlineData(DeploymentOrder.BottomToTop, BslModRole.MdlLoader)]
    public async Task SmoothBoot_ShouldFollowEitherLoaderAndHaveHighestPatchPriority(DeploymentOrder direction, BslModRole loaderRole)
    {
        var normal = await AddModAsync("普通");
        var dependency = await AddModAsync("依赖", BslModRole.Dependency);
        var loader = await AddModAsync("Loader", loaderRole);
        var sb = await AddModAsync("SmoothBoot", BslModRole.SmoothBoot);
        var all = new[] { normal, dependency, loader, sb };
        foreach (var mod in all) await File.WriteAllTextAsync(Path.Combine(mod.Path, "9ba626afa44a3aa3.patch_0"), mod.Name);
        var profileId = await _service.CreateProfileAsync("配置");
        await _service.SetModsEnabledAsync(profileId, all.Reverse().Select(mod => mod.Id).ToArray(), true);
        var profile = (await _service.GetProfileByIdAsync(profileId))!;
        Assert.Equal(all.Select(mod => mod.Id), profile.Mods.Select(item => item.ModId));
        await _service.ReorderModsAsync(profileId, all.Reverse().Select(mod => mod.Id).ToList());
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var result = await _deployment.DeployProfileAsync(profileId, DeploymentMethod.Copy, direction, game);
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        for (var i = 0; i < all.Length; i++)
            Assert.Equal(all[i].Name, await File.ReadAllTextAsync(Path.Combine(game, $"data/9ba626afa44a3aa3.patch_{i}")));

        // 绕过服务写入的旧配置也必须在清理之前拒绝 BSL+MDL 同时部署。
        var other = await AddModAsync("另一加载器", loaderRole == BslModRole.Loader ? BslModRole.MdlLoader : BslModRole.Loader);
        await File.WriteAllTextAsync(Path.Combine(other.Path, "9ba626afa44a3aa3.patch_0"), other.Name);
        profile = (await _profiles.GetByIdAsync(profileId))!;
        profile.Mods.Add(new ProfileMod { ProfileId = profileId, ModId = other.Id, Enabled = true, Order = 10 });
        await _profiles.UpdateAsync(profile);
        result = await _deployment.DeployProfileAsync(profileId, DeploymentMethod.Copy, direction, game);
        Assert.NotEqual(DeploymentStatus.Completed, result.Status);
        Assert.Contains("互斥", result.ErrorMessage);
        Assert.Equal("SmoothBoot", await File.ReadAllTextAsync(Path.Combine(game, "data/9ba626afa44a3aa3.patch_3")));
    }

    private static void WriteArchive(string path, string resourceName, string script)
    {
        var body = Encoding.UTF8.GetBytes(script);
        var bytes = new byte[184 + 8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xF0000011);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(104), BslModDetector.ResourceHash(resourceName));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(112), 0xa14e8dfa2cd117e2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(120), 184);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(160), (uint)(body.Length + 8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(184), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(188), 2);
        body.CopyTo(bytes, 192);
        File.WriteAllBytes(Path.Combine(path, "9ba626afa44a3aa3.patch_0"), bytes);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
