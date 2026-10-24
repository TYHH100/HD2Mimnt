using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModSourceMetadata_ShouldShowInCardsMenusAndDetailsAndOpenSourceAndAuthor()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: true);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        var target = library.FilteredMods[0];
        var link = new ModPlatformLink("nexusmods", "helldivers2", "16633", "https://www.nexusmods.com/helldivers2/mods/16633",
            InstalledVersion: "0.2.3", Author: "Author with a long name", AuthorUrl: "https://afdian.com/a/example");
        var saved = await scope.Services.GetRequiredService<ModService>().SetPlatformLinkAsync(target.Id, link);
        target.ApplyPlatformLink(saved);
        var browser = new MetadataBrowser();
        var item = new ModItemViewModel(saved, browser);
        main.SelectedTabIndex = 1;
        var window = new MainWindow { DataContext = main, Width = 960, Height = 740 };
        var application = Avalonia.Application.Current!;
        var originalTheme = application.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout()
            { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Click(Control control)
            {
                control.BringIntoView(); Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
            var card = view.GetVisualDescendants().OfType<Border>()
                .First(border => border.Classes.Contains("mod-card") && border.DataContext is ModItemViewModel mod && mod.Id == target.Id);
            var cardGrid = Assert.IsType<Grid>(card.Child);
            Assert.Equal(2, cardGrid.RowDefinitions.Count);
            var cardSource = card.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Command == target.OpenSourcePageCommand);
            var version = card.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "v0.2.3");
            var versionChip = Assert.IsType<Border>(version.Parent);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var width in new[] { 960d, 1280d })
            {
                application.RequestedThemeVariant = theme;
                window.Width = width;
                Layout();
                var sourcePosition = cardSource.TranslatePoint(default, card)!.Value;
                var versionPosition = versionChip.TranslatePoint(default, card)!.Value;
                Assert.True(cardSource.IsEffectivelyVisible && cardSource.Bounds.Width > 0);
                Assert.True(sourcePosition.X + cardSource.Bounds.Width < versionPosition.X);
                Assert.Equal(versionPosition.Y, sourcePosition.Y, 1);
                Assert.Equal(versionChip.Bounds.Height, cardSource.Bounds.Height, 1);
                Assert.Equal(versionChip.Padding, cardSource.Padding);
                Assert.Equal(versionChip.CornerRadius, cardSource.CornerRadius);
            }
            window.Width = 960;
            Layout();
            var title = card.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == target.Name);
            var point = title.TranslatePoint(new Point(12, 8), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Layout();
            var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
            Assert.True(menu.IsOpen);
            Assert.Contains(menu.Items.OfType<FAMenuFlyoutItem>(), entry => entry.Text == "打开来源网页");
            Assert.Contains(menu.Items.OfType<FAMenuFlyoutItem>(), entry => entry.Text == "打开作者主页");
            Assert.Contains(menu.Items.OfType<FAMenuFlyoutItem>(), entry => entry.Text == "检查和更新模组…");
            menu.Hide();
            library.ShowModDetailCommand.Execute(item); Layout();
            Assert.Equal("0.2.3", item.Version); Assert.Equal(link.Author, item.Author);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                application.RequestedThemeVariant = theme;
                var localization = scope.Services.GetRequiredService<ILocalizationService>();
                await localization.SetLanguageAsync(language); Layout();
                var panel = view.GetVisualDescendants().OfType<ModDetailPanel>().Single();
                var sourceButton = panel.FindControl<Button>("OpenModSourcePageButton")!;
                var authorButton = panel.FindControl<Button>("OpenModAuthorPageButton")!;
                Assert.Equal(localization["Metadata.OpenSource"], sourceButton.Content);
                Assert.Equal(localization["Metadata.OpenAuthor"], authorButton.Content);
                Assert.True(sourceButton.IsEffectivelyEnabled && authorButton.IsEffectivelyEnabled);
                Click(sourceButton); Assert.Equal(link.Url, browser.Urls.Last());
                Click(authorButton); Assert.Equal(link.AuthorUrl, browser.Urls.Last());
            }
            library.CloseDetailPanelCommand.Execute(null);
            await scope.Services.GetRequiredService<ILocalizationService>().SetLanguageAsync("zh-CN");
            library.OpenModLinkCommand.Execute(item); Layout();
            view.FindControl<TextBox>("ModAuthorUrlInput")!.Text = "https://afdian.com/a/new-author";
            Click(view.FindControl<Button>("SaveModAuthorPageButton")!);
            await library.ModLink!.SaveAuthorPageCommand.ExecutionTask!;
            Assert.False(library.ModLink.IsOpen);
            Assert.Equal("https://afdian.com/a/new-author", item.AuthorPageUrl);
            Assert.True(item.CanCheckUpdates);
            var other = library.FilteredMods.First(mod => mod.Id != item.Id);
            var authorOnly = await scope.Services.GetRequiredService<ModLinkService>()
                .SaveAuthorPageAsync(other.Id, "https://afdian.com/a/independent-author");
            var authorItem = new ModItemViewModel(authorOnly, browser);
            Assert.False(authorItem.CanCheckUpdates);
            library.ShowModDetailCommand.Execute(authorItem); Layout();
            var authorPanel = view.GetVisualDescendants().OfType<ModDetailPanel>().Single();
            Assert.False(authorPanel.FindControl<Button>("CheckModUpdatesButton")!.IsVisible);
            Click(authorPanel.FindControl<Button>("OpenModAuthorPageButton")!);
            Assert.Equal("https://afdian.com/a/independent-author", browser.Urls.Last());
            library.CloseDetailPanelCommand.Execute(null);
            library.OpenModLinkCommand.Execute(authorItem); Layout();
            Assert.False(library.ModLink.SaveCommand.CanExecute(null));
            Assert.Contains("作者主页", library.ModLink.PreviewText);
        }
        finally { window.Close(); application.RequestedThemeVariant = originalTheme; }
    }

    private sealed class MetadataBrowser : IBrowserLauncher
    {
        public List<string> Urls { get; } = new();
        public void Open(string url) => Urls.Add(url);
    }
}
