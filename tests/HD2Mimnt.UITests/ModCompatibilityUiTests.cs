using System.IO.Compression;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    private sealed class OfflineCompatibilityNames : ICompatibilityResourceNames
    {
        public Task<CompatibilityResourceNames> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompatibilityResourceNames { Source = "offline test fixture" });
    }
    private static async Task<Guid> ImportCompatibilityMod(UiScope scope, string name, bool brokenIndex = true)
    {
        var source = Path.Combine(scope.Root, "compat-source-" + Guid.NewGuid().ToString("N"));
        var patch = WritePatch(source, resources: [new(1, UnitType, Unit())]);
        if (brokenIndex)
        {
            var data = File.ReadAllBytes(patch); Put32(data, 104 + 76, 7); File.WriteAllBytes(patch, data);
        }
        File.WriteAllText(Path.Combine(source, "manifest.json"), "{\"Version\":1,\"Name\":\"" + name + "\"}");
        return await scope.Services.GetRequiredService<ModService>().ImportModAsync(source);
    }

    [AvaloniaFact]
    public async Task Compatibility_CardBadgesShareResultsSitBesideConflictAndOpenSingleMod()
    {
        await using var scope = await UiScope.CreateAsync(compatibilityNames: new OfflineCompatibilityNames());
        var id = await ImportCompatibilityMod(scope, "HD2Mimnt Test 兼容性角标 · 很长的模组名称验证省略布局");
        var game = Path.Combine(scope.Root, "game");
        WriteGame(game, new Resource(1, UnitType, Unit()));
        await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.GamePath, game);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        await profiles.InitializeAsync();
        var profileId = await profiles.CreateProfileAsync("Badge profile");
        await profiles.AddModToProfileAsync(profileId, id);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.LibraryViewModel.InitializeAsync(); await main.ProfileViewModel.LoadProfilesAsync();
        main.ProfileViewModel.SelectedProfile = main.ProfileViewModel.Profiles.Single(p => p.Id == profileId);
        await main.ProfileViewModel.RefreshAfterProfilesChangedAsync();
        var vm = main.Compatibility!;
        var libraryMod = main.LibraryViewModel.FilteredMods.Single(m => m.Id == id);
        Assert.False(libraryMod.CompatibilityBadge!.HasWarning);
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        var app = Avalonia.Application.Current!; var original = app.RequestedThemeVariant;
        try
        {
            window.Show();
            await vm.OpenAsync([libraryMod], libraryMod.Name);
            Assert.False(libraryMod.CompatibilityBadge.HasWarning);
            Assert.Equal(ModCompatibilityStatus.Compatible, vm.SelectedMod!.Report!.Status);
            Assert.True(vm.SelectedMod.Report.HasBlockingIssues);
            WriteGame(game, new Resource(1, UnitType, Unit(CurrentVersion + 1)));
            await vm.CheckCommand.ExecuteAsync(null);
            await vm.StopAsync();
            Assert.True(libraryMod.CompatibilityBadge.HasWarning);
            await main.ProfileViewModel.RetryProfileModsCommand.ExecuteAsync(null);
            var row = main.ProfileViewModel.ProfileMods.Single(m => m.ModId == id);
            Assert.Same(libraryMod.CompatibilityBadge, row.SourceInfo!.CompatibilityBadge);
            row.HasConflict = true; row.HasOptions = true;
            main.SelectedTabIndex = 0;
            var view = window.GetVisualDescendants().OfType<ProfileView>().Single();
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                app.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame();
                var badge = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ProfileCompatibilityBadge" && b.IsEffectivelyVisible);
                var conflict = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ProfileConflictBadge" && b.IsEffectivelyVisible);
                Assert.Equal(new Size(20, 20), badge.Bounds.Size);
                var right = badge.TranslatePoint(new Point(20, 0), window)!.Value;
                var left = conflict.TranslatePoint(default, window)!.Value;
                Assert.InRange(left.X - right.X, 2, 4); Assert.Equal(left.Y, right.Y);
                Assert.Equal("Wrench", badge.GetVisualDescendants().OfType<UtilityIcon>().Single().Kind);
                ToolTip.SetShowDelay(badge, 0);
                window.MouseMove(badge.TranslatePoint(new Point(10, 10), window)!.Value);
                await Task.Delay(100); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.True(ToolTip.GetIsOpen(badge));
                Assert.Contains("模型版本不匹配", Assert.IsType<TextBlock>(ToolTip.GetTip(badge)).Text);
                window.MouseMove(new Point(260, 560));
            }
            ClickVersionControl(window, view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenProfileCompatibilityButton" && b.IsEffectivelyVisible));
            await main.ProfileViewModel.CheckModCompatibilityCommand.ExecutionTask!;
            Assert.True(vm.IsOpen); Assert.Equal(id, Assert.Single(vm.Mods).Id); await vm.StopAsync();
            main.SelectedTabIndex = 1;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var libraryFrame = window.CaptureRenderedFrame();
            var library = window.GetVisualDescendants().OfType<LibraryView>().Single();
            ClickVersionControl(window, library.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenLibraryCompatibilityButton" && b.IsEffectivelyVisible));
            await main.LibraryViewModel.CheckModCompatibilityCommand.ExecutionTask!;
            Assert.True(vm.IsOpen); Assert.Equal(id, Assert.Single(vm.Mods).Id); await vm.StopAsync();
            libraryMod.CompatibilityBadge.ApplyReport(new() { ModId = id, Status = ModCompatibilityStatus.Compatible });
            Assert.False(row.SourceInfo.CompatibilityBadge!.HasWarning);
            libraryMod.CompatibilityBadge.ApplyReport(new() { ModId = id, Status = ModCompatibilityStatus.Unknown });
            Assert.False(row.SourceInfo.CompatibilityBadge.HasWarning); Assert.Contains("未确认", row.SourceInfo.CompatibilityBadge.Tooltip);
            libraryMod.CompatibilityBadge.ApplyReport(new() { ModId = id, Status = ModCompatibilityStatus.Error });
            Assert.False(row.SourceInfo.CompatibilityBadge.HasWarning);
            libraryMod.CompatibilityBadge.ApplyReport(new() { ModId = id, Status = ModCompatibilityStatus.NotApplicable });
            Assert.False(row.SourceInfo.CompatibilityBadge.HasWarning);
            vm.Invalidate(); Assert.False(libraryMod.CompatibilityBadge.HasWarning);
        }
        finally { await vm.StopAsync(); app.RequestedThemeVariant = original; window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Compatibility_ActualLibraryProfileAndDetailEntriesShareScopeAndRepairBackupFlow()
    {
        var confirmation = new RecordingConfirmation();
        await using var scope = await UiScope.CreateAsync(confirmation: confirmation, compatibilityNames: new OfflineCompatibilityNames());
        var id = await ImportCompatibilityMod(scope, "HD2Mimnt Test Compatibility");
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        await profiles.InitializeAsync();
        var profileId = await profiles.CreateProfileAsync("Compatibility profile");
        await profiles.AddModToProfileAsync(profileId, id);
        await profiles.SetActiveProfileAsync(profileId);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.LibraryViewModel.InitializeAsync(); await main.ProfileViewModel.LoadProfilesAsync();
        main.ProfileViewModel.SelectedProfile = main.ProfileViewModel.Profiles.Single(p => p.Id == profileId);
        await main.ProfileViewModel.RefreshAfterProfilesChangedAsync();
        main.SelectedTabIndex = 1;
        var vm = main.Compatibility!;
        Assert.Same(vm, main.LibraryViewModel.Compatibility); Assert.Same(vm, main.ProfileViewModel.Compatibility);
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 780 };
        try
        {
            window.Show();
            var library = window.GetVisualDescendants().OfType<LibraryView>().Single();
            ClickVersionControl(window, library.FindControl<Button>("CheckLibraryCompatibilityButton")!);
            await main.LibraryViewModel.CheckLibraryCompatibilityCommand.ExecutionTask!;
            Assert.True(vm.IsOpen); Assert.Equal(id, Assert.Single(vm.Mods).Id);
            Assert.NotNull(vm.SelectedMod!.Report); Assert.False(main.NavigateToProfilesCommand.CanExecute(null));
            var overlay = window.FindControl<ModCompatibilityOverlay>("CompatibilityOverlay")!;
            vm.RepairUnits = false;
            vm.SelectedTab = 1;
            ClickVersionControl(window, overlay.FindControl<Button>("PreviewSelectedCompatibilityButton")!);
            await vm.PreviewSelectedCommand.ExecutionTask!;
            Assert.True(vm.CanRepairSelected, vm.Message + " / " + string.Join("; ", vm.PlanDetails.Select(d => d.Text))); Assert.NotEmpty(vm.PlanDetails);
            vm.RecoverCompanions = false;
            Assert.False(vm.CanRepairSelected); Assert.Empty(vm.PlanDetails);
            ClickVersionControl(window, overlay.FindControl<Button>("PreviewSelectedCompatibilityButton")!);
            await vm.PreviewSelectedCommand.ExecutionTask!;
            ClickVersionControl(window, overlay.FindControl<Button>("RepairSelectedCompatibilityButton")!);
            await vm.RepairSelectedCommand.ExecutionTask!;
            Assert.Contains("重新部署", confirmation.Message);
            Assert.Contains("已修复", vm.SelectedOperation);
            Assert.False(vm.CanRepairSelected); Assert.NotEmpty(vm.Backups);
            var mod = (await scope.Services.GetRequiredService<ModService>().GetAllModsAsync()).Single(m => m.Id == id);
            var repaired = File.ReadAllBytes(Directory.GetFiles(mod.Path, "*.patch_0", SearchOption.AllDirectories).Single());
            Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(repaired.AsSpan(180)));
            vm.SelectedTab = 2;
            ClickVersionControl(window, overlay.FindControl<Button>("RestoreCompatibilityBackupButton")!);
            await vm.RestoreBackupCommand.ExecutionTask!;
            Assert.Contains("恢复完成", vm.Message);
            var restored = File.ReadAllBytes(Directory.GetFiles(mod.Path, "*.patch_0", SearchOption.AllDirectories).Single());
            Assert.Equal(7u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(restored.AsSpan(180)));
            ClickVersionControl(window, overlay.FindControl<Button>("CloseCompatibilityButton")!);
            await vm.CloseCommand.ExecutionTask!;
            Assert.False(vm.IsOpen); Assert.Empty(vm.Mods);
            main.SelectedTabIndex = 0;
            var profile = window.GetVisualDescendants().OfType<ProfileView>().Single();
            ClickVersionControl(window, profile.FindControl<Button>("CheckProfileCompatibilityButton")!);
            await main.ProfileViewModel.CheckProfileCompatibilityCommand.ExecutionTask!;
            Assert.Equal(id, Assert.Single(vm.Mods).Id);
            await vm.StopAsync();
            var row = main.ProfileViewModel.ProfileMods.Single(m => m.ModId == id);
            Assert.False(main.ProfileViewModel.LocalVersions.IsBusy);
            Assert.NotNull(row.SourceInfo); Assert.True(main.ProfileViewModel.ProfileMods.Contains(row));
            main.ProfileViewModel.ShowModDetailCommand.Execute(row);
            Assert.True(main.ProfileViewModel.IsDetailPanelOpen, "immediately after detail command");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var detailFrame = window.CaptureRenderedFrame();
            Assert.True(main.ProfileViewModel.IsDetailPanelOpen);
            var panel = profile.GetVisualDescendants().OfType<ModDetailPanel>().First(p => p.IsEffectivelyVisible);
            var detailButton = panel.FindControl<Button>("CheckDetailCompatibilityButton")!;
            detailButton.BringIntoView();
            ClickVersionControl(window, detailButton);
            await vm.Completion;
            Assert.True(vm.IsOpen); Assert.Equal(id, Assert.Single(vm.Mods).Id);
        }
        finally { await vm.StopAsync(); await main.ProfileViewModel.CloseModDetailAsync(); window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Compatibility_OverlayFitsMinimumWindowAndRelocalizesExistingResultsInBothThemes()
    {
        await using var scope = await UiScope.CreateAsync(confirmation: new RecordingConfirmation(), compatibilityNames: new OfflineCompatibilityNames());
        var id = await ImportCompatibilityMod(scope, "HD2Mimnt Test Long Compatibility Name 兼容性测试");
        var library = scope.Services.GetRequiredService<LibraryViewModel>(); await library.InitializeAsync();
        var vm = library.Compatibility!;
        var view = new ModCompatibilityOverlay { DataContext = vm };
        var window = new Window { Width = 960, Height = 600, Content = view };
        var app = Avalonia.Application.Current!; var original = app.RequestedThemeVariant;
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        HD2Mimnt.UI.Localization.UiLocalization.Current.Attach(language);
        try
        {
            window.Show(); await vm.OpenAsync(library.FilteredMods, "User scope 保持原文");
            await vm.PreviewSelectedCommand.ExecuteAsync(null);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                app.RequestedThemeVariant = theme; await language.SetLanguageAsync(code);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame();
                if (theme == ThemeVariant.Dark && code == "zh-CN" && Environment.GetEnvironmentVariable("HD2MIMNT_UI_SCREENSHOT") is { } screenshot) frame?.Save(screenshot);
                Assert.Equal(language["Compatibility.Title"], view.FindControl<TextBlock>("CompatibilityTitle")!.Text);
                Assert.Equal("User scope 保持原文", vm.ScopeName);
                Assert.Equal(id, Assert.Single(vm.Mods).Id);
                Assert.DoesNotContain("Compatibility.", vm.SelectedSummary);
                Assert.DoesNotContain("Compatibility.", string.Join(" ", vm.Details.Select(d => d.Text)));
                var surface = view.FindControl<Border>("CompatibilitySurface")!;
                Assert.Equal(1d, ((ISolidColorBrush)surface.Background!).Opacity);
                foreach (var tab in new[] { 0, 1, 2 })
                {
                    vm.SelectedTab = tab; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    var button = view.FindControl<Button>(tab switch { 0 => "RecheckCompatibilityButton", 1 => "RepairSelectedCompatibilityButton", _ => "RestoreCompatibilityBackupButton" })!;
                    var point = button.TranslatePoint(default, window)!.Value;
                    Assert.True(button.Bounds.Width > 10); Assert.InRange(point.Y + button.Bounds.Height, 0, window.Bounds.Height);
                    Assert.InRange(point.X + button.Bounds.Width, 0, window.Bounds.Width);
                }
            }
        }
        finally { await vm.StopAsync(); app.RequestedThemeVariant = original; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Compatibility_ProfileScopeIncludesCollapsedLockedLoadersAndBatchSelectionStaysExplicit()
    {
        await using var scope = await UiScope.CreateAsync(confirmation: new RecordingConfirmation(), compatibilityNames: new OfflineCompatibilityNames());
        var first = await ImportCompatibilityMod(scope, "HD2Mimnt Test scope first");
        var second = await ImportCompatibilityMod(scope, "HD2Mimnt Test scope second");
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var profileId = await profiles.CreateProfileAsync("Scope");
        await profiles.AddModToProfileAsync(profileId, first); await profiles.AddModToProfileAsync(profileId, second);
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var loader = (await repository.GetByIdAsync(first))!;
        loader.BslRole = HD2Mimnt.Core.Enums.BslModRole.Loader;
        loader.BslDetectionVersion = BslModDetector.DetectionVersion;
        await repository.UpdateAsync(loader);
        var separator = await profiles.AddSeparatorAsync(profileId, "Collapsed content", 0);
        await profiles.SetSeparatorCollapsedAsync(profileId, separator.Id, true);
        var profile = scope.Services.GetRequiredService<ProfileViewModel>();
        await profile.LoadProfilesAsync(); profile.SelectedProfile = profile.Profiles.Single(p => p.Id == profileId);
        await profile.RefreshAfterProfilesChangedAsync();
        var firstRow = profile.ProfileMods.Single(m => m.ModId == first);
        Assert.False(firstRow.CanDrag);
        var vm = profile.Compatibility!;
        await profile.CheckModCompatibilityCommand.ExecuteAsync(firstRow);
        Assert.Equal(first, Assert.Single(vm.Mods).Id); await vm.StopAsync();
        profile.ModSearchText = "no displayed matches";
        await profile.CheckProfileCompatibilityCommand.ExecuteAsync(null);
        Assert.Equal(new[] { first, second }.Order(), vm.Mods.Select(m => m.Id).Order()); await vm.StopAsync();
        var secondRow = profile.ProfileMods.Single(m => m.ModId == second); secondRow.IsSelected = true;
        await profile.CheckSelectedCompatibilityCommand.ExecuteAsync(null);
        Assert.Equal(second, Assert.Single(vm.Mods).Id); await vm.StopAsync();
        var library = scope.Services.GetRequiredService<LibraryViewModel>(); await library.InitializeAsync();
        library.SelectedMods.Add(library.FilteredMods.Single(m => m.Id == second));
        await library.CheckSelectedCompatibilityCommand.ExecuteAsync(null);
        Assert.Equal(second, Assert.Single(vm.Mods).Id); await vm.StopAsync();
    }

    [AvaloniaFact]
    public async Task Compatibility_MixedChoicesRemainPerModAndRecoveryErrorsSurviveRecheck()
    {
        await using var scope = await UiScope.CreateAsync(confirmation: new RecordingConfirmation(), compatibilityNames: new OfflineCompatibilityNames());
        await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.CompatibilityEnableBatchRepair, true);
        await ImportCompatibilityMod(scope, "HD2Mimnt Test mixed first");
        await ImportCompatibilityMod(scope, "HD2Mimnt Test mixed second");
        var library = scope.Services.GetRequiredService<LibraryViewModel>(); await library.InitializeAsync();
        var vm = library.Compatibility!;
        try
        {
            await vm.OpenAsync(library.FilteredMods, "Mixed"); vm.LodStrategy = CompatibilityLodStrategy.Mixed;
            vm.Mods[0].UnitChoices.Single().PreserveLod = true;
            Assert.False(vm.Mods[1].UnitChoices.Single().PreserveLod);
            vm.RepairUnits = false; await vm.PreviewScopeCommand.ExecuteAsync(null); Assert.True(vm.CanRepairScope);
            vm.Mods[0].UnitChoices.Single().PreserveLod = false; Assert.False(vm.CanRepairScope);
            vm.SelectedMod!.OperationError = "failed to restore: locked backup";
            vm.SelectedMod.OperationState = CompatibilityBatchState.Failed;
            await vm.CheckCommand.ExecuteAsync(null);
            Assert.Contains("locked backup", vm.SelectedOperation);
        }
        finally { await vm.StopAsync(); }
    }

    private sealed class BlockingCompatibilityAnalyzer : IModCompatibilityAnalyzer
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CleanupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cleaned;
        public async Task<ModCompatibilityReport> AnalyzeAsync(string modPath, uint? referenceVersion = null, Guid? modId = null, string? referenceSource = null, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { CleanupStarted.SetResult(); await ReleaseCleanup.Task; Cleaned = true; }
            return new();
        }
        public Task<CompatibilityRepairPlan> CreateRepairPlanAsync(string p, CancellationToken t = default) => throw new NotSupportedException();
        public Task<CompatibilityRepairResult> RepairAsync(string p, CancellationToken t = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetBackupsAsync(string p, CancellationToken t = default) => throw new NotSupportedException();
        public Task<CompatibilityRepairResult> RestoreBackupAsync(string p, string b, CancellationToken t = default) => throw new NotSupportedException();
        public Task<CompatibilityFullRepairPlan> CreateFullRepairPlanAsync(string p, string? g, IReadOnlyList<string> s, CompatibilityRepairOptions o, CancellationToken t = default) => throw new NotSupportedException();
        public Task<CompatibilityRepairResult> RepairFullAsync(string p, string? g, IReadOnlyList<string> s, CompatibilityRepairOptions o, CancellationToken t = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CompatibilityBackupEntry>> GetBackupHistoryAsync(string p, CancellationToken t = default) => throw new NotSupportedException();
        public Task<CompatibilityRepairResult> RollbackToAsync(string p, DateTime d, CancellationToken t = default) => throw new NotSupportedException();
        public Task<int> DeleteBackupAsync(string p, string b, CancellationToken t = default) => throw new NotSupportedException();
        public Task<int> CleanOldBackupsAsync(string p, int k = 3, CancellationToken t = default) => throw new NotSupportedException();
    }

    [AvaloniaFact]
    public async Task Compatibility_CloseCancelsAndWaitsForCleanupWhileUiRemainsResponsive()
    {
        var analyzer = new BlockingCompatibilityAnalyzer();
        await using var scope = await UiScope.CreateAsync(seedMods: true, compatibilityNames: new OfflineCompatibilityNames(), compatibilityAnalyzer: analyzer);
        var library = scope.Services.GetRequiredService<LibraryViewModel>(); await library.InitializeAsync();
        var vm = library.Compatibility!;
        var opening = vm.OpenAsync([library.FilteredMods[0], library.FilteredMods[0]], "selection");
        await analyzer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(vm.Mods); Assert.True(vm.IsBusy);
        var closing = vm.StopAsync();
        await analyzer.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(closing.IsCompleted); Assert.True(vm.IsOpen);
        var input = new TextBox(); var window = new Window { Content = input };
        try { window.Show(); input.Focus(); window.KeyTextInput("responsive"); Assert.Equal("responsive", input.Text); }
        finally { window.Close(); analyzer.ReleaseCleanup.TrySetResult(); }
        await closing; await opening;
        Assert.True(analyzer.Cleaned); Assert.False(vm.IsBusy); Assert.False(vm.IsOpen); Assert.Empty(vm.Mods);
    }
}
