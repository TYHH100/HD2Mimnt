using System.IO.Compression;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// 散装文件夹合集：外层目录没有清单、每个子目录各自是模组时，按一个模组导入，
/// 一级是子模组组件、二级是子模组自己的选项。回归见 docs/features/MOD_COLLECTION_IMPORT.md。
/// </summary>
public sealed class ModCollectionImportTests : IDisposable
{
    private const string PatchPrefix = "9ba626afa44a3aa3";

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-collection-test-{Guid.NewGuid():N}");
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _mods;
    private readonly ProfileRepository _profiles;
    private readonly DeploymentRepository _deployments;
    private readonly ModImportService _import;
    private readonly DeploymentService _deployment;

    public ModCollectionImportTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _mods = new(_db); _profiles = new(_db); _deployments = new(_db);
        _import = new(_mods, Path.Combine(_root, "library"), tempDirectory: Path.Combine(_root, "temp"));
        _deployment = new(_profiles, _mods, _deployments, [new CopyDeploymentMethod()]);
    }

    [Fact]
    public async Task LooseFolderSeries_ImportsOneModWithComponentsAndSecondLevelOptions()
    {
        // 复现用户反馈的散装文件夹：外层没有清单，三个子目录各自是清单名称相同的模组。
        var source = Path.Combine(_root, "Shinra战斗女仆替换RS系列");
        const string seriesName = "Shinra战斗女仆替换RS-67“空密码”潜行中甲";
        var first = CreateV1Child(source, "RS-100“制裁者”潜行轻甲", seriesName);
        CreateV1Child(source, "RS-67“空密码”潜行中甲", seriesName);
        CreateV1Child(source, "RS-89“暗影典范”潜行轻甲", seriesName);
        var firstManifestJson = await File.ReadAllTextAsync(Path.Combine(first, "manifest.json"));

        var id = await _import.ImportModAsync(source);
        var mod = (await _mods.GetByIdAsync(id))!;

        // 一个库条目：名称取导入的文件夹名，不是子模组清单里被复制的名称。
        Assert.Equal("Shinra战斗女仆替换RS系列", mod.Name);
        Assert.Equal(string.Empty, mod.WorkflowType);
        Assert.Equal(ModSource.LocalFolder, mod.Source);
        Assert.Equal(ManifestVersion.V1, mod.Manifest!.ManifestVersion);

        // 一级组件：子目录名区分同名作者清单；组件图来自子清单 IconPath。
        Assert.Equal(3, mod.Manifest.OptionGroups!.Count);
        Assert.Equal(new[]
        {
            "RS-100“制裁者”潜行轻甲", "RS-67“空密码”潜行中甲", "RS-89“暗影典范”潜行轻甲"
        }, mod.Manifest.OptionGroups.Select(group => group.Name));
        Assert.All(mod.Manifest.OptionGroups, group => Assert.True(File.Exists(Path.Combine(mod.Path, group.Image!))));

        // 二级选项：子清单的 6 个选项都保留，Include 与图片映射到合集目录下。
        Assert.Equal(6, mod.Manifest.Options!.Count);
        Assert.All(mod.Manifest.Options, option => Assert.False(string.IsNullOrWhiteSpace(option.GroupId)));
        Assert.Equal("本体", mod.Manifest.Options[0].Name);
        Assert.Equal("RS-100“制裁者”潜行轻甲/本体", Assert.Single(mod.Manifest.Options[0].Include!));
        Assert.Equal("RS-100“制裁者”潜行轻甲/本体/1.png", mod.Manifest.Options[0].Image);
        Assert.True(File.Exists(Path.Combine(mod.Path, mod.Manifest.Options[0].Image!)));

        // 外层没有封面时卡片留空，不能借用组件图片；作者子清单保持原字节。
        Assert.Null(mod.Manifest.IconPath);
        Assert.Empty(mod.ThumbnailPath);
        Assert.True(File.Exists(Path.Combine(mod.Path, ".hd2mimnt-options.json")));
        Assert.Equal(firstManifestJson, await File.ReadAllTextAsync(
            Path.Combine(mod.Path, "RS-100“制裁者”潜行轻甲", "manifest.json")));
        // 清单分组随数据库保存，重新读取仍能显示两级。
        var reloaded = (await _mods.GetByIdAsync(id))!;
        Assert.Equal(3, reloaded.Manifest!.OptionGroups!.Count);

        // 数据库清单缺失时从管理器自有 JSON 回填，两级结构不能丢。
        reloaded.Manifest = null;
        await _mods.UpdateAsync(reloaded);
        var service = new ModService(_mods, Path.Combine(_root, "library"));
        var backfilled = (await service.GetAllModsAsync()).Single(item => item.Id == id);
        Assert.Equal(3, backfilled.Manifest!.OptionGroups!.Count);
        Assert.Equal(6, backfilled.Manifest.Options!.Count);
        Assert.Equal("RS-100“制裁者”潜行轻甲/本体", backfilled.Manifest.Options[0].Include![0]);

        // 全部启用：六个目录的补丁按部署编号落盘（缺失的 .stream/.gpu_resources 由部署器补占位文件）。
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var all = await CreateProfile(id, [true, true, true, true, true, true]);
        Assert.Equal(DeploymentStatus.Completed, (await Deploy(all, game)).Status);
        var deployed = DeployedPatchMainFiles(game);
        Assert.Equal(6, deployed.Length);
        Assert.Equal("RS-100“制裁者”潜行轻甲-本体",
            await File.ReadAllTextAsync(Path.Combine(game, "data", $"{PatchPrefix}.patch_0")));

        // 只启用第二个组件：只部署它自己的两个选项目录，其余组件不参与。
        var second = await CreateProfile(id, [false, false, true, true, false, false]);
        var secondResult = await Deploy(second, game);
        Assert.True(secondResult.Status == DeploymentStatus.Completed, secondResult.ErrorMessage);
        var onlySecond = DeployedPatchMainFiles(game);
        Assert.Equal(2, onlySecond.Length);
        Assert.Equal("RS-67“空密码”潜行中甲-本体",
            await File.ReadAllTextAsync(Path.Combine(game, "data", $"{PatchPrefix}.patch_0")));
        Assert.Equal("RS-67“空密码”潜行中甲-材质包",
            await File.ReadAllTextAsync(Path.Combine(game, "data", $"{PatchPrefix}.patch_1")));

        // 同一份散装文件夹重复添加仍按内容判重，不能因为合集是生成的清单而漏判。
        await Assert.ThrowsAsync<InvalidOperationException>(() => _import.ImportModAsync(source));
        Assert.Single(await _mods.GetAllAsync());
    }

    [Fact]
    public async Task WrappedZipSeries_IsDetectedAsCollectionAtSecondLevel()
    {
        // 压缩包常见布局：zip 根目录只有一个同名文件夹，合集实际在第二层。
        var staging = Path.Combine(_root, "staging");
        var wrapper = Path.Combine(staging, "Shinra系列");
        CreateV1Child(wrapper, "RS-100“制裁者”潜行轻甲", "同名清单");
        CreateV1Child(wrapper, "RS-67“空密码”潜行中甲", "同名清单");
        var archive = Path.Combine(_root, "CustomSeries.zip");
        ZipFile.CreateFromDirectory(staging, archive);

        var id = await _import.ImportModAsync(archive);
        var mod = (await _mods.GetByIdAsync(id))!;
        Assert.Equal("CustomSeries", mod.Name);
        Assert.Equal(ModSource.LocalZip, mod.Source);
        Assert.Equal(2, mod.Manifest!.OptionGroups!.Count);
        Assert.Equal(new[] { "RS-100“制裁者”潜行轻甲", "RS-67“空密码”潜行中甲" },
            mod.Manifest.OptionGroups.Select(group => group.Name));
        // 压缩包临时解压目录在导入结束后清理。
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "temp")));
    }

    [Fact]
    public async Task LegacyAndOptionlessChildren_AreConvertedToComponentOptions()
    {
        var source = Path.Combine(_root, "Mixed Collection");
        var legacy = Path.Combine(source, "Legacy Child");
        CreateLegacyChild(legacy);
        var plain = Path.Combine(source, "Plain Child");
        CreatePlainChild(plain);

        var id = await _import.ImportModAsync(source);
        var mod = (await _mods.GetByIdAsync(id))!;
        Assert.Equal(2, mod.Manifest!.OptionGroups!.Count);

        // Legacy 子模组：一个组件卡片 + 两个互斥子选项，Include 指向合集下的子目录。
        var legacyOption = Assert.Single(mod.Manifest.Options!, option => option.Name == "Legacy Child");
        Assert.Empty(legacyOption.Include!);
        Assert.Equal(new[] { "本体", "材质包" }, legacyOption.SubOptions!.Select(sub => sub.Name));
        Assert.Equal("Legacy Child/本体", Assert.Single(legacyOption.SubOptions![0].Include!));

        // 没有选项声明的子模组：整个子目录作为一个可整体启停的组件。
        var plainOption = Assert.Single(mod.Manifest.Options!, option => option.Name == "Plain Child");
        Assert.Equal(new[] { "Plain Child" }, plainOption.Include!);
        Assert.True(plainOption.DefaultEnabled);

        // 只选 Legacy 组件的第二款丝袜：只部署该子目录。
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var profile = await CreateProfile(id, [true, false], optionSelections: [1, 0]);
        Assert.Equal(DeploymentStatus.Completed, (await Deploy(profile, game)).Status);
        var files = DeployedPatchMainFiles(game);
        Assert.Single(files);
        Assert.Equal("Legacy Child-材质包", await File.ReadAllTextAsync(files[0]));
    }

    [Fact]
    public async Task SingleWrappedModFolder_KeepsAuthorManifestInsteadOfCollection()
    {
        // 回归：不是合集（只有一个带清单的包裹目录）时保持原有“向下找一层清单”的行为。
        var source = Path.Combine(_root, "wrapper-source");
        var inner = Path.Combine(source, "Inner Mod");
        Directory.CreateDirectory(Path.Combine(inner, "本体"));
        await File.WriteAllTextAsync(Path.Combine(inner, "本体", $"{PatchPrefix}.patch_0"), "patch");
        await File.WriteAllTextAsync(Path.Combine(inner, "manifest.json"),
            """{"Version":1,"Name":"Inner Mod","Description":"作者清单","Options":[{"Name":"本体","Include":["本体"]}]}""");

        var id = await _import.ImportModAsync(source);
        var mod = (await _mods.GetByIdAsync(id))!;
        Assert.Equal("Inner Mod", mod.Name);
        Assert.Null(mod.Manifest!.OptionGroups);
        Assert.Equal("作者清单", mod.Manifest.Description);
        Assert.False(File.Exists(Path.Combine(mod.Path, ".hd2mimnt-options.json")));
    }

    [Fact]
    public async Task NestedArchivePackage_ExpandsIntoPatchComponentCollection()
    {
        // 用户 2026-10-27 样本：外层压缩包只有一个包裹目录，里面是多个无清单的补丁压缩包。
        // 旧行为把内层压缩包原样当模组文件保存，部署收不到任何补丁。
        var staging = Path.Combine(_root, "nested-staging");
        var wrapper = Path.Combine(staging, "修复合集");
        Directory.CreateDirectory(wrapper);
        foreach (var name in new[] { "56 替换TG-8", "摘下口罩", "移除帘子" })
            CreatePatchPackage(Path.Combine(wrapper, name + ".zip"), name);

        var archive = Path.Combine(_root, "NestedPackage.zip");
        ZipFile.CreateFromDirectory(staging, archive);

        var id = await _import.ImportModAsync(archive);
        var mod = (await _mods.GetByIdAsync(id))!;

        // 一个库条目：内层包各是一级组件，补丁目录默认启用。
        Assert.Equal("NestedPackage", mod.Name);
        Assert.Equal(ModSource.LocalZip, mod.Source);
        Assert.Equal(new[] { "56 替换TG-8", "摘下口罩", "移除帘子" },
            mod.Manifest!.OptionGroups!.Select(group => group.Name));
        Assert.Equal(3, mod.Manifest.Options!.Count);
        Assert.All(mod.Manifest.Options, option => Assert.True(option.DefaultEnabled));
        Assert.Equal("摘下口罩", Assert.Single(mod.Manifest.Options[1].Include!));

        // 内层压缩包已展开：库内保存目录与补丁文件，而不是原压缩包。
        Assert.True(File.Exists(Path.Combine(mod.Path, "摘下口罩", $"{PatchPrefix}.patch_0")));
        Assert.Empty(Directory.EnumerateFiles(mod.Path, "*.zip", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(mod.Path, ".hd2mimnt-options.json")));

        // 全部启用：三个组件的补丁按资源前缀重新编号后落盘。
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var all = await CreateProfile(id, [true, true, true]);
        Assert.Equal(DeploymentStatus.Completed, (await Deploy(all, game)).Status);
        var deployed = DeployedPatchMainFiles(game);
        Assert.Equal(3, deployed.Length);
        Assert.Equal("56 替换TG-8", await File.ReadAllTextAsync(Path.Combine(game, "data", $"{PatchPrefix}.patch_0")));
        Assert.Equal("移除帘子", await File.ReadAllTextAsync(Path.Combine(game, "data", $"{PatchPrefix}.patch_2")));

        // 只启用第二个组件：只部署它自己的补丁，其余组件不参与。
        var onlyMask = await CreateProfile(id, [false, true, false]);
        Assert.Equal(DeploymentStatus.Completed, (await Deploy(onlyMask, game)).Status);
        var onlySecond = DeployedPatchMainFiles(game);
        Assert.Single(onlySecond);
        Assert.Equal("摘下口罩", await File.ReadAllTextAsync(onlySecond[0]));

        // 重复导入按展开后的完整内容判重。
        await Assert.ThrowsAsync<InvalidOperationException>(() => _import.ImportModAsync(archive));
        Assert.Single(await _mods.GetAllAsync());
    }

    [Fact]
    public async Task SingleNestedPatchArchive_IsFlattenedIntoModRoot()
    {
        // 只有一个内层补丁包时不算合集：展开后拍平到模组根，普通部署即可收集补丁。
        var staging = Path.Combine(_root, "single-staging");
        Directory.CreateDirectory(staging);
        CreatePatchPackage(Path.Combine(staging, "Milltina替换TG-8.zip"), "替换补丁");
        var archive = Path.Combine(_root, "SinglePackage.zip");
        ZipFile.CreateFromDirectory(staging, archive);

        var id = await _import.ImportModAsync(archive);
        var mod = (await _mods.GetByIdAsync(id))!;

        Assert.Equal("SinglePackage", mod.Name);
        Assert.NotNull(mod.Manifest);
        Assert.Null(mod.Manifest!.Options);
        Assert.True(File.Exists(Path.Combine(mod.Path, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(mod.Path, $"{PatchPrefix}.patch_0")));
        Assert.Empty(Directory.EnumerateFiles(mod.Path, "*.zip", SearchOption.AllDirectories));

        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var profile = await CreateProfile(id, []);
        Assert.Equal(DeploymentStatus.Completed, (await Deploy(profile, game)).Status);
        Assert.Equal("替换补丁", await File.ReadAllTextAsync(Assert.Single(DeployedPatchMainFiles(game))));
    }

    [Fact]
    public async Task ModWithRootManifest_KeepsNestedArchiveAsContent()
    {
        // 作者根清单已声明这是模组：包内压缩包是素材内容，不能展开或删除。
        var staging = Path.Combine(_root, "content-staging");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
            """{"Version":1,"Name":"带素材包的模组","Description":"作者清单"}""");
        CreatePatchPackage(Path.Combine(staging, "素材包.zip"), "素材");

        var archive = Path.Combine(_root, "ModWithContent.zip");
        ZipFile.CreateFromDirectory(staging, archive);

        var id = await _import.ImportModAsync(archive);
        var mod = (await _mods.GetByIdAsync(id))!;

        Assert.Equal("带素材包的模组", mod.Name);
        Assert.True(File.Exists(Path.Combine(mod.Path, "素材包.zip")));
        Assert.False(Directory.Exists(Path.Combine(mod.Path, "素材包")));
    }

    [Fact]
    public async Task LoosePatchSubdirectoriesWithoutManifest_KeepInferredManifestNotCollection()
    {
        // 普通散装目录没有清单：仍按旧语义推断选项，不能因为子目录是补丁目录就改判成合集。
        var source = Path.Combine(_root, "LegacyPatchLayout");
        foreach (var name in new[] { "本体", "材质包" })
        {
            var directory = Path.Combine(source, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{PatchPrefix}.patch_0"), name);
        }

        var id = await _import.ImportModAsync(source);
        var mod = (await _mods.GetByIdAsync(id))!;

        Assert.Null(mod.Manifest!.OptionGroups);
        Assert.Equal(2, mod.Manifest.Options!.Count);
        Assert.False(File.Exists(Path.Combine(mod.Path, ".hd2mimnt-options.json")));
    }

    private static void CreatePatchPackage(string zipPath, string marker)
    {
        var content = Path.Combine(Path.GetDirectoryName(zipPath)!, $"payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(content);
        try
        {
            File.WriteAllText(Path.Combine(content, $"{PatchPrefix}.patch_0"), marker);
            ZipFile.CreateFromDirectory(content, zipPath);
        }
        finally
        {
            Directory.Delete(content, true);
        }
    }

    private static string CreateV1Child(string parent, string directoryName, string manifestName)
    {
        var child = Path.Combine(parent, directoryName);
        foreach (var optionDirectory in new[] { "本体", "材质包" })
        {
            Directory.CreateDirectory(Path.Combine(child, optionDirectory));
            File.WriteAllText(Path.Combine(child, optionDirectory, $"{PatchPrefix}.patch_0"), $"{directoryName}-{optionDirectory}");
            File.WriteAllBytes(Path.Combine(child, optionDirectory, "1.png"), Png);
        }
        File.WriteAllBytes(Path.Combine(child, "1.png"), Png);
        File.WriteAllText(Path.Combine(child, "manifest.json"), $$"""
            {
              "Version": 1,
              "Guid": "{{Guid.NewGuid()}}",
              "Name": "{{manifestName}}",
              "Description": "合集组件",
              "IconPath": "1.png",
              "Options": [
                { "Name": "本体", "Description": "必选", "Image": "本体/1.png", "Include": ["本体"] },
                { "Name": "材质包", "Description": "可选", "Image": "材质包/1.png", "Include": ["材质包"] }
              ]
            }
            """);
        return child;
    }

    private static string CreateLegacyChild(string child)
    {
        foreach (var optionDirectory in new[] { "本体", "材质包" })
        {
            Directory.CreateDirectory(Path.Combine(child, optionDirectory));
            File.WriteAllText(Path.Combine(child, optionDirectory, $"{PatchPrefix}.patch_0"), $"Legacy Child-{optionDirectory}");
        }
        File.WriteAllBytes(Path.Combine(child, "1.png"), Png);
        // 没有 Version 字段 = Legacy 清单，Options 是互斥目录名的字符串数组。
        File.WriteAllText(Path.Combine(child, "manifest.json"),
            """{"Name":"Legacy Child","Description":"旧版组件","Options":["本体","材质包"]}""");
        return child;
    }

    private static string CreatePlainChild(string child)
    {
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, $"{PatchPrefix}.patch_0"), "Plain Child");
        File.WriteAllBytes(Path.Combine(child, "1.png"), Png);
        File.WriteAllText(Path.Combine(child, "manifest.json"),
            """{"Version":1,"Name":"Plain Child","Description":"无选项组件","IconPath":"1.png"}""");
        return child;
    }

    private async Task<Guid> CreateProfile(Guid modId, IReadOnlyList<bool> toggles,
        IReadOnlyList<int>? optionSelections = null)
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(), Name = $"test-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow
        };
        profile.Mods.Add(new ProfileMod
        {
            ProfileId = profile.Id, ModId = modId, Enabled = true,
            OptionsJson = new ProfileModOptionsState
            {
                OptionToggles = toggles.ToList(),
                OptionSelections = optionSelections?.ToList()
            }.ToJson()
        });
        await _profiles.AddAsync(profile);
        return profile.Id;
    }

    /// <summary>只取补丁主文件（部署器会为缺失的 .stream/.gpu_resources 创建占位文件）。</summary>
    private static string[] DeployedPatchMainFiles(string game)
        => Directory.GetFiles(Path.Combine(game, "data"))
            .Where(file => !file.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

    private Task<DeploymentResult> Deploy(Guid profile, string game)
        => _deployment.DeployProfileAsync(profile, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, game);

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
