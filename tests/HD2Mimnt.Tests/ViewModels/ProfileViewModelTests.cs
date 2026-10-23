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
    [Fact]
    public async Task RenameProfile_ShouldOpenPrefilledDialogAndSaveConfirmedName()
    {
        await _profileService.CreateProfileAsync("Original profile");
        var vm = CreateViewModel();
        await vm.LoadProfilesAsync();
        var profile = Assert.Single(vm.Profiles);

        await vm.RenameProfileAsync(profile);

        Assert.True(vm.IsRenameProfileDialogOpen);
        Assert.Equal("Original profile", vm.RenameProfileName);
        vm.RenameProfileName = "Renamed profile";
        await vm.ConfirmRenameProfileCommand.ExecuteAsync(null);

        Assert.False(vm.IsRenameProfileDialogOpen);
        Assert.Equal("Renamed profile", Assert.Single(vm.Profiles).Name);
    }

    [Fact]
    public async Task ProfileRefresh_ShouldKeepCurrentCardsWhenOnlyAnotherProfileChanges()
    {
        var mod = await AddModAsync("Current Mod");
        var currentId = await _profileService.CreateProfileAsync("Current Profile");
        var otherId = await _profileService.CreateProfileAsync("Other Profile");
        await _profileService.AddModToProfileAsync(currentId, mod.Id);
        await _profileService.SetActiveProfileAsync(currentId);
        var vm = CreateViewModel();
        await vm.LoadProfilesAsync();
        var card = Assert.Single(vm.ProfileMods);

        await _profileService.RenameProfileAsync(otherId, "Renamed Other Profile");
        await vm.RefreshAfterProfilesChangedAsync(skipUnchangedProfile: true);

        Assert.Same(card, Assert.Single(vm.ProfileMods));
        Assert.Equal("Renamed Other Profile", vm.Profiles.Single(p => p.Id == otherId).Name);

        await _profileService.SetModEnabledAsync(currentId, mod.Id, false);
        await vm.RefreshAfterProfilesChangedAsync(skipUnchangedProfile: true);

        Assert.False(Assert.Single(vm.ProfileMods).Enabled);
    }

    [Fact]
    public void ConflictCards_ShouldKeepDenseRelationsSharedAndPreviewBounded()
    {
        var vm = CreateViewModel();
        var owners = Enumerable.Range(0, 1000).Select(index => new ConflictFileOwner
            { ModId = Guid.NewGuid(), ModName = $"Mod {index}" }).ToArray();
        var groups = Enumerable.Range(0, 100).Select(index => new ConflictGroupViewModel(new ConflictGroup
            { ResourceId = (ulong)index, Owners = owners })).ToArray();
        foreach (var owner in owners)
            vm.ProfileMods.Add(new ProfileModViewModel { ModId = owner.ModId, ModName = owner.ModName, Enabled = true });
        vm.ApplyConflictResults(groups);
        Assert.All(vm.ProfileMods, mod => Assert.True(mod.HasConflict));
        var card = vm.ProfileMods[0];
        Assert.Equal(999, card.ConflictTargets.Count());
        Assert.DoesNotContain(card.ModId, card.ConflictTargets.Select(target => target.ModId));
        Assert.Contains("查看全部关联模组", card.ConflictTooltip);
        Assert.True(card.ConflictTooltip.Length < 500);
        vm.ApplyConflictResults([]);
        Assert.Empty(card.ConflictTargets);
        Assert.Equal(string.Empty, card.ConflictTooltip);
    }

    [Fact]
    public void FocusMod_ShouldOnlyFocusAndAnchorVisibleRangesWithoutSelectingFixedCards()
    {
        var vm = CreateViewModel();
        var mods = Enumerable.Range(0, 3).Select(index => new ProfileModViewModel
            { ModId = Guid.NewGuid(), ModName = $"Mod {index}" }).ToArray();
        var loader = new ProfileModViewModel { BslRole = HD2Mimnt.Core.Enums.BslModRole.Loader };
        foreach (var mod in mods.Append(loader))
        {
            vm.ProfileMods.Add(mod);
            vm.VisibleProfileMods.Add(mod);
        }
        vm.ToggleModSelected(mods[2]);
        vm.FocusMod(mods[0]);
        Assert.Same(mods[0], vm.FocusedMod);
        Assert.False(vm.HasSelectedMods);
        Assert.True(mods[0].IsFocused);
        Assert.True(mods[0].IsHighlighted);
        vm.FocusMod(loader);
        vm.ToggleModSelected(loader);
        Assert.Same(mods[0], vm.FocusedMod);
        Assert.False(loader.IsSelected);
        Assert.False(loader.IsHighlighted);
        vm.SelectModRange(mods[2]);
        Assert.False(mods[0].IsFocused);
        Assert.Equal(mods, vm.ProfileMods.Where(mod => mod.IsSelected));
        vm.SelectModRange(mods[1]);
        Assert.Equal(mods.Take(2), vm.ProfileMods.Where(mod => mod.IsSelected));
        vm.VisibleProfileMods.Remove(mods[0]);
        vm.SelectModRange(mods[2]);
        Assert.Equal(mods[2], Assert.Single(vm.ProfileMods, mod => mod.IsSelected));
        vm.ClearModSelectionCommand.Execute(null);
        vm.SelectModRange(mods[1]);
        Assert.Equal(mods[1], Assert.Single(vm.ProfileMods, mod => mod.IsSelected));
    }

    [Fact]
    public void SelectModRange_ShouldRetainAnchorAndAppendToSelection()
    {
        var vm = CreateViewModel();
        var mods = Enumerable.Range(0, 5).Select(index => new ProfileModViewModel
            { ModId = Guid.NewGuid(), ModName = $"Mod {index}" }).ToArray();
        foreach (var mod in mods)
        {
            vm.ProfileMods.Add(mod);
            vm.VisibleProfileMods.Add(mod);
        }
        vm.SetModSelectionAnchor(mods[1]);
        vm.SelectModRange(mods[3]);
        Assert.Equal(mods.Skip(1).Take(3), mods.Where(mod => mod.IsSelected));
        vm.SelectModRange(mods[2]);
        Assert.Equal(mods.Skip(1).Take(2), mods.Where(mod => mod.IsSelected));
        mods[4].IsSelected = true;
        vm.SelectModRange(mods[0], additive: true);
        Assert.Equal(new[] { mods[0], mods[1], mods[2], mods[4] }, mods.Where(mod => mod.IsSelected));
    }

    [Fact]
    public void ModSelection_ShouldSkipFixedCardsAndKeepSelectableAnchor()
    {
        var vm = CreateViewModel();
        var mods = new[]
        {
            new ProfileModViewModel(),
            new ProfileModViewModel { BslRole = HD2Mimnt.Core.Enums.BslModRole.Dependency },
            new ProfileModViewModel { BslRole = HD2Mimnt.Core.Enums.BslModRole.Loader },
            new ProfileModViewModel { BslRole = HD2Mimnt.Core.Enums.BslModRole.MdlLoader },
            new ProfileModViewModel { BslRole = HD2Mimnt.Core.Enums.BslModRole.SmoothBoot },
            new ProfileModViewModel { IsSeparator = true },
            new ProfileModViewModel { BslRole = HD2Mimnt.Core.Enums.BslModRole.Dependency }
        };
        foreach (var mod in mods)
        {
            vm.ProfileMods.Add(mod);
            vm.VisibleProfileMods.Add(mod);
        }
        vm.SetModSelectionAnchor(mods[1]);
        vm.SetModSelectionAnchor(mods[2]);
        vm.SelectModRange(mods[^1]);
        Assert.Equal(new[] { mods[1], mods[^1] }, mods.Where(mod => mod.IsSelected));
        vm.SelectModRange(mods[4]);
        Assert.Equal(new[] { mods[1], mods[^1] }, mods.Where(mod => mod.IsSelected));
        vm.SelectAllVisibleMods();
        Assert.All(mods, mod => Assert.Equal(mod.CanSelect, mod.IsSelected));
    }

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
        _modService = new ModService(_modRepository, _storageDirectory, profileService: _profileService);
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
    public async Task MoveToPosition_ShouldMoveSelectedModToRequestedOneBasedSlot()
    {
        var mods = new[]
        {
            await AddModAsync("Position 1"), await AddModAsync("Position 2"),
            await AddModAsync("Position 3"), await AddModAsync("Position 4")
        };
        var profileId = await _profileService.CreateProfileAsync("Position Profile");
        await _profileService.SetActiveProfileAsync(profileId);
        foreach (var mod in mods) await _profileService.AddModToProfileAsync(profileId, mod.Id);

        var viewModel = CreateViewModel();
        await viewModel.LoadProfilesCommand.ExecuteAsync(null);
        var selected = viewModel.ProfileMods.Single(mod => mod.ModId == mods[1].Id);
        selected.IsSelected = true;
        viewModel.OpenMoveToPositionCommand.Execute(selected);
        viewModel.MoveToPositionText = "4";

        await viewModel.ConfirmMoveToPositionCommand.ExecuteAsync(null);

        Assert.Equal(new[] { mods[0].Id, mods[2].Id, mods[3].Id, mods[1].Id },
            viewModel.ProfileMods.Select(mod => mod.ModId));
        Assert.False(viewModel.IsMoveToPositionDialogOpen);
        Assert.Equal(4, viewModel.ProfileMods.Single(mod => mod.ModId == mods[1].Id).Position);
    }

    [Fact]
    public async Task SetActiveProfileAsync_ShouldRefreshSelectedProfileDetails()
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

        // 旧版 SetActiveProfileCommand 已由"选中即切换"语义替代，入口为公共方法 SetActiveProfileAsync
        await viewModel.SetActiveProfileAsync(secondProfile);

        Assert.Same(secondProfile, viewModel.SelectedProfile);
        Assert.True(secondProfile.IsActive);
        var selectedMod = Assert.Single(viewModel.ProfileMods);
        Assert.Equal(secondMod.Id, selectedMod.ModId);
        Assert.Equal(secondProfileId, (await _profileService.GetActiveProfileAsync())!.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LuaSeparator_ShouldFollowDirectionAndKeepMovesWithinEachGroup(bool bottomToTop)
    {
        var normal = await AddModAsync("Normal");
        var first = await AddModAsync("Dependency First");
        var second = await AddModAsync("Dependency Second");
        var loader = await AddModAsync("Loader");
        var smoothBoot = await AddModAsync("SmoothBoot");
        foreach (var (mod, role) in new[]
                 {
                     (first, HD2Mimnt.Core.Enums.BslModRole.Dependency),
                     (second, HD2Mimnt.Core.Enums.BslModRole.Dependency),
                     (loader, HD2Mimnt.Core.Enums.BslModRole.Loader),
                     (smoothBoot, HD2Mimnt.Core.Enums.BslModRole.SmoothBoot)
                 })
        {
            mod.BslRole = role;
            mod.BslDetectionVersion = HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectionVersion;
            await _modRepository.UpdateAsync(mod);
        }
        var profileId = await _profileService.CreateProfileAsync("Separator Profile");
        await _profileService.SetActiveProfileAsync(profileId);
        foreach (var mod in new[] { normal, first, second, loader, smoothBoot })
            await _profileService.AddModToProfileAsync(profileId, mod.Id);
        var settings = new SettingsService(_storageDirectory);
        await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, bottomToTop ? "BottomToTop" : "TopToBottom");
        var vm = CreateViewModel();
        await vm.LoadProfilesCommand.ExecuteAsync(null);

        Assert.Equal(bottomToTop
                ? new[] { smoothBoot.Id, loader.Id, second.Id, first.Id, Guid.Empty, normal.Id }
                : new[] { normal.Id, Guid.Empty, first.Id, second.Id, loader.Id, smoothBoot.Id },
            vm.ProfileRows.Select(row => row.ModId));
        Assert.Equal(bottomToTop ? -90 : 90,
            vm.ProfileRows.Single(row => row.ModId == Guid.Empty).ChevronAngle);
        Assert.Equal(!bottomToTop, vm.CanMoveMods(new[] { normal.Id }, Guid.Empty));
        Assert.Equal(bottomToTop, vm.CanMoveMods(new[] { second.Id }, Guid.Empty));
        Assert.False(vm.CanMoveMods(new[] { normal.Id }, first.Id));
        Assert.False(vm.CanMoveMods(new[] { first.Id }, normal.Id));

        // 反向依赖区域的末尾既能拖到分隔符，也能通过数字定位抵达。
        if (bottomToTop)
        {
            await vm.MoveModsAsync(new[] { second.Id }, Guid.Empty);
            Assert.Equal(new[] { smoothBoot.Id, loader.Id, first.Id, second.Id, Guid.Empty, normal.Id },
                vm.ProfileRows.Select(row => row.ModId));
            var moving = vm.ProfileMods.Single(mod => mod.ModId == first.Id);
            vm.OpenMoveToPositionCommand.Execute(moving);
            vm.MoveToPositionText = "4";
            await vm.ConfirmMoveToPositionCommand.ExecuteAsync(null);
            Assert.False(vm.IsMoveToPositionDialogOpen);
            Assert.Equal(new[] { smoothBoot.Id, loader.Id, second.Id, first.Id, Guid.Empty, normal.Id },
                vm.ProfileRows.Select(row => row.ModId));
            var persisted = await _profileService.GetProfileByIdAsync(profileId);
            Assert.Equal(new[] { normal.Id, first.Id, second.Id, loader.Id, smoothBoot.Id },
                persisted!.Mods.OrderBy(mod => mod.Order).Select(mod => mod.ModId));
        }

        vm.ModSearchText = "Dependency";
        Assert.Equal(bottomToTop ? new[] { second.Id, first.Id, Guid.Empty } : new[] { Guid.Empty, first.Id, second.Id },
            vm.ProfileRows.Select(row => row.ModId));
        vm.ModSearchText = "Normal";
        Assert.Equal(bottomToTop ? new[] { Guid.Empty, normal.Id } : new[] { normal.Id, Guid.Empty },
            vm.ProfileRows.Select(row => row.ModId));
        vm.ModSearchText = "";
        var systemGroup = vm.ProfileRows.Single(row => row.IsSeparator);
        Assert.Equal(4, systemGroup.GroupModCount);
        Assert.False(systemGroup.CanDrag);
        var before = (await _profileService.GetProfileByIdAsync(profileId))!.Mods
            .Select(item => (item.ModId, item.Enabled, item.Order, item.OptionsJson)).ToArray();
        vm.OpenRenameSeparatorCommand.Execute(systemGroup);
        Assert.False(vm.IsSeparatorNameEditable);
        vm.UseDefaultSeparatorColor = false;
        vm.SeparatorSelectedColor = Avalonia.Media.Color.Parse("#44AA77");
        await vm.ConfirmSeparatorCommand.ExecuteAsync(null);
        Assert.Equal("#44AA77", systemGroup.SeparatorColor);
        vm.SelectAllVisibleMods();
        await vm.ToggleSeparatorCommand.ExecuteAsync(systemGroup);
        Assert.True(systemGroup.IsCollapsed);
        Assert.Equal(normal.Id, Assert.Single(vm.VisibleProfileMods).ModId);
        Assert.All(vm.ProfileMods.Where(row => row.IsBslMember), row => Assert.False(row.IsSelected));
        Assert.Equal(!bottomToTop, vm.CanMoveMods(new[] { normal.Id }, Guid.Empty));
        Assert.Equal(before, (await _profileService.GetProfileByIdAsync(profileId))!.Mods
            .Select(item => (item.ModId, item.Enabled, item.Order, item.OptionsJson)));
        vm.ModSearchText = "Dependency";
        Assert.Equal(2, vm.VisibleProfileMods.Count);
        Assert.True(systemGroup.IsCollapsed);
        vm.ModSearchText = "";
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        systemGroup = vm.ProfileRows.Single(row => row.IsSeparator);
        Assert.True(systemGroup.IsCollapsed);
        Assert.Equal("#44AA77", systemGroup.SeparatorColor);
        await vm.ToggleSeparatorCommand.ExecuteAsync(systemGroup);
        Assert.Equal(5, vm.VisibleProfileMods.Count);
    }

    [Theory]
    [InlineData(HD2Mimnt.Core.Enums.BslModRole.Dependency)]
    [InlineData(HD2Mimnt.Core.Enums.BslModRole.Loader)]
    public async Task LuaSeparator_ShouldOnlyShowWhenDependencyOrLoaderIsInProfile(HD2Mimnt.Core.Enums.BslModRole role)
    {
        var mod = await AddModAsync("Ordinary mod");
        var profileId = await _profileService.CreateProfileAsync("Conditional Lua Separator");
        await _profileService.SetActiveProfileAsync(profileId);
        await _profileService.AddModToProfileAsync(profileId, mod.Id);

        var vm = CreateViewModel();
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.ProfileRows, row => row.ModId == Guid.Empty);

        mod.BslRole = role;
        mod.BslDetectionVersion = HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectionVersion;
        await _modRepository.UpdateAsync(mod);
        await vm.LoadProfilesCommand.ExecuteAsync(null);

        Assert.Contains(vm.ProfileRows, row => row.ModId == Guid.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveToPosition_WithLuaGroup_ShouldAllowOrdinaryBoundaryAndRejectCrossing(bool bottomToTop)
    {
        var normal = new List<Mod>();
        for (var index = 0; index < 4; index++) normal.Add(await AddModAsync($"Normal {index}"));
        var dependency = await AddModAsync("Dependency");
        dependency.BslRole = HD2Mimnt.Core.Enums.BslModRole.Dependency;
        dependency.BslDetectionVersion = HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectionVersion;
        await _modRepository.UpdateAsync(dependency);
        var loader = await AddModAsync("Loader");
        loader.BslRole = HD2Mimnt.Core.Enums.BslModRole.Loader;
        loader.BslDetectionVersion = HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectionVersion;
        await _modRepository.UpdateAsync(loader);
        var profileId = await _profileService.CreateProfileAsync("Lua Boundary");
        await _profileService.SetActiveProfileAsync(profileId);
        foreach (var mod in normal.Concat(new[] { dependency, loader }))
            await _profileService.AddModToProfileAsync(profileId, mod.Id);
        var settings = new SettingsService(_storageDirectory);
        await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, bottomToTop ? "BottomToTop" : "TopToBottom");
        var vm = CreateViewModel();
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        var selected = vm.ProfileMods.Single(mod => mod.ModId == normal[2].Id);
        vm.OpenMoveToPositionCommand.Execute(selected);
        vm.MoveToPositionText = bottomToTop ? "1" : "5";
        await vm.ConfirmMoveToPositionCommand.ExecuteAsync(null);
        Assert.True(vm.IsMoveToPositionDialogOpen);
        Assert.NotEmpty(vm.MoveToPositionError);
        vm.MoveToPositionText = bottomToTop ? "6" : "4";
        await vm.ConfirmMoveToPositionCommand.ExecuteAsync(null);
        Assert.False(vm.IsMoveToPositionDialogOpen);
        Assert.Empty(vm.MoveToPositionError);
        Assert.Equal(bottomToTop ? 6 : 4, selected.Position);
        var normalOrder = new[] { normal[0].Id, normal[1].Id, normal[3].Id, normal[2].Id };
        Assert.Equal(normalOrder, vm.ProfileMods.Where(mod => !mod.IsBslMember).Select(mod => mod.ModId));
        var persisted = await _profileService.GetProfileByIdAsync(profileId);
        Assert.Equal(normalOrder.Concat(new[] { dependency.Id, loader.Id }),
            persisted!.Mods.OrderBy(mod => mod.Order).Select(mod => mod.ModId));

        // 多选也允许整体移动到普通模组区域末尾，并保留相对顺序。
        foreach (var mod in vm.ProfileMods) mod.IsSelected = normal.Take(2).Any(item => item.Id == mod.ModId);
        vm.OpenMoveToPositionCommand.Execute(vm.ProfileMods.Single(mod => mod.ModId == normal[0].Id));
        vm.MoveToPositionText = bottomToTop ? "5" : "3";
        await vm.ConfirmMoveToPositionCommand.ExecuteAsync(null);
        Assert.False(vm.IsMoveToPositionDialogOpen);
        normalOrder = new[] { normal[3].Id, normal[2].Id, normal[0].Id, normal[1].Id };
        Assert.Equal(normalOrder, vm.ProfileMods.Where(mod => !mod.IsBslMember).Select(mod => mod.ModId));
        persisted = await _profileService.GetProfileByIdAsync(profileId);
        Assert.Equal(normalOrder.Concat(new[] { dependency.Id, loader.Id }),
            persisted!.Mods.OrderBy(mod => mod.Order).Select(mod => mod.ModId));
    }

    [Fact]
    public async Task OpenModOptions_WithSubOptions_ShouldBuildChoicesAndPersistSelection()
    {
        // Arrange：带 v1 选项与子选项（多选一变体）的模组
        var mod = await AddModAsync("Sub Option Mod");
        mod.Manifest = new ModManifest
        {
            Name = mod.Name,
            ManifestVersion = ManifestVersion.V1,
            Options = new List<ModOption>
            {
                new()
                {
                    Name = "上身",
                    Description = "替换上身",
                    Include = new List<string> { "上身" },
                    SubOptions = new List<ModSubOption>
                    {
                        new() { Name = "内衣", Include = new List<string> { "上身/内衣" } },
                        new() { Name = "空", Include = new List<string>() }
                    }
                },
                new() { Name = "本体", Include = new List<string> { "本体" } }
            }
        };
        await _modRepository.UpdateAsync(mod);

        var profileId = await _profileService.CreateProfileAsync("P");
        await _profileService.SetActiveProfileAsync(profileId);
        await _profileService.AddModToProfileAsync(profileId, mod.Id);

        var viewModel = CreateViewModel();
        await viewModel.LoadProfilesCommand.ExecuteAsync(null);

        // Assert：卡片带选项入口，覆盖层构建出选项与子选项
        var card = Assert.Single(viewModel.ProfileMods);
        Assert.True(card.HasOptions);

        viewModel.OpenModOptionsCommand.Execute(card);
        var dialog = viewModel.ModOptionsDialog;
        Assert.NotNull(dialog);
        Assert.False(dialog!.IsLegacyMode);
        Assert.Equal(2, dialog.OptionCards.Count);

        var cardWithSub = dialog.OptionCards[0];
        Assert.True(cardWithSub.HasSubOptions);
        Assert.Equal(new[] { "内衣", "空" }, cardWithSub.SubOptions.Select(sub => sub.Name));
        Assert.True(cardWithSub.SubOptions[0].IsSelected);
        Assert.False(dialog.OptionCards[1].HasSubOptions);

        // Act：点选第二个变体（多选一），应立即持久化
        dialog.SelectSubOption(cardWithSub.SubOptions[1]);

        var persisted = await _profileService.GetProfileByIdAsync(profileId);
        var state = ProfileModOptionsState.FromJson(Assert.Single(persisted!.Mods).OptionsJson);
        Assert.Equal(new List<int> { 1, 0 }, state.OptionSelections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserSeparators_ShouldCollapseSearchMoveGroupsAndRestoreWithoutChangingDeployment(bool bottomToTop)
    {
        var mods = new List<Mod>();
        for (var i = 0; i < 4; i++) mods.Add(await AddModAsync($"Normal {i}"));
        var loader = await AddModAsync("Loader");
        loader.BslRole = HD2Mimnt.Core.Enums.BslModRole.Loader;
        loader.BslDetectionVersion = HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectionVersion;
        await _modRepository.UpdateAsync(loader);
        var profileId = await _profileService.CreateProfileAsync("用户分组");
        await _profileService.SetActiveProfileAsync(profileId);
        foreach (var mod in mods.Append(loader)) await _profileService.AddModToProfileAsync(profileId, mod.Id);
        var first = await _profileService.AddSeparatorAsync(profileId, "武器", 0, "#F08020");
        var second = await _profileService.AddSeparatorAsync(profileId, "护甲", 2);
        var settings = new SettingsService(_storageDirectory);
        await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, bottomToTop ? "BottomToTop" : "TopToBottom");
        var vm = CreateViewModel();
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        var firstRow = vm.ProfileSeparators.First(row => row.ModId == first.Id);
        Assert.Equal("#F08020", firstRow.SeparatorColor);
        Assert.Equal(2, firstRow.GroupModCount);
        Assert.False(firstRow.CanSelect);
        var before = (await _profileService.GetProfileByIdAsync(profileId))!.Mods
            .Select(item => (item.ModId, item.Enabled, item.Order)).ToArray();
        vm.SelectAllVisibleMods();
        await vm.ToggleSeparatorCommand.ExecuteAsync(firstRow);
        Assert.True(firstRow.IsCollapsed);
        Assert.DoesNotContain(vm.VisibleProfileMods, row => row.ModId == mods[0].Id || row.ModId == mods[1].Id);
        Assert.False(vm.ProfileMods.First(row => row.ModId == mods[0].Id).IsSelected);
        Assert.Contains(vm.VisibleProfileMods, row => row.ModId == loader.Id);
        Assert.Equal(before, (await _profileService.GetProfileByIdAsync(profileId))!.Mods
            .Select(item => (item.ModId, item.Enabled, item.Order)));
        vm.ModSearchText = "Normal 1";
        Assert.Equal(mods[1].Id, Assert.Single(vm.VisibleProfileMods).ModId);
        Assert.Contains(firstRow, vm.ProfileRows);
        Assert.True(firstRow.IsCollapsed);
        vm.ModSearchText = "";
        Assert.False(vm.CanMoveMods(new[] { first.Id }, loader.Id));
        await vm.MoveModsAsync(new[] { second.Id }, first.Id);
        Assert.Equal(new[] { mods[2].Id, mods[3].Id, mods[0].Id, mods[1].Id },
            vm.ProfileMods.Where(row => !row.IsBslMember).Select(row => row.ModId));
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        Assert.Equal(new[] { second.Id, first.Id }, vm.ProfileSeparators.Select(row => row.ModId));
        firstRow = vm.ProfileSeparators.Single(row => row.ModId == first.Id);
        Assert.True(firstRow.IsCollapsed);
        Assert.Equal(new[] { mods[0].Id, mods[1].Id }, vm.GetSeparatorMembers(firstRow).Select(row => row.ModId));
        var last = vm.ProfileMods.Single(row => row.ModId == mods[1].Id);
        await vm.MoveModsAsync(new[] { last.ModId }, first.Id);
        Assert.Equal(3, vm.ProfileSeparators.Single(row => row.ModId == first.Id).Order);
        await vm.RemoveModFromProfileCommand.ExecuteAsync(vm.ProfileMods.Single(row => row.ModId == mods[2].Id));
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.ProfileSeparators.Single(row => row.ModId == first.Id).Order);
        await vm.RemoveSeparatorCommand.ExecuteAsync(vm.ProfileSeparators.Single(row => row.ModId == first.Id));
        Assert.Equal(4, vm.ProfileMods.Count);
        Assert.Single(vm.ProfileSeparators);
    }

    [Fact]
    public async Task GroupSlab_ShouldTintExpandedMembersWithSeparatorColorAndClearWhenCollapsed()
    {
        var mods = new List<Mod>();
        for (var i = 0; i < 4; i++) mods.Add(await AddModAsync($"Slab {i}"));
        var profileId = await _profileService.CreateProfileAsync("分组色板");
        await _profileService.SetActiveProfileAsync(profileId);
        foreach (var mod in mods) await _profileService.AddModToProfileAsync(profileId, mod.Id);
        var colored = await _profileService.AddSeparatorAsync(profileId, "有色", 1, "#F08020");
        await _profileService.AddSeparatorAsync(profileId, "主题色", 3);
        var vm = CreateViewModel();
        await vm.LoadProfilesCommand.ExecuteAsync(null);

        var first = vm.ProfileMods.Single(row => row.ModId == mods[1].Id);
        var second = vm.ProfileMods.Single(row => row.ModId == mods[2].Id);
        var outside = vm.ProfileMods.Single(row => row.ModId == mods[0].Id);
        var themed = vm.ProfileMods.Single(row => row.ModId == mods[3].Id);

        Assert.True(first.IsInsideGroup);
        var tint = Assert.IsType<Avalonia.Media.Immutable.ImmutableSolidColorBrush>(first.GroupTintBrush);
        Assert.Equal(0xF0, tint.Color.R);
        Assert.Equal(0x80, tint.Color.G);
        Assert.Equal(0x20, tint.Color.B);
        Assert.Equal(38, tint.Color.A);
        Assert.True(first.IsSlabStart);
        Assert.False(first.IsSlabEnd);
        Assert.True(second.IsInsideGroup);
        Assert.False(second.IsSlabStart);
        Assert.True(second.IsSlabEnd);
        Assert.False(outside.IsInsideGroup);
        Assert.Null(outside.GroupTintBrush);
        Assert.True(themed.IsInsideGroup);
        Assert.Null(themed.GroupTintBrush);
        Assert.True(themed.IsSlabStart && themed.IsSlabEnd);

        var separatorRow = vm.ProfileSeparators.Single(row => row.ModId == colored.Id);
        await vm.ToggleSeparatorCommand.ExecuteAsync(separatorRow);
        Assert.True(separatorRow.IsCollapsed);
        Assert.DoesNotContain(vm.ProfileRows, row => row.ModId == mods[1].Id || row.ModId == mods[2].Id);
        await vm.ToggleSeparatorCommand.ExecuteAsync(separatorRow);
        first = vm.ProfileMods.Single(row => row.ModId == mods[1].Id);
        second = vm.ProfileMods.Single(row => row.ModId == mods[2].Id);
        Assert.True(first.IsInsideGroup && first.IsSlabStart && !first.IsSlabEnd);
        Assert.True(second.IsInsideGroup && second.IsSlabEnd && !second.IsSlabStart);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CollapsedSeparator_ShouldKeepOriginalMembersWhenMovedOrDroppedBelow(bool bottomToTop, bool legacy)
    {
        var mods = new List<Mod>();
        for (var i = 0; i < 4; i++) mods.Add(await AddModAsync($"Bounded {i}"));
        var profileId = await _profileService.CreateProfileAsync("收起分组边界");
        await _profileService.SetActiveProfileAsync(profileId);
        foreach (var mod in mods) await _profileService.AddModToProfileAsync(profileId, mod.Id);
        var separator = await _profileService.AddSeparatorAsync(profileId, "原有成员", 2);
        await _profileService.SetSeparatorCollapsedAsync(profileId, separator.Id, true);
        if (legacy)
        {
            using var ownedConnection = await _dbContext.OpenConnectionAsync();
            using var command = ownedConnection.CreateCommand();
            command.CommandText = "UPDATE ProfileSeparators SET MemberCount = NULL WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", separator.Id.ToString());
            await command.ExecuteNonQueryAsync();
        }
        await new SettingsService(_storageDirectory).SetSettingAsync(SettingKeys.DefaultDeploymentOrder,
            bottomToTop ? "BottomToTop" : "TopToBottom");
        var vm = CreateViewModel();
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        // 将收起的末尾分组搬到组外模组上方，不能顺带吸收原落点之后的模组。
        await vm.MoveModsAsync(new[] { separator.Id }, mods[0].Id);
        var group = Assert.Single(vm.ProfileSeparators);
        Assert.Equal(new[] { mods[2].Id, mods[3].Id }, vm.GetSeparatorMembers(group).Select(row => row.ModId));
        Assert.Equal(new[] { mods[0].Id, mods[1].Id }, vm.VisibleProfileMods.Select(row => row.ModId));
        // 下方的插入点保持组外，且不改变既有隐藏成员和它们的相对顺序。
        await vm.MoveModsAsync(new[] { mods[1].Id }, mods[0].Id);
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        group = Assert.Single(vm.ProfileSeparators);
        Assert.True(group.IsCollapsed);
        Assert.Equal(2, group.MemberCount);
        Assert.Equal(new[] { mods[1].Id, mods[0].Id }, vm.VisibleProfileMods.Select(row => row.ModId));
        Assert.Equal(new[] { mods[2].Id, mods[3].Id, mods[1].Id, mods[0].Id },
            (await _profileService.GetProfileByIdAsync(profileId))!.Mods.OrderBy(row => row.Order).Select(row => row.ModId));
        // 展开后可以将模组拖入成员范围；再移出和删除时同步缩短边界。
        await vm.ToggleSeparatorCommand.ExecuteAsync(group);
        Assert.Equal(2, vm.GetSeparatorMembers(group).Count);
        await vm.MoveModsAsync(new[] { mods[1].Id }, mods[3].Id);
        Assert.Equal(3, vm.GetSeparatorMembers(group).Count);
        await vm.MoveModsAsync(new[] { mods[1].Id }, separator.Id);
        Assert.Equal(2, vm.GetSeparatorMembers(group).Count);
        await vm.RemoveModFromProfileCommand.ExecuteAsync(vm.ProfileMods.Single(row => row.ModId == mods[3].Id));
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        group = Assert.Single(vm.ProfileSeparators);
        Assert.Equal(mods[2].Id, Assert.Single(vm.GetSeparatorMembers(group)).ModId);
        vm.ModSearchText = group.ModName;
        Assert.Equal(mods[2].Id, Assert.Single(vm.VisibleProfileMods).ModId);
    }

    [Theory]
    [InlineData(false, false, "single")]
    [InlineData(true, false, "single")]
    [InlineData(false, true, "single")]
    [InlineData(true, true, "single")]
    [InlineData(false, false, "batch")]
    [InlineData(true, false, "batch")]
    [InlineData(false, true, "batch")]
    [InlineData(true, true, "batch")]
    [InlineData(false, false, "delete")]
    [InlineData(true, false, "delete")]
    [InlineData(false, true, "delete")]
    [InlineData(true, true, "delete")]
    public async Task ModRemoval_ShouldPreserveSeparatorMembersAndEmptyGroupsAfterReload(
        bool bottomToTop, bool collapsed, string operation)
    {
        var mods = new List<Mod>();
        for (var i = 0; i < 6; i++) mods.Add(await AddModAsync($"Removal {i}"));
        var profileId = await _profileService.CreateProfileAsync("移除后保留分组");
        await _profileService.SetActiveProfileAsync(profileId);
        foreach (var mod in mods) await _profileService.AddModToProfileAsync(profileId, mod.Id);
        var first = await _profileService.AddSeparatorAsync(profileId, "武器", 1);
        var second = await _profileService.AddSeparatorAsync(profileId, "护甲", 3);
        var last = await _profileService.AddSeparatorAsync(profileId, "其它", 5);
        if (collapsed)
            foreach (var separator in new[] { first, second, last })
                await _profileService.SetSeparatorCollapsedAsync(profileId, separator.Id, true);
        var copy = await _profileService.DuplicateProfileAsync(profileId, "其它配置中的同一模组");
        await new SettingsService(_storageDirectory).SetSettingAsync(SettingKeys.DefaultDeploymentOrder,
            bottomToTop ? "BottomToTop" : "TopToBottom");
        var vm = CreateViewModel();
        await vm.LoadProfilesCommand.ExecuteAsync(null);
        var removed = new HashSet<Guid>();
        void AssertGroups()
        {
            foreach (var (id, members) in new[]
            {
                (first.Id, mods.Skip(1).Take(2)), (second.Id, mods.Skip(3).Take(2)), (last.Id, mods.Skip(5))
            })
            {
                var row = vm.ProfileSeparators.Single(item => item.ModId == id);
                var expected = members.Where(mod => !removed.Contains(mod.Id)).Select(mod => mod.Id).ToArray();
                Assert.Equal(expected, vm.GetSeparatorMembers(row).Select(item => item.ModId));
                Assert.Equal(expected.Length, row.GroupModCount);
                Assert.Equal(collapsed, row.IsCollapsed);
            }
        }
        AssertGroups();
        // 依次移除分组前的模组、首组全部成员及下一组首项；空组不能吸收相邻成员。
        var batches = operation == "batch" ? new[] { mods.Take(4).ToArray() }
            : mods.Take(4).Select(mod => new[] { mod }).ToArray();
        foreach (var batch in batches)
        {
            if (operation == "batch")
            {
                vm.ModSearchText = "Removal";
                foreach (var mod in batch) vm.ToggleModSelected(vm.ProfileMods.Single(row => row.ModId == mod.Id));
                await vm.RemoveSelectedModsCommand.ExecuteAsync(null);
                vm.ModSearchText = "";
            }
            else if (operation == "delete")
            {
                await vm.DeleteModCommand.ExecuteAsync(vm.ProfileMods.Single(row => row.ModId == batch[0].Id));
                await vm.LoadProfilesCommand.ExecuteAsync(null);
            }
            else
                await vm.RemoveModFromProfileCommand.ExecuteAsync(vm.ProfileMods.Single(row => row.ModId == batch[0].Id));
            removed.UnionWith(batch.Select(mod => mod.Id));
            AssertGroups();
            await vm.LoadProfilesCommand.ExecuteAsync(null);
            AssertGroups();
        }
        var copied = (await _profileService.GetProfileByIdAsync(copy.Id))!;
        var expectedCopy = operation == "delete" ? mods.Where(mod => !removed.Contains(mod.Id)) : mods;
        Assert.Equal(expectedCopy.Select(mod => mod.Id), copied.Mods.Select(mod => mod.ModId));
        Assert.Equal(operation == "delete" ? new[] { 0, 0, 1 } : new[] { 1, 3, 5 },
            copied.Separators.Select(separator => separator.Order));
        if (collapsed)
            Assert.Equal(operation == "delete" ? new int?[] { 0, 1, 1 } : new int?[] { 2, 2, 1 },
                copied.Separators.Select(separator => separator.MemberCount));
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
            confirmation.Object,
            new SettingsService(_storageDirectory));
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
