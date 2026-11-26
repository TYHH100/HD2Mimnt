using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public class ModBinarySearchServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"binary-search-{Guid.NewGuid()}.db");
    private readonly HD2MimntDbContext _dbContext;
    private readonly ModRepository _modRepository;
    private readonly ProfileRepository _profileRepository;
    private readonly DeploymentRepository _deploymentRepository;
    private readonly ProfileService _profileService;
    private readonly ModBinarySearchService _service;

    public ModBinarySearchServiceTests()
    {
        _dbContext = new HD2MimntDbContext(_databasePath);
        _dbContext.InitializeDatabaseAsync().Wait();
        _modRepository = new ModRepository(_dbContext);
        _profileRepository = new ProfileRepository(_dbContext);
        _deploymentRepository = new DeploymentRepository(_dbContext);
        _profileService = new ProfileService(_profileRepository, _modRepository, deploymentRepository: _deploymentRepository);
        _service = new ModBinarySearchService(_profileRepository, _modRepository, _deploymentRepository, _profileService);
    }

    [Fact]
    public async Task StartAndPrepare_ShouldCopyOnlyEnabledModsAndChangeOnlyTemporaryEnabledStates()
    {
        var mods = await AddModsAsync(4);
        var disabled = await AddModAsync("Originally disabled");
        var loader = await AddModAsync("Loader");
        loader.BslRole = BslModRole.MdlLoader;
        loader.BslDetectionVersion = int.MaxValue;
        await _modRepository.UpdateAsync(loader);
        var source = await AddProfileAsync(mods.Append(loader).Concat([disabled]).ToList());

        var start = await _service.StartAsync(source.Id);
        var temporaryBeforeTest = await _profileRepository.GetByIdAsync(start.TemporaryProfileId);
        Assert.NotNull(temporaryBeforeTest);
        var expectedEnabled = source.Mods.Where(item => item.Enabled).OrderBy(item => item.Order).ToList();
        Assert.Equal(expectedEnabled.Count, temporaryBeforeTest.Mods.Count);
        Assert.Equal(expectedEnabled.Select(item => (item.ModId, item.OptionsJson)),
            temporaryBeforeTest.Mods.Select(item => (item.ModId, item.OptionsJson)));
        Assert.All(temporaryBeforeTest.Mods, item => Assert.True(item.Enabled));
        Assert.DoesNotContain(temporaryBeforeTest.Mods, item => item.ModId == disabled.Id);
        Assert.Equal(source.Separators.Select(item => (item.Name, item.Order, item.MemberCount, item.IsCollapsed, item.Color)),
            temporaryBeforeTest.Separators.Select(item => (item.Name, item.Order, item.MemberCount, item.IsCollapsed, item.Color)));
        Assert.Equal(5, start.TotalModCount);
        Assert.Equal(4, start.CandidateCount);
        Assert.Equal(["Loader"], start.ExcludedModNames);

        var test = await _service.PrepareFirstTestAsync();
        var temporary = await _profileRepository.GetByIdAsync(start.TemporaryProfileId);
        var reloadedSource = await _profileRepository.GetByIdAsync(source.Id);
        Assert.NotNull(temporary);
        Assert.NotNull(reloadedSource);
        Assert.Equal(2, test.DisabledCandidates.Count);
        Assert.Equal(expectedEnabled.Select(item => (item.ModId, item.OptionsJson)),
            temporary.Mods.Select(item => (item.ModId, item.OptionsJson)));
        Assert.Equal(source.Mods.Select(item => (item.ModId, item.Enabled)),
            reloadedSource.Mods.Select(item => (item.ModId, item.Enabled)));
        Assert.DoesNotContain(temporary.Mods, item => item.ModId == disabled.Id);
        Assert.True(temporary.Mods.Single(item => item.ModId == loader.Id).Enabled);
        Assert.All(test.DisabledCandidates, candidate =>
            Assert.False(temporary.Mods.Single(item => item.ModId == candidate.ModId).Enabled));
        Assert.All(test.EnabledCandidates, candidate =>
            Assert.True(temporary.Mods.Single(item => item.ModId == candidate.ModId).Enabled));
    }

    [Fact]
    public async Task EnabledSnapshot_ShouldExcludeDisabledModsAndRecalculateSeparatorBoundaries()
    {
        var mods = await AddModsAsync(6);
        var source = await AddProfileAsync(mods);
        source.Mods[1].Enabled = false;
        source.Mods[4].Enabled = false;
        source.Separators =
        [
            new ProfileSeparator { Id = Guid.NewGuid(), ProfileId = source.Id, Name = "First", Order = 1, MemberCount = 2 },
            new ProfileSeparator { Id = Guid.NewGuid(), ProfileId = source.Id, Name = "Second", Order = 4, MemberCount = 2 }
        ];
        await _profileRepository.UpdateAsync(source);

        var start = await _service.StartAsync(source.Id);
        var temporary = await _profileRepository.GetByIdAsync(start.TemporaryProfileId);
        Assert.NotNull(temporary);
        Assert.Equal(new[] { mods[0].Id, mods[2].Id, mods[3].Id, mods[5].Id }, temporary.Mods.Select(item => item.ModId));
        Assert.Equal(Enumerable.Range(0, 4), temporary.Mods.Select(item => item.Order));
        Assert.Equal(new[] { (1, 1), (3, 1) }, temporary.Separators.Select(item => (item.Order, item.MemberCount!.Value)));
        var reloadedSource = await _profileRepository.GetByIdAsync(source.Id);
        Assert.NotNull(reloadedSource);
        Assert.Equal(6, reloadedSource.Mods.Count);
        Assert.False(reloadedSource.Mods[1].Enabled);
        Assert.False(reloadedSource.Mods[4].Enabled);
    }

    [Fact]
    public async Task RecordObservation_ShouldRestoreKnownGoodModsAndVerifySingleSuspect()
    {
        var mods = await AddModsAsync(8);
        var source = await AddProfileAsync(mods);
        var start = await _service.StartAsync(source.Id);
        var first = await _service.PrepareFirstTestAsync();
        Assert.Equal(mods.Take(4).Select(mod => mod.Id), first.DisabledCandidates.Select(item => item.ModId));

        var secondStep = await _service.RecordObservationAsync(first.Id, problemRemains: true);
        var second = secondStep.Test!;
        Assert.Equal(mods.Take(2).Select(mod => mod.Id), second.DisabledCandidates.Select(item => item.ModId));
        var temporary = await _profileRepository.GetByIdAsync(start.TemporaryProfileId);
        Assert.NotNull(temporary);
        Assert.Equal(mods.Skip(2).Select(mod => mod.Id), temporary.Mods.Where(item => item.Enabled).Select(item => item.ModId));

        var third = (await _service.RecordObservationAsync(second.Id, problemRemains: false)).Test!;
        Assert.Equal([mods[2].Id], third.DisabledCandidates.Select(item => item.ModId));
        temporary = await _profileRepository.GetByIdAsync(start.TemporaryProfileId);
        Assert.NotNull(temporary);
        Assert.Equal(mods.Where(mod => mod.Id != mods[2].Id).Select(mod => mod.Id),
            temporary.Mods.Where(item => item.Enabled).Select(item => item.ModId));

        var final = (await _service.RecordObservationAsync(third.Id, problemRemains: true)).Test!;
        Assert.True(final.IsFinalVerification);
        Assert.Equal([mods[2].Id], final.DisabledCandidates.Select(item => item.ModId));
        temporary = await _profileRepository.GetByIdAsync(start.TemporaryProfileId);
        Assert.NotNull(temporary);
        Assert.Equal(mods.Where(mod => mod.Id != mods[2].Id).Select(mod => mod.Id),
            temporary.Mods.Where(item => item.Enabled).Select(item => item.ModId));

        var completed = await _service.RecordObservationAsync(final.Id, problemRemains: false);
        Assert.NotNull(completed.Result);
        Assert.True(completed.Result.Confirmed);
        Assert.Equal(mods[2].Id, completed.Result.Suspect!.ModId);
        Assert.Equal(4, completed.Result.History.Count);

        await _service.DeleteTemporaryProfileAsync(null);
        Assert.Null(await _profileRepository.GetByIdAsync(start.TemporaryProfileId));
        Assert.NotNull(await _profileRepository.GetByIdAsync(source.Id));
        var unchanged = await _profileRepository.GetByIdAsync(source.Id);
        Assert.NotNull(unchanged);
        Assert.All(unchanged.Mods, item => Assert.True(item.Enabled));
    }

    [Fact]
    public async Task FinalVerification_WhenProblemRemains_ShouldNotNameSuspect()
    {
        var mod = await AddModAsync("Only mod");
        var source = await AddProfileAsync([mod]);
        var start = await _service.StartAsync(source.Id);
        var test = await _service.PrepareFirstTestAsync();
        Assert.True(test.IsFinalVerification);

        var completed = await _service.RecordObservationAsync(test.Id, problemRemains: true);
        Assert.NotNull(completed.Result);
        Assert.False(completed.Result.Confirmed);
        Assert.Null(completed.Result.Suspect);
        await _service.DeleteTemporaryProfileAsync(null);
    }

    [Fact]
    public async Task DeleteTemporaryProfile_ShouldRefuseWhileTemporaryDeploymentRecordsRemain()
    {
        var mod = await AddModAsync("Mod");
        var source = await AddProfileAsync([mod]);
        var start = await _service.StartAsync(source.Id);
        await _service.PrepareFirstTestAsync();
        await _service.CancelAsync();
        await _deploymentRepository.AddAsync(new DeploymentRecord
        {
            Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), ProfileId = start.TemporaryProfileId,
            ModId = mod.Id, Workflow = "Default", DeploymentMethod = "Copy", GamePath = @"G:\Games\HD2",
            StartTime = DateTime.UtcNow, Status = "Completed"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.DeleteTemporaryProfileAsync(@"G:\Games\HD2"));
        Assert.NotNull(await _profileRepository.GetByIdAsync(start.TemporaryProfileId));
        Assert.Single(await _deploymentRepository.GetCurrentDeploymentsAsync(@"G:\Games\HD2"));
    }

    [Fact]
    public async Task Cleanup_ShouldRefuseToOverwriteSourceChangedDuringSession()
    {
        var mods = await AddModsAsync(2);
        var source = await AddProfileAsync(mods);
        var start = await _service.StartAsync(source.Id);
        await _service.PrepareFirstTestAsync();
        await _service.CancelAsync();
        await _profileService.SetModEnabledAsync(source.Id, mods[0].Id, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.EnsureSourceUnchangedAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteTemporaryProfileAsync(null));
        Assert.NotNull(await _profileRepository.GetByIdAsync(start.TemporaryProfileId));
    }

    private async Task<Mod> AddModAsync(string name)
    {
        var mod = new Mod { Id = Guid.NewGuid(), Name = name, Version = "1.0", Path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()) };
        await _modRepository.AddAsync(mod);
        return mod;
    }

    private async Task<List<Mod>> AddModsAsync(int count)
    {
        var mods = new List<Mod>();
        for (var index = 0; index < count; index++) mods.Add(await AddModAsync($"Mod {index}"));
        return mods;
    }

    private async Task<Profile> AddProfileAsync(List<Mod> mods)
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(), Name = "Source", CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow,
            Mods = mods.Select((mod, index) => new ProfileMod
            {
                ProfileId = Guid.Empty, ModId = mod.Id, Enabled = mod.Name != "Originally disabled",
                Order = index, OptionsJson = $"{{\"selection\":{index}}}"
            }).ToList(),
            Separators = [new ProfileSeparator
            {
                Id = Guid.NewGuid(), ProfileId = Guid.Empty, Name = "Group", Order = 1,
                MemberCount = 2, IsCollapsed = true, Color = "#112233"
            }]
        };
        foreach (var item in profile.Mods) item.ProfileId = profile.Id;
        foreach (var separator in profile.Separators) separator.ProfileId = profile.Id;
        await _profileRepository.AddAsync(profile);
        return profile;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_databasePath + suffix); } catch { }
        }
    }
}
