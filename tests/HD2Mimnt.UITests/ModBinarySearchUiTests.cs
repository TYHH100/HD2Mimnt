using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModBinarySearch_ShouldUseTemporaryProfileVerifySuspectAndRestoreOriginal()
    {
        await using var scope = await UiScope.CreateAsync(confirmation: new RecordingConfirmation(),
            workerHostManagerFactory: services => new LoopbackWorkerHost(services));
        var generated = Enumerable.Range(1, 4).Select(index => new Mod
        {
            Name = $"HD2Mimnt Test Binary {index}", Author = "Helldivers", Version = "1.0.0",
            Path = Path.Combine(scope.Root, "sources", index.ToString())
        }).ToList();
        foreach (var mod in generated) WritePatch(mod.Path);
        await scope.Services.GetRequiredService<TestDataService>().ImportGeneratedModsAsync(generated);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var profileId = await profiles.CreateProfileAsync("Binary search source");
        await profiles.SetActiveProfileAsync(profileId);
        var importedMods = (await scope.Services.GetRequiredService<TestDataService>().GetAllModsAsync()).ToList();
        foreach (var mod in importedMods)
            await profiles.AddModToProfileAsync(profileId, mod.Id);
        await profiles.SetModEnabledAsync(profileId, importedMods[^1].Id, false);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        await language.SetLanguageAsync("zh-CN");
        var settings = scope.Services.GetRequiredService<SettingsService>();
        var gamePath = Path.Combine(scope.Root, "game");
        Directory.CreateDirectory(gamePath);
        await settings.SetSettingAsync(SettingKeys.GamePath, gamePath);
        var sourceId = (await profiles.GetActiveProfileAsync())!.Id;
        var vm = main.ModBinarySearchViewModel!;
        main.SelectedTabIndex = 3;
        main.MoreViewModel.SelectedCategory = "模组二分排查";
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 820 };
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            void Click(Control control)
            {
                control.BringIntoView();
                Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                Assert.True(control.IsEffectivelyVisible);
                Assert.True(control.IsVisualAncestorOf((Visual)window.InputHitTest(point)!) || ReferenceEquals(control, window.InputHitTest(point)));
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
            }

            Layout();
            main.NotificationPanelViewModel.DismissAllToasts();
            var more = window.GetVisualDescendants().OfType<MoreView>().Single();
            Assert.True(main.MoreViewModel.IsModBinarySearchVisible);
            Click(more.FindControl<Button>("StartModBinarySearchButton")!);
            await vm.StartCommand.ExecutionTask!;
            Layout();
            Assert.Equal(ModBinarySearchStage.ReadyToDeploy, vm.Stage);
            Assert.Equal(3, vm.TotalModCount);
            Assert.Equal(3, vm.CandidateCount);
            var temporary = (await profiles.GetAllProfilesAsync()).Single(profile => profile.Id != sourceId);
            Assert.Equal(vm.TemporaryProfileName, temporary.Name);
            Assert.Equal(3, temporary.Mods.Count);

            Click(more.FindControl<Button>("DeployModBinarySearchTestButton")!);
            await vm.DeployCurrentTestCommand.ExecutionTask!;
            Layout();
            Assert.True(vm.Stage == ModBinarySearchStage.AwaitingObservation, vm.StatusMessage);
            Assert.Empty(vm.History);
            var repository = scope.Services.GetRequiredService<IDeploymentRepository>();
            Assert.All(await repository.GetCurrentDeploymentsAsync(gamePath), record => Assert.Equal(temporary.Id, record.ProfileId));

            Click(more.FindControl<Button>("ModBinarySearchProblemGoneButton")!);
            await vm.ProblemGoneCommand.ExecutionTask!;
            Layout();
            Assert.Equal(2, vm.CurrentTest!.DisabledCandidates.Count + vm.CurrentTest.EnabledCandidates.Count);
            Click(more.FindControl<Button>("DeployModBinarySearchTestButton")!);
            await vm.DeployCurrentTestCommand.ExecutionTask!;

            Click(more.FindControl<Button>("ModBinarySearchProblemRemainsButton")!);
            await vm.ProblemRemainsCommand.ExecutionTask!;
            Layout();
            Assert.True(vm.CurrentTest!.IsFinalVerification);
            Click(more.FindControl<Button>("DeployModBinarySearchTestButton")!);
            await vm.DeployCurrentTestCommand.ExecutionTask!;

            Click(more.FindControl<Button>("ModBinarySearchProblemGoneButton")!);
            await vm.ProblemGoneCommand.ExecutionTask!;
            Layout();
            Assert.Equal(ModBinarySearchStage.AwaitingCleanup, vm.Stage);
            Assert.False(vm.CanDisableSuspect);
            Assert.True(vm.Result!.Confirmed);
            Assert.NotNull(vm.Result.Suspect);
            var suspectId = vm.Result.Suspect.ModId;
            Assert.Equal(3, vm.History.Count);
            Click(more.FindControl<Button>("FinishModBinarySearchButton")!);
            await vm.FinishCleanupCommand.ExecutionTask!;
            Layout();
            Assert.Equal(ModBinarySearchStage.Completed, vm.Stage);
            Assert.Null(await profiles.GetProfileByIdAsync(temporary.Id));
            var restored = await profiles.GetProfileByIdAsync(sourceId);
            Assert.NotNull(restored);
            Assert.Equal(4, restored.Mods.Count);
            Assert.Equal(3, restored.Mods.Count(mod => mod.Enabled));
            Assert.False(restored.Mods.Single(mod => mod.ModId == importedMods[^1].Id).Enabled);
            var records = await repository.GetCurrentDeploymentsAsync(gamePath);
            Assert.Equal(3, records.Count);
            Assert.All(records, record => Assert.Equal(sourceId, record.ProfileId));
            Assert.Contains("源配置内容未被修改", vm.StatusMessage);
            Assert.DoesNotContain("ModBinarySearch.", vm.StatusMessage);
            var disableButton = more.FindControl<Button>("DisableVerifiedModButton")!;
            Assert.True(disableButton.IsEffectivelyVisible);
            Click(disableButton);
            await vm.DisableSuspectCommand.ExecutionTask!;
            Layout();
            var afterDisable = await profiles.GetProfileByIdAsync(sourceId);
            Assert.NotNull(afterDisable);
            Assert.False(afterDisable.Mods.Single(mod => mod.ModId == suspectId).Enabled);
            Assert.Equal(2, afterDisable.Mods.Count(mod => mod.Enabled));
            Assert.Equal(3, (await repository.GetCurrentDeploymentsAsync(gamePath)).Count);
            Assert.Contains("已在源配置中禁用", vm.StatusMessage);
            Assert.False(disableButton.IsEffectivelyVisible);
            var localizer = scope.Services.GetRequiredService<HD2Mimnt.UI.Localization.UiLocalization>();
            foreach (var code in new[] { "en-US", "zh-TW", "zh-CN" })
            {
                await language.SetLanguageAsync(code);
                Layout();
                Assert.DoesNotContain("ModBinarySearch.", vm.StatusMessage);
                Assert.Contains(more.GetVisualDescendants().OfType<TextBlock>(),
                    block => block.Text == localizer["ModBinarySearch.Title"]);
                if (code == "en-US")
                    Assert.NotEqual("模组二分排查", localizer.Translate("模组二分排查"));
            }
            window.Width = 960;
            window.Height = 600;
            Layout();
            Assert.True(more.FindControl<TextBlock>("ModBinarySearchStatus")!.IsEffectivelyVisible);
            Assert.True(more.FindControl<Button>("StartModBinarySearchButton")!.Bounds.Width > 60);
        }
        finally
        {
            if (vm.HasSession) await vm.PrepareShutdownAsync();
            window.DataContext = null;
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
