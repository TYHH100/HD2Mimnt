using System.IO.Compression;
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
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Services;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModCreation_ShouldNavigateEditExportReimportAndKeepTagsAcrossLanguagesAndThemes()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>(); await main.InitializeAsync();
        var vm = main.ModCreationViewModel!;
        var app = Avalonia.Application.Current!; var theme = app.RequestedThemeVariant;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Click(Control control)
            {
                control.BringIntoView();
                Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                Assert.Contains(control, (window.InputHitTest(point) as Visual)!.GetSelfAndVisualAncestors());
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var navigation = window.FindControl<FANavigationView>("NavigationView")!;
            var entry = navigation.MenuItems.OfType<FANavigationViewItem>().Single(item => item.Tag?.ToString() == "6");
            Click(entry); Assert.Equal(6, main.SelectedTabIndex); Assert.Same(vm, main.CurrentView);
            var view = window.GetVisualDescendants().OfType<ModCreationView>().Single();
            var source = Path.Combine(scope.Root, "creation-source"); Directory.CreateDirectory(Path.Combine(source, "red"));
            var testData = scope.Services.GetRequiredService<TestDataService>();
            await testData.ImportGeneratedModsAsync([new() { Name = "HD2Mimnt Test Creation Source", Path = source }]);
            var generated = (await testData.GetAllModsAsync()).Single();
            using (var writer = new BinaryWriter(File.Create(Path.Combine(generated.Path, "aaaaaaaaaaaaaaaa.patch_0"))))
            {
                writer.Write(0xF0000011u); writer.Write(1u); writer.Write(1u); writer.Write(new byte[60]);
                writer.Write(new byte[32]); writer.Write(ulong.MaxValue); writer.Write(0xcd4238c6a0c69e32UL);
                writer.Write(184UL); writer.Write(new byte[32]); writer.Write(4u); writer.Write(new byte[20]); writer.Write(new byte[4]);
            }
            // 现有导入源保持独立；创建页读取库副本作为完整包的源目录。
            vm.SourceDirectory = generated.Path;
            await vm.InitializeSourceAsync();
            while (vm.Options.Count > 0) vm.RemoveOptionCommand.Execute(null);
            var name = view.FindControl<TextBox>("CreationNameInput")!;
            name.Focus(); name.SelectAll(); window.KeyTextInput("Created Mod 测试"); Layout(); Assert.Equal("Created Mod 测试", vm.ModName);
            vm.Author = "Creator"; vm.ModVersion = "2.0";
            var websiteInput = view.FindControl<TextBox>("CreationAuthorUrlInput")!;
            websiteInput.BringIntoView(); Layout(); websiteInput.Focus();
            window.KeyTextInput("https://afdian.com/a/example"); Layout();
            Assert.Equal("https://afdian.com/a/example", vm.AuthorUrl);
            Assert.DoesNotContain("AuthorUrl", vm.JsonPreview);
            Assert.Contains("AuthorUrl", vm.MetadataPreview);
            var tabs = view.FindControl<TabControl>("CreationTabs")!;
            Click(tabs.GetVisualDescendants().OfType<TabItem>().ElementAt(1));
            Click(view.FindControl<Button>("AddCreationOptionButton")!);
            var option = Assert.Single(vm.Options);
            var optionName = view.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "CreationOptionNameInput");
            optionName.Focus(); window.KeyTextInput("外观"); Layout(); Assert.Equal("外观", option.Name);
            option.HasIncludeMapping = true; option.IncludePaths = ".";
            Click(view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "AddCreationSubOptionButton"));
            var sub = Assert.Single(option.SubOptions); sub.Name = "原始"; sub.IncludePaths = ".";
            Assert.Contains("SubOptions", vm.JsonPreview);
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                await localization.SetLanguageAsync(language);
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    app.RequestedThemeVariant = variant; window.Width = 960; window.Height = 600; Layout();
                    main.NotificationPanelViewModel.DismissAllToasts(); Layout();
                    Assert.True(view.FindControl<Button>("ExportModArchiveButton")!.IsEffectivelyVisible);
                    Assert.True(optionName.Bounds.Width > 100);
                    Capture(window, variant.ToString(), $"mod-creation-options-{language}");
                }
            }
            Click(tabs.GetVisualDescendants().OfType<TabItem>().ElementAt(0)); Layout();
            Capture(window, "Dark", "mod-creation-basic");
            var zip = Path.Combine(scope.Root, "created.zip"); await vm.ExportAsync(zip); Layout();
            Assert.False(vm.IsBusy); Assert.False(vm.IsDirty); Assert.True(File.Exists(zip), vm.StatusMessage);
            using (var archive = ZipFile.OpenRead(zip))
            {
                using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
                var manifest = new ManifestParser().ParseFromJson(await reader.ReadToEndAsync())!;
                Assert.Equal("Created Mod 测试", manifest.Name); Assert.Equal("1.0.0", manifest.Version);
                Assert.Equal("Unknown", manifest.Author);
                Assert.NotNull(archive.GetEntry(ManifestPackage.MetadataFileName));
                Assert.Equal("原始", Assert.Single(Assert.Single(manifest.Options!).SubOptions!).Name);
                Assert.NotNull(archive.GetEntry("aaaaaaaaaaaaaaaa.patch_0"));
            }
            await main.LibraryViewModel.ImportPathsAsync([zip]);
            Assert.Equal(1, main.LibraryViewModel.Import.SuccessCount);
            var imported = Assert.Single((await testData.GetAllModsAsync()).Where(mod => mod.Name == "Created Mod 测试"));
            Assert.Equal("2.0", imported.Version); Assert.Equal("Creator", imported.Author);
            Assert.Equal("https://afdian.com/a/example", imported.Manifest!.AuthorUrl);
            Assert.True(Assert.Single(imported.Manifest!.Options!).DefaultEnabled);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(generated.Path, "aaaaaaaaaaaaaaaa.patch_0")),
                await File.ReadAllBytesAsync(Path.Combine(imported.Path, "aaaaaaaaaaaaaaaa.patch_0")));
            main.LibraryViewModel.Import.CloseCommand.Execute(null); Layout();
            main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            Click(navigation.MenuItems.OfType<FANavigationViewItem>().Single(item => item.Tag?.ToString() == "1"));
            Assert.Equal(1, main.SelectedTabIndex);
            var libraryView = window.GetVisualDescendants().OfType<LibraryView>().Single();
            var card = libraryView.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("mod-card")
                && border.DataContext is ModItemViewModel mod && mod.Id == imported.Id);
            var libraryItem = (ModItemViewModel)card.DataContext!;
            var cardTitle = card.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == imported.Name);
            void CheckAuthorMenu(bool visible)
            {
                cardTitle.BringIntoView(); Layout();
                var position = cardTitle.TranslatePoint(new Point(10, 8), window)!.Value;
                window.MouseMove(position); window.MouseDown(position, MouseButton.Right); window.MouseUp(position, MouseButton.Right); Layout();
                var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
                Assert.True(menu.IsOpen);
                Assert.Equal(visible, menu.Items.OfType<FAMenuFlyoutItem>().Any(item => item.Command == libraryItem.OpenAuthorPageCommand));
                menu.Hide(); Layout();
            }
            Assert.Null(libraryItem.PlatformLink); Assert.True(libraryItem.HasAuthorPage);
            CheckAuthorMenu(true);
            var browser = new MetadataBrowser();
            var websiteItem = new ModItemViewModel(imported, browser);
            websiteItem.OpenAuthorPageCommand.Execute(null); Assert.Equal(vm.AuthorUrl, browser.Urls.Last());
            libraryItem.GetEntity().Manifest!.AuthorUrl = null;
            Assert.False(libraryItem.HasAuthorPage); CheckAuthorMenu(false);
            libraryItem.GetEntity().Manifest!.AuthorUrl = vm.AuthorUrl;
            var profiles = scope.Services.GetRequiredService<ProfileService>();
            await profiles.AddModToProfileAsync((await profiles.GetActiveProfileAsync())!.Id, imported.Id);
            await main.ProfileViewModel.RefreshAfterProfilesChangedAsync();
            Click(navigation.MenuItems.OfType<FANavigationViewItem>().Single(item => item.Tag?.ToString() == "0"));
            var profileView = window.GetVisualDescendants().OfType<ProfileView>().Single();
            var profileItem = main.ProfileViewModel.ProfileMods.Single(item => item.ModId == imported.Id);
            profileView.FindControl<ItemsControl>("ModItemsControl")!.ScrollIntoView(profileItem); Layout();
            var profileCard = profileView.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("mod-card")
                && ReferenceEquals(border.DataContext, profileItem));
            var profileTitle = profileCard.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == imported.Name);
            Assert.NotNull(profileItem.SourceInfo); Assert.Null(profileItem.SourceInfo.PlatformLink);
            void CheckProfileAuthorMenu(bool visible)
            {
                profileTitle.BringIntoView(); Layout();
                var position = profileTitle.TranslatePoint(new Point(10, 8), window)!.Value;
                window.MouseMove(position); window.MouseDown(position, MouseButton.Right); window.MouseUp(position, MouseButton.Right); Layout();
                var menu = Assert.IsType<FAMenuFlyout>(profileCard.ContextFlyout);
                Assert.True(menu.IsOpen);
                Assert.Equal(visible, menu.Items.OfType<FAMenuFlyoutItem>().Any(item => item.Command == profileItem.SourceInfo.OpenAuthorPageCommand));
                menu.Hide(); Layout();
            }
            CheckProfileAuthorMenu(true);
            profileItem.SourceInfo.GetEntity().Manifest!.AuthorUrl = null; CheckProfileAuthorMenu(false);
            profileItem.SourceInfo.GetEntity().Manifest!.AuthorUrl = vm.AuthorUrl;
            Click(navigation.MenuItems.OfType<FANavigationViewItem>().Single(item => item.Tag?.ToString() == "1"));
            Assert.True(window.GetVisualDescendants().OfType<LibraryView>().Single().FindControl<TextBox>("SearchBox")!.Focus());
            var shortcut = Assert.Single(window.KeyBindings.Where(binding => ReferenceEquals(binding.Command, main.NavigateToModCreationCommand)));
            Assert.Equal(Key.D7, shortcut.Gesture!.Key);
            foreach (var digit in Enumerable.Range(1, 7)) Assert.Contains(window.KeyBindings,
                binding => binding.Gesture?.Key == Key.D0 + digit && binding.Gesture.KeyModifiers == KeyModifiers.Control);
            window.KeyPress(Key.D7, RawInputModifiers.Control, PhysicalKey.Digit7, null);
            window.KeyRelease(Key.D7, RawInputModifiers.Control, PhysicalKey.Digit7, null); Layout();
            Assert.Equal(6, main.SelectedTabIndex); Assert.Equal("Created Mod 测试", vm.ModName);
            main.SettingsViewModel.SelectedCategory = "入口自定义";
            main.SettingsViewModel.NavigationEntries.Single(option => option.Id == "navigation.mod-creation").IsVisible = false;
            await main.SettingsViewModel.SaveCommand.ExecuteAsync(null); Layout(); Assert.False(entry.IsVisible);
            await main.SettingsViewModel.ResetCommand.ExecuteAsync(null); Layout(); Assert.True(entry.IsVisible);
        }
        finally { await vm.StopAsync(); window.DataContext = null; window.Close(); app.RequestedThemeVariant = theme; }
    }
}
