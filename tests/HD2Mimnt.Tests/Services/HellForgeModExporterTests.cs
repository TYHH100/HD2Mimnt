using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Tests.Services;

public sealed class HellForgeModExporterTests : IDisposable
{
    private const string BundleX = "9ba626afa44a3aa3";
    private const string BundleY = "047c26f2f11d253e";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-HfExport-{Guid.NewGuid():N}");

    public HellForgeModExporterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ResolveKind_CleanV1Pack_QualifiesAsPack()
    {
        var mod = CreateMod(Guid.NewGuid(), V1Manifest(
            Option("A", "A"), Option("B", "B")));
        WritePatch(Path.Combine(mod.Path, "A"), BundleX, 0, "a");
        WritePatch(Path.Combine(mod.Path, "B"), BundleX, 1, "b");

        Assert.Equal(HellForgeModKind.Pack, HellForgeModExporter.ResolveKind(mod));
        Assert.True(HellForgeModExporter.TryQualifyAsPack(mod, out _));
    }

    [Fact]
    public void ResolveKind_LegacyManifest_FallsBackToFlat()
    {
        var mod = CreateMod(Guid.NewGuid(), new ModManifest
        {
            Guid = Guid.NewGuid(), Name = "Legacy", ManifestVersion = ManifestVersion.Legacy,
            LegacyOptions = new List<string> { "One", "Two" }
        });
        Assert.Equal(HellForgeModKind.Flat, HellForgeModExporter.ResolveKind(mod));
        Assert.False(HellForgeModExporter.TryQualifyAsPack(mod, out var reason));
        Assert.Contains("V1", reason);
    }

    [Fact]
    public void ResolveKind_MissingIncludeMapping_FallsBackToFlat()
    {
        var mod = CreateMod(Guid.NewGuid(), V1Manifest(new ModOption { Name = "A", Include = null }));
        Assert.Equal(HellForgeModKind.Flat, HellForgeModExporter.ResolveKind(mod));
        Assert.False(HellForgeModExporter.TryQualifyAsPack(mod, out var reason));
        Assert.Contains("Include", reason);
    }

    [Fact]
    public void ResolveKind_RootPatches_FallsBackToFlat()
    {
        var mod = CreateMod(Guid.NewGuid(), V1Manifest(Option("A", "A")));
        WritePatch(Path.Combine(mod.Path, "A"), BundleX, 0, "a");
        WritePatch(mod.Path, BundleX, 9, "root");
        Assert.Equal(HellForgeModKind.Flat, HellForgeModExporter.ResolveKind(mod));
        Assert.False(HellForgeModExporter.TryQualifyAsPack(mod, out var reason));
        Assert.Contains("根目录", reason);
    }

    [Fact]
    public void ResolveKind_MissingIncludeDirectory_FallsBackToFlat()
    {
        var mod = CreateMod(Guid.NewGuid(), V1Manifest(Option("A", "Missing")));
        Assert.Equal(HellForgeModKind.Flat, HellForgeModExporter.ResolveKind(mod));
        Assert.False(HellForgeModExporter.TryQualifyAsPack(mod, out var reason));
        Assert.Contains("Missing", reason);
    }

    [Fact]
    public async Task ExportFlat_RenumbersPerBundleInEffectiveOrder_AndExcludesRootPatches()
    {
        // 根补丁迫使扁平化；完整 Include 映射使 HD2Mimnt 只部署 A、B 目录（根补丁被忽略）。
        var mod = CreateMod(Guid.NewGuid(), V1Manifest(Option("A", "A"), Option("B", "B")));
        WritePatch(Path.Combine(mod.Path, "A"), BundleX, 5, "A5");
        WritePatch(Path.Combine(mod.Path, "B"), BundleX, 2, "B2", stream: true);
        WritePatch(mod.Path, BundleX, 9, "ROOT");

        var export = await HellForgeModExporter.ExportAsync(mod, null, ExportDir(mod));

        Assert.Equal(HellForgeModKind.Flat, export.Kind);
        var data = Path.Combine(ExportDir(mod), "data");
        // A 先部署 → patch_0；B 后部署 → patch_1（编号取反原编号，保住有效顺序）。
        Assert.Equal("A5", await File.ReadAllTextAsync(Path.Combine(data, $"{BundleX}.patch_0")));
        Assert.Equal("B2", await File.ReadAllTextAsync(Path.Combine(data, $"{BundleX}.patch_1")));
        Assert.Equal("B2-stream", await File.ReadAllTextAsync(Path.Combine(data, $"{BundleX}.patch_1.stream")));
        Assert.False(File.Exists(Path.Combine(data, $"{BundleX}.patch_2")));
        Assert.False(File.Exists(Path.Combine(data, $"{BundleX}.patch_9")));

        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(export.ManifestPath)).RootElement;
        Assert.Equal(1, manifest.GetProperty("format").GetInt32());
        Assert.Equal(HellForgeModExporter.FlatModId(mod), manifest.GetProperty("id").GetString());
        Assert.True(manifest.GetProperty("gameplay").GetBoolean());
        Assert.Equal("1.0.0", manifest.GetProperty("version").GetString());
    }

    [Fact]
    public async Task ExportFlat_LegacySelection_OnlySelectedDirectory()
    {
        var mod = CreateMod(Guid.NewGuid(), new ModManifest
        {
            Guid = Guid.NewGuid(), Name = "Legacy", ManifestVersion = ManifestVersion.Legacy,
            LegacyOptions = new List<string> { "One", "Two" }
        });
        WritePatch(Path.Combine(mod.Path, "One"), BundleX, 0, "one");
        WritePatch(Path.Combine(mod.Path, "Two"), BundleY, 0, "two");
        var optionsJson = new ProfileModOptionsState { LegacySelectedIndex = 1 }.ToJson();

        await HellForgeModExporter.ExportAsync(mod, optionsJson, ExportDir(mod));

        var data = Path.Combine(ExportDir(mod), "data");
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(data, $"{BundleY}.patch_0")));
        Assert.False(File.Exists(Path.Combine(data, $"{BundleX}.patch_0")));
    }

    [Fact]
    public async Task ExportFlat_MultipleBundles_NumberIndependently()
    {
        var mod = CreateMod(Guid.NewGuid(), new ModManifest { Guid = Guid.NewGuid(), Name = "NoOptions" });
        WritePatch(mod.Path, BundleX, 3, "x3");
        WritePatch(mod.Path, BundleX, 7, "x7");
        WritePatch(mod.Path, BundleY, 2, "y2");

        await HellForgeModExporter.ExportAsync(mod, null, ExportDir(mod));

        var data = Path.Combine(ExportDir(mod), "data");
        Assert.Equal("x3", await File.ReadAllTextAsync(Path.Combine(data, $"{BundleX}.patch_0")));
        Assert.Equal("x7", await File.ReadAllTextAsync(Path.Combine(data, $"{BundleX}.patch_1")));
        Assert.Equal("y2", await File.ReadAllTextAsync(Path.Combine(data, $"{BundleY}.patch_0")));
    }

    [Fact]
    public async Task ExportPack_CopiesManifestIncludeDirsAndImages_ExcludesJunk()
    {
        var manifest = V1Manifest(
            new ModOption { Name = "A", Include = new List<string> { "A" }, Image = ".jpgs/a.png" },
            new ModOption { Name = "B", Include = new List<string> { "B" } });
        manifest.IconPath = ".jpgs/icon.png";
        var mod = CreateMod(Guid.NewGuid(), manifest);
        WritePatch(Path.Combine(mod.Path, "A"), BundleX, 0, "a");
        WritePatch(Path.Combine(mod.Path, "B"), BundleX, 1, "b");
        Directory.CreateDirectory(Path.Combine(mod.Path, ".jpgs"));
        File.WriteAllText(Path.Combine(mod.Path, ".jpgs", "icon.png"), "icon");
        File.WriteAllText(Path.Combine(mod.Path, ".jpgs", "a.png"), "a-img");
        // 垃圾文件：不应进入导出。
        Directory.CreateDirectory(Path.Combine(mod.Path, "LuaExtracted"));
        File.WriteAllText(Path.Combine(mod.Path, "LuaExtracted", "script.lua"), "lua");
        File.WriteAllText(Path.Combine(mod.Path, $"{BundleX}.patch-backup_0.hd2mm-backup"), "backup");

        var export = await HellForgeModExporter.ExportAsync(mod, null, ExportDir(mod));

        Assert.Equal(HellForgeModKind.Pack, export.Kind);
        Assert.Equal(Path.Combine(ExportDir(mod), "manifest.json"), export.ManifestPath);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(mod.Path, "manifest.json")),
            await File.ReadAllTextAsync(export.ManifestPath));
        Assert.True(File.Exists(Path.Combine(ExportDir(mod), "A", $"{BundleX}.patch_0")));
        Assert.True(File.Exists(Path.Combine(ExportDir(mod), "B", $"{BundleX}.patch_1")));
        Assert.True(File.Exists(Path.Combine(ExportDir(mod), ".jpgs", "icon.png")));
        Assert.True(File.Exists(Path.Combine(ExportDir(mod), ".jpgs", "a.png")));
        Assert.False(Directory.Exists(Path.Combine(ExportDir(mod), "LuaExtracted")));
        Assert.False(File.Exists(Path.Combine(ExportDir(mod), $"{BundleX}.patch-backup_0.hd2mm-backup")));
    }

    [Fact]
    public async Task ExportPack_IncludesDisabledOptionDirectories()
    {
        var mod = CreateMod(Guid.NewGuid(), V1Manifest(Option("A", "A"), Option("B", "B")));
        WritePatch(Path.Combine(mod.Path, "A"), BundleX, 0, "a");
        WritePatch(Path.Combine(mod.Path, "B"), BundleX, 1, "b");
        var optionsJson = new ProfileModOptionsState { OptionToggles = new List<bool> { true, false } }.ToJson();

        await HellForgeModExporter.ExportAsync(mod, optionsJson, ExportDir(mod));

        // B 在配置里禁用，但其目录仍须导出，供 HellForge Mods 页切换。
        Assert.True(File.Exists(Path.Combine(ExportDir(mod), "B", $"{BundleX}.patch_1")));
    }

    [Fact]
    public void ComputeFingerprint_PackIgnoresOptions_FlatDependsOnOptions()
    {
        var pack = CreateMod(Guid.NewGuid(), V1Manifest(Option("A", "A"), Option("B", "B")));
        WritePatch(Path.Combine(pack.Path, "A"), BundleX, 0, "a");
        WritePatch(Path.Combine(pack.Path, "B"), BundleX, 1, "b");
        var optionsA = new ProfileModOptionsState { OptionToggles = new List<bool> { true, true } }.ToJson();
        var optionsB = new ProfileModOptionsState { OptionToggles = new List<bool> { true, false } }.ToJson();
        Assert.Equal(
            HellForgeModExporter.ComputeFingerprint(pack, optionsA, HellForgeModKind.Pack),
            HellForgeModExporter.ComputeFingerprint(pack, optionsB, HellForgeModKind.Pack));

        var flat = CreateMod(Guid.NewGuid(), new ModManifest
        {
            Guid = Guid.NewGuid(), Name = "Legacy", ManifestVersion = ManifestVersion.Legacy,
            LegacyOptions = new List<string> { "One", "Two" }
        });
        WritePatch(Path.Combine(flat.Path, "One"), BundleX, 0, "one");
        WritePatch(Path.Combine(flat.Path, "Two"), BundleY, 0, "two");
        var legacyA = new ProfileModOptionsState { LegacySelectedIndex = 0 }.ToJson();
        var legacyB = new ProfileModOptionsState { LegacySelectedIndex = 1 }.ToJson();
        Assert.NotEqual(
            HellForgeModExporter.ComputeFingerprint(flat, legacyA, HellForgeModKind.Flat),
            HellForgeModExporter.ComputeFingerprint(flat, legacyB, HellForgeModKind.Flat));
    }

    [Fact]
    public async Task ExportFlat_NoPatches_Throws()
    {
        var mod = CreateMod(Guid.NewGuid(), new ModManifest { Guid = Guid.NewGuid(), Name = "Empty" });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HellForgeModExporter.ExportAsync(mod, null, ExportDir(mod)));
    }

    [Fact]
    public async Task ExportFlat_TruncatesNameAndVersion_DefaultsEmptyVersion()
    {
        var mod = CreateMod(Guid.NewGuid(), new ModManifest { Guid = Guid.NewGuid(), Name = "n" },
            name: new string('名', 100), version: new string('9', 50));
        WritePatch(mod.Path, BundleX, 0, "x");
        var export = await HellForgeModExporter.ExportAsync(mod, null, ExportDir(mod));
        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(export.ManifestPath)).RootElement;
        Assert.Equal(80, manifest.GetProperty("name").GetString()!.Length);
        Assert.Equal(32, manifest.GetProperty("version").GetString()!.Length);

        var emptyVersion = CreateMod(Guid.NewGuid(), new ModManifest { Guid = Guid.NewGuid(), Name = "v" }, version: "");
        WritePatch(emptyVersion.Path, BundleX, 0, "x");
        var export2 = await HellForgeModExporter.ExportAsync(emptyVersion, null, ExportDir(emptyVersion));
        var manifest2 = JsonDocument.Parse(await File.ReadAllTextAsync(export2.ManifestPath)).RootElement;
        Assert.Equal("1.0.0", manifest2.GetProperty("version").GetString());
    }

    private string ExportDir(Mod mod) => Path.Combine(_root, "export", mod.Id.ToString("N"));

    private Mod CreateMod(Guid id, ModManifest manifest, string name = "Test Mod", string version = "1.0.0")
    {
        var directory = Path.Combine(_root, "library", id.ToString("N"));
        Directory.CreateDirectory(directory);
        if (manifest.Guid == Guid.Empty) manifest.Guid = id;
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest));
        return new Mod
        {
            Id = id, Name = name, Version = version, Path = directory,
            Manifest = manifest, ThumbnailPath = string.Empty
        };
    }

    private static ModManifest V1Manifest(params ModOption[] options) => new()
    {
        Guid = Guid.NewGuid(), Name = "Pack", ManifestVersion = ManifestVersion.V1,
        Options = options.ToList()
    };

    private static ModOption Option(string name, string include) => new()
    {
        Name = name, Include = new List<string> { include }
    };

    private static void WritePatch(string directory, string bundle, int index, string content,
        bool stream = false, bool gpu = false)
    {
        Directory.CreateDirectory(directory);
        var main = Path.Combine(directory, $"{bundle}.patch_{index}");
        File.WriteAllText(main, content);
        if (stream) File.WriteAllText(main + ".stream", content + "-stream");
        if (gpu) File.WriteAllText(main + ".gpu_resources", content + "-gpu");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}