using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Infrastructure.Darktide;

namespace HD2Mimnt.Tests.Darktide;

public sealed class DarktideGameLocatorTests
{
    private readonly DarktideGameLocator _locator = new();

    [Fact]
    public void Locate_CompleteDmlLayout_ReturnsLoaderInstalled()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();

        var state = _locator.Locate(env.GameRoot);

        Assert.Equal(DarktideGameStatus.LoaderInstalled, state.Status);
        Assert.True(state.HasGameExecutable);
        Assert.True(state.HasDmlCore);
        Assert.True(state.HasDmf);
        Assert.True(state.HasToggleScript);
        Assert.True(state.HasPatcher);
        Assert.True(state.HasLoadOrderFile);
        Assert.False(state.PatchStateKnown);
        Assert.Null(state.AlternativeLoader);
    }

    [Fact]
    public void Locate_GameWithoutDml_ReturnsGameWithoutLoader()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame(withDml: false);

        var state = _locator.Locate(env.GameRoot);

        Assert.Equal(DarktideGameStatus.GameWithoutLoader, state.Status);
        Assert.True(state.HasGameExecutable);
        Assert.False(state.HasDmlCore);
    }

    [Fact]
    public void Locate_PartialDml_ReturnsLoaderIncomplete()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        File.Delete(Path.Combine(env.GameRoot, "mods", "mod_load_order.txt"));

        var state = _locator.Locate(env.GameRoot);

        Assert.Equal(DarktideGameStatus.LoaderIncomplete, state.Status);
        Assert.False(state.HasLoadOrderFile);
    }

    [Fact]
    public void Locate_InvalidFolder_DoesNotCreateFiles()
    {
        using var env = new DarktideTestEnvironment();
        var invalid = Path.Combine(env.Root, "not-a-game");
        Directory.CreateDirectory(invalid);

        var state = _locator.Locate(invalid);

        Assert.Equal(DarktideGameStatus.InvalidPath, state.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(invalid));
    }

    [Fact]
    public void Locate_GamePassContentFolder_ResolvesActualRoot()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame(withDml: false);
        var packageRoot = Path.Combine(env.Root, "XboxGame");
        Directory.CreateDirectory(packageRoot);
        Directory.Move(env.GameRoot, Path.Combine(packageRoot, "content"));

        var state = _locator.Locate(packageRoot);

        Assert.NotEqual(DarktideGameStatus.InvalidPath, state.Status);
        Assert.Equal(Path.Combine(packageRoot, "content"), state.RootPath);
    }

    [Fact]
    public void Locate_AmlFolder_ReportsAlternativeLoader()
    {
        using var env = new DarktideTestEnvironment();
        env.CreateGame();
        Directory.CreateDirectory(Path.Combine(env.GameRoot, "mods", "AML"));
        File.WriteAllText(Path.Combine(env.GameRoot, "mods", "AML", "AML.mod"), "return {}");

        var state = _locator.Locate(env.GameRoot);

        Assert.Equal("AML", state.AlternativeLoader);
        Assert.Contains(state.Notes, note => note.Contains("AML", StringComparison.OrdinalIgnoreCase));
    }
}
