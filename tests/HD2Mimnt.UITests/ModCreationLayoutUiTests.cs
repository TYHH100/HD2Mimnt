using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModCreationLayout_ShouldKeepFormsInsideViewportAndPackagingScrollable()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        main.NavigateToModCreationCommand.Execute(null);
        var vm = main.ModCreationViewModel!;
        vm.ModName = new string('测', 160);
        vm.Author = new string('A', 160);
        vm.IconPath = "images/" + new string('a', 160) + ".png";
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        var window = new MainWindow { DataContext = main, Width = 1196, Height = 754 };
        try
        {
            window.Show();
            var view = window.GetVisualDescendants().OfType<ModCreationView>().Single();
            var tabs = view.FindControl<TabControl>("CreationTabs")!;
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var language in new[] { "zh-CN", "en-US" })
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var size in new[] { (Width: 1196d, Height: 754d), (Width: 960d, Height: 600d) })
            {
                await localization.SetLanguageAsync(language);
                app.RequestedThemeVariant = theme;
                window.Width = size.Width; window.Height = size.Height;
                tabs.SelectedIndex = 0;
                Layout();
                main.NotificationPanelViewModel.DismissAllToasts(); Layout();
                Assert.True(tabs.Bounds.Height >= size.Height * 0.45, $"Editor height: {tabs.Bounds.Height}");
                var basic = view.FindControl<ScrollViewer>("CreationBasicScroll")!;
                foreach (var name in new[] { "CreationBasicCard", "CreationSourceInput", "CreationSourceBrowseButton",
                    "CreationNameInput", "CreationCoverInput", "CreationCoverPreviewButton" })
                {
                    var control = view.FindControl<Control>(name)!;
                    var position = control.TranslatePoint(default, basic)!.Value;
                    Assert.True(position.X >= -1 && position.X + control.Bounds.Width <= basic.Bounds.Width + 1,
                        $"{language} {size.Width}: {name} x={position.X}, width={control.Bounds.Width}, viewport={basic.Bounds.Width}");
                }
                Assert.True(view.FindControl<TextBox>("CreationCoverInput")!.Bounds.Width > 120);
                Assert.True(basic.Extent.Width <= basic.Viewport.Width + 1);
                Capture(window, theme.ToString(), $"creation-layout-basic-{language}-{size.Width}");

                tabs.SelectedItem = view.FindControl<TabItem>("CreationPackagingTab");
                vm.Passwords.UsePassword = true;
                Layout();
                var packaging = view.FindControl<ScrollViewer>("CreationPackagingScroll")!;
                var library = packaging.GetVisualDescendants().OfType<Expander>().Single();
                library.IsExpanded = true;
                Layout();
                var password = view.FindControl<TextBox>("ExportArchivePassword")!;
                Assert.True(password.IsEffectivelyVisible);
                Assert.True(packaging.Extent.Width <= packaging.Viewport.Width + 1);
                Assert.True(tabs.Bounds.Height >= size.Height * 0.45);
                Assert.True(view.FindControl<Button>("ExportModArchiveButton")!.IsEffectivelyVisible);
                packaging.Offset = new Vector(0, packaging.Extent.Height);
                Layout();
                Assert.True(packaging.Offset.Y > 0);
                packaging.Offset = default;
                library.IsExpanded = false;
                Layout();
                Capture(window, theme.ToString(), $"creation-layout-packaging-{language}-{size.Width}");
            }
        }
        finally { window.Close(); app.RequestedThemeVariant = originalTheme; }

        void Layout()
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
        }
    }
}
