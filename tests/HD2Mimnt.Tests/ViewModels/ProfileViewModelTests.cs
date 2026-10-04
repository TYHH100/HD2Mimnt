using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Moq;

namespace HD2Mimnt.Tests.ViewModels;

public sealed class ProfileViewModelTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly IModRepository _modRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly ProfileService _profileService;
    private readonly ModService _modService;
    private readonly string _databasePath;
    private readonly string _storageDirectory;

    public ProfileViewModelTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"hd2mimnt-profile-vm-{Guid.NewGuid():N}.db");
        _storageDirectory = Path.Combine(Path.GetTempPath(), $"hd2mimnt-profile-vm-{Guid.NewGuid():N}");
        _dbContext = new HD2MimntDbContext(_databasePath);
        _dbContext.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _modRepository = new ModRepository(_dbContext);
        _profileRepository = new ProfileRepository(_dbContext);
        _profileService = new ProfileService(_profileRepository, _modRepository);
        _modService = new ModService(_modRepository, _storageDirectory);
    }

    [Fact]
    public async Task ToggleModEnabledCommand_ShouldPersistTheRequestedToggle()
    {
        var mod = await AddModAsync("Toggle Mod");
        var profileId = await _profileService.CreateProfileAsync("Active Profile");
        await _profileService.SetActiveProfileAsync(profileId);
        await _profileService.AddModToProfileAsync(profileId, mod.Id);

        var viewModel = CreateViewModel();
        await viewModel.LoadProfilesCommand.ExecuteAsync(null);
        var profileMod = Assert.Single(viewModel.ProfileMods);
        Assert.True(profileMod.Enabled);

        await viewModel.ToggleModEnabledCommand.ExecuteAsync(profileMod);

        Assert.False(profileMod.Enabled);
        var persisted = await _profileService.GetProfileByIdAsync(profileId);
        Assert.NotNull(persisted);
        Assert.False(Assert.Single(persisted.Mods).Enabled);
    }

    [Fact]
    public async Task SetActiveProfileCommand_ShouldRefreshSelectedProfileDetails()
    {
        var firstMod = await AddModAsync("First Mod");
        var secondMod = await AddModAsync("Second Mod");
        var firstProfileId = await _profileService.CreateProfileAsync("First Profile");
        var secondProfileId = await _profileService.CreateProfileAsync("Second Profile");
        await _profileService.SetActiveProfileAsync(firstProfileId);
        await _profileService.AddModToProfileAsync(firstProfileId, firstMod.Id);
        await _profileService.AddModToProfileAsync(secondProfileId, secondMod.Id);

        var viewModel = CreateViewModel();
        await viewModel.LoadProfilesCommand.ExecuteAsync(null);
        var secondProfile = Assert.Single(viewModel.Profiles, profile => profile.Id == secondProfileId);

        await viewModel.SetActiveProfileCommand.ExecuteAsync(secondProfile);

        Assert.Same(secondProfile, viewModel.SelectedProfile);
        var selectedMod = Assert.Single(viewModel.ProfileMods);
        Assert.Equal(secondMod.Id, selectedMod.ModId);
        Assert.Equal(secondProfileId, (await _profileService.GetActiveProfileAsync())!.Id);
    }

    private ProfileViewModel CreateViewModel()
    {
        var confirmation = new Mock<IConfirmationService>();
        confirmation.Setup(service => service.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        return new ProfileViewModel(
            _profileService,
            _modService,
            new NotificationService(),
            confirmation.Object);
    }

    private async Task<Mod> AddModAsync(string name)
    {
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = name,
            Version = "1.0.0",
            Path = Path.Combine(_storageDirectory, name.Replace(' ', '-'))
        };
        await _modRepository.AddAsync(mod);
        return mod;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        if (Directory.Exists(_storageDirectory))
        {
            Directory.Delete(_storageDirectory, recursive: true);
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
