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

        // 外层没有封面时回退第一个组件图片；生成选项只进自有 JSON，作者子清单保持原字节。
        Assert.Equal("RS-100“制裁者”潜行轻甲/1.png", mod.Manifest.IconPath);
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
        Assert.Equal(DeploymentStatus.Completed, (await Deploy(second, game)).Status);
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
