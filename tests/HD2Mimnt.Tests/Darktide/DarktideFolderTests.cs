using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Infrastructure.Darktide;

namespace HD2Mimnt.Tests.Darktide;

public sealed class DarktideFolderTests
{
    private readonly DarktideFolderInspector _inspector = new();

    [Fact]
    public void Inspect_ModRootFolder_ReturnsSingleCandidate()
    {
        using var env = new DarktideTestEnvironment();
        var folder = env.CreateModFolder("MyMod");

        var result = _inspector.Inspect(folder);

        Assert.Null(result.Error);
        Assert.Equal(DarktideArchivePackageKind.StandardMod, result.Kind);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("", candidate.RelativeRoot);
        Assert.Equal("MyMod", candidate.InternalName);
        Assert.True(candidate.HasScripts);
        Assert.True(candidate.HasInfoJson);
    }

    [Fact]
    public void Inspect_CollectionFolder_ReturnsBundle()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateModFolder("Alpha");
        env.CreateModFolder("Beta");

        var result = _inspector.Inspect(Path.Combine(env.Root, "sources"));

        Assert.Null(result.Error);
        Assert.Equal(DarktideArchivePackageKind.ModBundle, result.Kind);
        Assert.Equal(new[] { "Alpha", "Beta" }, result.Candidates.Select(candidate => candidate.InternalName));
    }

    [Fact]
    public void Inspect_ExecutableOutsideCandidates_RequiresCustomInstall()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateModFolder("Alpha");
        File.WriteAllText(Path.Combine(env.Root, "sources", "patcher.exe"), "binary");

        var result = _inspector.Inspect(Path.Combine(env.Root, "sources"));

        Assert.Null(result.Error);
        Assert.Equal(DarktideArchivePackageKind.CustomInstallRequired, result.Kind);
        Assert.Contains(result.ExtraFiles, path => path.EndsWith("patcher.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Inspect_FolderWithoutModStructure_ReturnsError()
    {
        using var env = new DarktideTestEnvironment();
        var folder = Path.Combine(env.Root, "empty");
        Directory.CreateDirectory(folder);

        var result = _inspector.Inspect(folder);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Inspect_MissingFolder_ReturnsError()
    {
        using var env = new DarktideTestEnvironment();

        var result = _inspector.Inspect(Path.Combine(env.Root, "missing"));

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Inspect_SymlinkInsideFolder_ReturnsError()
    {
        using var env = new DarktideTestEnvironment();
        var folder = env.CreateModFolder("Alpha");
        var outside = Path.Combine(env.Root, "outside.txt");
        File.WriteAllText(outside, "outside");
        var link = Path.Combine(folder, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Windows 未开启开发者模式/无符号链接权限时跳过。
        }

        var result = _inspector.Inspect(folder);

        Assert.NotNull(result.Error);
        Assert.Contains("符号链接", result.Error);
    }

    [Fact]
    public async Task CreateArchiveAsync_ProducesReinstallableStructure()
    {
        using var env = new DarktideTestEnvironment();
        var folder = env.CreateModFolder("Alpha");
        var zip = Path.Combine(env.Root, "Alpha.zip");

        await _inspector.CreateArchiveAsync(folder, "Alpha", zip);

        var inspection = new DarktideArchiveInspector().Inspect(zip);
        Assert.Null(inspection.Error);
        var candidate = Assert.Single(inspection.Candidates);
        Assert.Equal("Alpha", candidate.RelativeRoot);
        Assert.Equal("Alpha", candidate.InternalName);
    }

    [Fact]
    public async Task CreateArchiveAsync_SameContent_ProducesSameBytes()
    {
        using var env = new DarktideTestEnvironment();
        var folder = env.CreateModFolder("Alpha");
        var first = Path.Combine(env.Root, "first.zip");
        var second = Path.Combine(env.Root, "second.zip");

        await _inspector.CreateArchiveAsync(folder, "Alpha", first);
        await _inspector.CreateArchiveAsync(folder, "Alpha", second);

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
    }
}