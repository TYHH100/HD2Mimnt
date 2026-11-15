using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using static HD2Mimnt.Tests.Services.GpuUsageFixture;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task GpuUsage_ShouldShowBothPagesAndRefreshEnabledOrderAndLanguage()
    {
        await using var scope = await UiScope.CreateAsync();
        var settings = scope.Services.GetRequiredService<SettingsService>();
        await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.TopToBottom.ToString());
        await settings.SetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, false);
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var profileId = await profiles.CreateProfileAsync("GPU test");
        await profiles.SetActiveProfileAsync(profileId);
        foreach (var index in new[] { 1, 2 })
        {
            var mod = new Mod { Id = Guid.NewGuid(), Name = $"HD2Mimnt Test GPU {index}", Version = "1.0",
                Path = Path.Combine(scope.Root, "gpu-mods", index.ToString()), BslRole = BslModRole.None,
                BslDetectionVersion = BslModDetector.DetectionVersion };
            WritePatch(Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_0"), new Resource(1, Unit, (uint)index * 16 * 1048576));
            await repository.AddAsync(mod);
            await profiles.AddModToProfileAsync(profileId, mod.Id);
        }
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var library = main.LibraryViewModel;
        var profile = main.ProfileViewModel;
        await main.WaitForRefreshesAsync();
        await library.WaitForGpuUsageAsync();
        await profile.WaitForGpuUsageAsync();
        Assert.Equal(32 * 1048576, profile.ProfileGpuUsage!.Usage!.Bytes.TotalBytes);
        Assert.All(profile.ProfileMods, card => Assert.True(card.GpuUsage!.Usage!.IsComplete));
        var view = new ProfileView { DataContext = profile };
        var window = new Window { Width = 1100, Height = 800, Content = view };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var totalLabel = view.FindControl<TextBlock>("ProfileGpuUsageLabel")!;
            var language = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await language.SetLanguageAsync(code);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal(profile.ProfileGpuUsage.Label.Localize(), totalLabel.Text);
                Assert.Contains("32", totalLabel.Text);
                var modLabels = view.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == "ProfileModGpuUsageLabel").ToArray();
                Assert.Equal(2, modLabels.Length);
                Assert.All(modLabels, text =>
                {
                    Assert.Contains("MiB", text.Text);
                    Assert.True(text.Margin.Right >= 6);
                });
            }
            Assert.Contains("GPU resources", totalLabel.Text);
            Capture(window, "Dark", "gpu-profile");
            // 切换部署方向后，较早的模组成为覆盖者；保留原有卡片。
            var cards = profile.ProfileMods.ToArray();
            await settings.SetSettingAsync(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.BottomToTop.ToString());
            await profile.WaitForGpuUsageAsync();
            Assert.Equal(16 * 1048576, profile.ProfileGpuUsage!.Usage!.Bytes.TotalBytes);
            var first = cards.Single(card => card.ModName.EndsWith("1"));
            await profile.ToggleModEnabledCommand.ExecuteAsync(first);
            await main.WaitForRefreshesAsync();
            await profile.WaitForGpuUsageAsync();
            Assert.Equal(32 * 1048576, profile.ProfileGpuUsage!.Usage!.Bytes.TotalBytes);
            Assert.False(first.GpuUsage!.Enabled);
            Assert.All(cards, card => Assert.Same(card, profile.ProfileMods.Single(item => item.ModId == card.ModId)));

            var libraryView = new LibraryView { DataContext = library };
            window.Content = libraryView;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var labels = libraryView.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == "LibraryGpuUsageLabel").ToArray();
            Assert.Equal(2, labels.Length);
            Assert.All(labels, label => Assert.Contains("MiB", label.Text));
            Capture(window, "Dark", "gpu-library");

            var emptyId = await profiles.CreateProfileAsync("GPU empty");
            await main.WaitForRefreshesAsync();
            profile.SelectedProfile = profile.Profiles.Single(item => item.Id == emptyId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (profile.IsProfileModsLoading) await Task.Delay(10, timeout.Token);
            await main.WaitForRefreshesAsync();
            await profile.WaitForGpuUsageAsync();
            Assert.Equal(emptyId, profile.SelectedProfile.Id);
            Assert.Equal(0, profile.ProfileGpuUsage!.Usage!.Bytes.TotalBytes);
            Assert.Empty(profile.ProfileMods);
        }
        finally
        {
            await main.ConflictsViewModel.StopScanAsync();
            await main.DisposeAsync();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task GpuUsage_PartialAndDisabledTooltips_ShouldRetainMeaningAcrossLanguages()
    {
        await using var scope = await UiScope.CreateAsync();
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        UiLocalization.Current.Attach(language);
        var usage = new ModGpuUsage(Guid.NewGuid(), new(100, 200, 0), 2, ["external resource issue"]);
        var display = new GpuUsageDisplay(usage, GpuUsageScope.ProfileMod, Enabled: false, EffectiveComplete: false);
        foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
        {
            await language.SetLanguageAsync(code);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("MiB", display.Label.Localize());
            Assert.Contains("external resource issue", display.Tooltip.Localize());
            Assert.Contains(UiLocalization.Current["GpuUsage.Disabled"], display.Tooltip.Localize());
        }
    }
}
