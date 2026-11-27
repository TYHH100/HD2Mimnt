using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public sealed class ModelToIasProfileReplacementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-modeltoias-profile-{Guid.NewGuid():N}");
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _mods;
    private readonly ProfileRepository _profiles;
    private readonly ProfileService _service;

    public ModelToIasProfileReplacementTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _mods = new(_db);
        _profiles = new(_db);
        _service = new(_profiles, _mods);
    }

    [Fact]
    public async Task ReplaceModWith_EnablesReplacementAndDisablesSourceInOneUpdate()
    {
        var source = NewMod("Source");
        var replacement = NewMod("IAS");
        await _mods.AddAsync(source);
        await _mods.AddAsync(replacement);
        var profile = new Profile { Id = Guid.NewGuid(), Name = "Default", IsDefault = true };
        profile.Mods.Add(new ProfileMod { ProfileId = profile.Id, ModId = source.Id, Enabled = true, Order = 0 });
        await _profiles.AddAsync(profile);

        await _service.ReplaceModWithAsync(profile.Id, source.Id, replacement.Id);

        var saved = await _profiles.GetByIdAsync(profile.Id);
        Assert.False(saved!.Mods.Single(item => item.ModId == source.Id).Enabled);
        Assert.True(saved.Mods.Single(item => item.ModId == replacement.Id).Enabled);
    }

    [Fact]
    public async Task ReplaceModWith_MissingReplacementLeavesSourceEnabled()
    {
        var source = NewMod("Source");
        await _mods.AddAsync(source);
        var profile = new Profile { Id = Guid.NewGuid(), Name = "Default", IsDefault = true };
        profile.Mods.Add(new ProfileMod { ProfileId = profile.Id, ModId = source.Id, Enabled = true, Order = 0 });
        await _profiles.AddAsync(profile);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ReplaceModWithAsync(profile.Id, source.Id, Guid.NewGuid()));

        var saved = await _profiles.GetByIdAsync(profile.Id);
        Assert.True(saved!.Mods.Single(item => item.ModId == source.Id).Enabled);
    }

    private static Mod NewMod(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Path = Path.Combine(Path.GetTempPath(), name),
        Manifest = new() { Name = name }
    };

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
