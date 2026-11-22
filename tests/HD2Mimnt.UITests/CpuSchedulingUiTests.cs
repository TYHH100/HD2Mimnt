using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task CpuSchedulingSettings_ShouldExposeControlsAndStatus()
    {
        await using var scope = await UiScope.CreateAsync();
        var viewModel = scope.Services.GetRequiredService<SettingsViewModel>();
        await viewModel.LoadAsync();

        var view = new SettingsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        try
        {
            window.Show();

            Assert.NotNull(view.FindControl<ToggleSwitch>("CpuSchedulingEnabledToggle"));
            Assert.NotNull(view.FindControl<ComboBox>("CpuSchedulingModeInput"));
            Assert.NotNull(view.FindControl<Button>("CpuSchedulingRefreshButton"));
            Assert.NotNull(view.FindControl<Button>("CpuSchedulingRestoreButton"));
            Assert.False(string.IsNullOrWhiteSpace(viewModel.CpuSchedulingStatusText));
            Assert.InRange(viewModel.CpuSchedulingModeIndex, 0, 2);
        }
        finally
        {
            window.Close();
        }
    }
}