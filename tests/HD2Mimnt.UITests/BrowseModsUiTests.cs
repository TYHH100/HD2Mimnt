using System.Net;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task BrowseTab_ShouldLoadFilterOpenDetailsAndCancelWebsiteDownloadOnLeaving()
    {
        using var handler = new BrowseContractHandler();
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            void Click(Control control)
            {
                Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var navigation = window.FindControl<FANavigationView>("NavigationView")!;
            Click(navigation.MenuItems.OfType<FANavigationViewItem>().Single(item => item.Tag?.ToString() == "5"));
            await main.NavigateToBrowseCommand.ExecutionTask!;
            Layout();
            Assert.Equal("5", Assert.Single(navigation.MenuItems.OfType<FANavigationViewItem>(), item => item.IsSelected).Tag?.ToString());
            var vm = main.BrowseModsViewModel!;
            Assert.Equal(5, main.SelectedTabIndex);
            Assert.Equal(2, vm.Mods.Count);
            Assert.Contains("5", vm.ScopeDescription);
            Assert.Single(handler.Paths);
            Assert.Null(handler.ApiKeys[0]);
            vm.SearchText = "builder";
            Assert.Equal(2, vm.Mods.Count); // 输入不再筛选热门清单，显式搜索才请求服务器。
            vm.SearchText = "";
            Layout();
            var view = window.GetVisualDescendants().OfType<BrowseModsView>().Single();
            var card = vm.Mods[0];
            Click(view.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.CommandParameter, card)));
            await vm.OpenDetailsCommand.ExecutionTask!;
            Assert.False(vm.HasDetails);
            Assert.NotEmpty(vm.Message);
            await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("browse-test-key");
            Click(view.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.CommandParameter, card)));
            await vm.OpenDetailsCommand.ExecutionTask!;
            Assert.Equal("123", vm.Details!.Id);
            Assert.Equal("详细说明\n第二行", vm.Details.Description);
            Assert.Equal("nexus-v1-file:456", vm.SelectedFile!.Id);
            Assert.True(vm.CanDownload);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var size in new[] { new Size(1100, 760), new Size(960, 600) })
            {
                app.RequestedThemeVariant = theme; window.Width = size.Width; window.Height = size.Height;
                Layout();
                foreach (var name in new[] { "BrowseDownloadButton", "BrowseDownloadAndImportButton", "BrowsePreviewCoverButton", "BrowseImagesWebsiteButton" })
                {
                    var button = view.FindControl<Button>(name)!;
                    Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
                    var edge = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
                    Assert.InRange(edge.X, 0, window.Bounds.Width);
                    Assert.InRange(edge.Y, 0, window.Bounds.Height);
                }
                Assert.True(view.FindControl<ListBox>("BrowseModList")!.Bounds.Width > 250);
                Capture(window, theme.ToString(), $"browse-mods-{size.Width}");
            }
            vm.SelectedFeed = vm.Feeds[1];
            Click(view.FindControl<Button>("BrowseRefreshButton")!);
            await vm.RefreshCommand.ExecutionTask!;
            Assert.Contains("latest_added.json", handler.Paths.Last());
            Assert.Single(vm.Mods);
            vm.ModLink = "https://www.nexusmods.com/helldivers2/mods/123?tab=files";
            Click(view.FindControl<Button>("BrowseOpenLinkButton")!);
            await vm.OpenLinkCommand.ExecutionTask!;
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var language in new[] { "zh-TW", "en-US", "zh-CN" })
            {
                await localization.SetLanguageAsync(language);
                UiLocalization.Current.Attach(localization);
                Layout();
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == localization["Browse.Files"]);
                Assert.Equal("HD2Mimnt Test Armor", vm.Details!.Name);
                Assert.Equal("123", vm.Details.Id);
                Assert.Equal("456", vm.SelectedFile!.WebsiteFileId);
            }
            vm.SelectedFeed = vm.Feeds[2];
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Contains("latest_updated.json", handler.Paths.Last());
            await vm.OpenLinkCommand.ExecuteAsync(null);
            var pending = vm.DownloadAsync(Path.Combine(scope.Root, "cancelled.zip"));
            await WaitForBrowseAuthorizationAsync(vm);
            Assert.True(vm.IsWaitingForWebsite);
            Assert.Contains("file_id=456", scope.WebsiteLauncher.Urls.Last());
            main.ReceiveBrowserProtocolLink("nxm://helldivers2/mods/999/files/456?key=secret&expires=4102444800&user_id=1");
            Assert.True(vm.IsWaitingForWebsite);
            main.NavigateToLibraryCommand.Execute(null);
            await pending;
            Assert.False(vm.IsBusy || vm.IsWaitingForWebsite);
            Assert.False(vm.TryReceiveProtocolLink("nxm://helldivers2/mods/123/files/456?key=secret&expires=4102444800&user_id=1"));
            Assert.False(File.Exists(Path.Combine(scope.Root, "cancelled.zip")));
        }
        finally { await main.BrowseModsViewModel!.CloseAsync(); window.DataContext = null; window.Close(); app.RequestedThemeVariant = originalTheme; }
    }

    [AvaloniaFact]
    public async Task BrowseDownload_ShouldAcceptOnlySelectedFileAndSaveArchiveWithoutInstalledMod()
    {
        using var handler = new BrowseContractHandler();
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("browse-test-key");
        var vm = main.BrowseModsViewModel!;
        vm.ModLink = "123";
        await vm.OpenLinkCommand.ExecuteAsync(null);
        var destination = Path.Combine(scope.Root, "download.zip");
        var pending = vm.DownloadAsync(destination);
        await WaitForBrowseAuthorizationAsync(vm);
        Assert.True(vm.IsWaitingForWebsite);
        main.ReceiveBrowserProtocolLink("nxm://helldivers2/mods/123/files/999?key=secret&expires=4102444800&user_id=1");
        Assert.True(vm.IsWaitingForWebsite);
        main.ReceiveBrowserProtocolLink("nxm://helldivers2/mods/123/files/456?key=secret&expires=4102444800&user_id=1");
        await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(5, main.SelectedTabIndex);
        Assert.Contains("已保存", vm.Message);
        Assert.Equal("test-archive", await File.ReadAllTextAsync(destination));
        Assert.Empty(await scope.Services.GetRequiredService<IModRepository>().GetAllAsync());
        Assert.Null(handler.ArchiveApiKey);
        await vm.CloseAsync();
    }

    [AvaloniaFact]
    public async Task BrowseImport_ShouldUseImportOverlayAndCleanArchiveOnSuccessFailureAndCancellation()
    {
        using var handler = new BrowseContractHandler();
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("browse-test-key");
        var vm = main.BrowseModsViewModel!;
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        try
        {
            window.Show();
            main.NavigateToBrowseCommand.Execute(null);
            await main.NavigateToBrowseCommand.ExecutionTask!;
            vm.ModLink = "123";
            await vm.OpenLinkCommand.ExecuteAsync(null);
            using var data = new MemoryStream();
            using (var zip = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("0123456789abcdef.patch_0").Open()))
                writer.Write("downloaded mod patch");
            var archive = data.ToArray();
            var importer = main.LibraryViewModel.Import;
            var view = window.GetVisualDescendants().OfType<BrowseModsView>().Single();
            string? importSource = null;
            string outcome = "";
            importer.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ModImportViewModel.Detail) && importer.Step == "准备添加")
                    importSource = importer.Detail;
                if (outcome == "cancel" && args.PropertyName == nameof(ModImportViewModel.Step) && importer.Step == "解压模组")
                    importer.CancelCommand.Execute(null);
            };
            foreach (var result in new[] { "success", "failure", "cancel" })
            {
                outcome = result;
                handler.Archive = result == "failure" ? Encoding.UTF8.GetBytes("not an archive") : archive;
                importSource = null;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using (var frame = window.CaptureRenderedFrame()) { }
                var button = view.FindControl<Button>("BrowseDownloadAndImportButton")!;
                Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                await WaitForBrowseAuthorizationAsync(vm);
                Assert.True(vm.IsWaitingForWebsite);
                Assert.False(vm.CanDownloadAndImport);
                Assert.True(main.LibraryViewModel.ImportCompletion.IsCompleted);
                main.ReceiveBrowserProtocolLink("nxm://helldivers2/mods/123/files/456?key=secret&expires=4102444800&user_id=1");
                await vm.DownloadAndImportCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(importer.IsOpen && importer.HasResult);
                Assert.False(importer.IsImporting || vm.IsBusy);
                Assert.NotNull(importSource);
                Assert.False(Directory.Exists(Path.GetDirectoryName(importSource)));
                var installed = Assert.Single(await scope.Services.GetRequiredService<IModRepository>().GetAllAsync());
                Assert.Equal("downloaded mod patch", await File.ReadAllTextAsync(Path.Combine(installed.Path, "0123456789abcdef.patch_0")));
                if (result == "success")
                {
                    Assert.Equal(1, importer.SuccessCount);
                    Assert.Contains("已下载并导入", vm.Message);
                    Assert.Single(main.LibraryViewModel.FilteredMods);
                }
                else if (result == "failure")
                {
                    Assert.Equal(1, importer.FailedCount);
                    Assert.DoesNotContain("已下载并导入", vm.Message);
                }
                else
                {
                    Assert.Equal(1, importer.RemainingCount);
                    Assert.Contains("已取消", vm.Message);
                }
                Assert.Null(handler.ArchiveApiKey);
                importer.CloseCommand.Execute(null);
            }
            var pending = vm.DownloadAndImportCommand.ExecuteAsync(null);
            await WaitForBrowseAuthorizationAsync(vm);
            Assert.True(vm.IsWaitingForWebsite);
            await vm.StopAsync();
            await pending;
            Assert.False(vm.IsWaitingForWebsite);
            Assert.False(vm.TryReceiveProtocolLink("nxm://helldivers2/mods/123/files/456?key=secret&expires=4102444800&user_id=1"));
        }
        finally { await vm.CloseAsync(); window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task BrowseCover_ShouldPreviewSourceResolutionAndHandleFailureCancellationAndLeaving()
    {
        using var png = new MemoryStream();
        using (var bitmap = new RenderTargetBitmap(new PixelSize(900, 600))) bitmap.Save(png);
        using var handler = new BrowseContractHandler { Cover = png.ToArray() };
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("browse-test-key");
        var vm = main.BrowseModsViewModel!;
        var preview = scope.Services.GetRequiredService<ImagePreviewService>();
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        try
        {
            window.Show();
            await main.NavigateToBrowseCommand.ExecuteAsync(null);
            // 等待缩略图，确认点击后不拿 240px 缩略图充当源图。
            for (var attempt = 0; attempt < 100 && vm.Mods[0].Image == null; attempt++) await Task.Delay(10);
            Assert.Equal(240, vm.Mods[0].Image!.PixelSize.Width);
            var view = window.GetVisualDescendants().OfType<BrowseModsView>().Single();
            void Click(Control control)
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using (var frame = window.CaptureRenderedFrame()) { }
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                var hit = window.InputHitTest(point);
                Assert.True(ReferenceEquals(hit, control) || (hit as Visual)?.GetVisualAncestors().Contains(control) == true,
                    $"Click {control.Name} at {point} hit {hit}; visible={control.IsEffectivelyVisible}, bounds={control.Bounds}");
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            }
            var cover = view.GetVisualDescendants().OfType<Border>().Single(border =>
                border.Width == 88 && border.Height == 68 && ReferenceEquals(border.DataContext, vm.Mods[0]));
            Click(cover);
            await vm.PreviewCoverCommand.ExecutionTask!;
            Assert.True(preview.IsOpen);
            Assert.Equal(new PixelSize(900, 600), preview.Image!.PixelSize);
            Assert.NotSame(vm.Mods[0].Image, preview.Image);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (var size in new[] { new Size(1100, 760), new Size(960, 600) })
            {
                app.RequestedThemeVariant = theme; window.Width = size.Width; window.Height = size.Height;
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.True(window.GetVisualDescendants().OfType<HD2Mimnt.UI.Controls.ImagePreviewOverlay>().Single().IsEffectivelyVisible);
                Capture(window, theme.ToString(), "browse-cover-preview");
            }
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(preview.IsOpen);
            Assert.Null(preview.Image);
            await vm.OpenDetailsCommand.ExecuteAsync(vm.Mods[0]);
            Click(view.FindControl<Button>("BrowsePreviewCoverButton")!);
            await vm.PreviewCoverCommand.ExecutionTask!;
            Assert.True(preview.IsOpen);
            main.CloseOverlaysCommand.Execute(null);
            Click(view.FindControl<Button>("BrowseImagesWebsiteButton")!);
            Assert.Equal("https://www.nexusmods.com/helldivers2/mods/123?tab=images", scope.WebsiteLauncher.Urls.Last());
            handler.Cover = null;
            await vm.PreviewCoverCommand.ExecuteAsync(vm.Details);
            Assert.False(preview.IsOpen);
            Assert.NotEmpty(vm.Message);
            handler.DelayCover = true;
            var loading = vm.PreviewCoverCommand.ExecuteAsync(vm.Details);
            Assert.True(vm.IsLoadingImage);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await loading;
            Assert.True(handler.CoverCancelled);
            Assert.False(vm.IsLoadingImage);
            handler.DelayCover = false;
            handler.Cover = png.ToArray();
            await vm.PreviewCoverCommand.ExecuteAsync(vm.Details);
            Assert.True(preview.IsOpen);
            main.NavigateToLibraryCommand.Execute(null);
            await vm.StopAsync();
            Assert.False(preview.IsOpen);
        }
        finally { await vm.CloseAsync(); window.DataContext = null; window.Close(); app.RequestedThemeVariant = originalTheme; }
    }

    [AvaloniaFact]
    public async Task BrowseSearch_ShouldSubmitToServerPageAndSwitchAllLanguages()
    {
        using var handler = new BrowseContractHandler();
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.BrowseModsViewModel!;
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        var app = Avalonia.Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            await main.NavigateToBrowseCommand.ExecuteAsync(null);
            main.NotificationPanelViewModel.DismissAllToasts();
            var view = window.GetVisualDescendants().OfType<BrowseModsView>().Single();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Click(string name)
            {
                Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
                var control = view.FindControl<Button>(name)!;
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                var hit = window.InputHitTest(point);
                Assert.True(ReferenceEquals(hit, control) || (hit as Visual)?.GetVisualAncestors().Contains(control) == true,
                    $"Click {name} at {point} hit {hit}; ancestors={string.Join(",", (hit as Visual)?.GetVisualAncestors().Select(item => item.GetType().Name + ":" + (item as Control)?.Name) ?? [])}; bounds={control.Bounds}, enabled={control.IsEffectivelyEnabled}");
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            }
            var input = view.FindControl<TextBox>("BrowseSearchInput")!;
            input.Text = "Builder"; Layout();
            Capture(window, "Dark", "browse-search-initial");
            Assert.Equal(2, vm.Mods.Count);
            Click("BrowseSearchButton");
            await vm.SearchCommand.ExecutionTask!;
            Assert.Equal("HD2Mimnt Test Server Result", Assert.Single(vm.Mods).Info.Name);
            Assert.True(vm.CanNextPage); Assert.False(vm.CanPreviousPage);
            input.Text = "edited but not submitted";
            Click("BrowseNextPageButton"); await vm.NextPageCommand.ExecutionTask!;
            Assert.Equal("Builder", handler.SearchVariables.Last().GetProperty("filter").GetProperty("filter")[0].GetProperty("author")[0].GetProperty("value").GetString());
            Assert.Equal("HD2Mimnt Test Author Result", Assert.Single(vm.Mods).Info.Name);
            Assert.True(vm.CanPreviousPage); Assert.False(vm.CanNextPage);
            Click("BrowsePreviousPageButton"); await vm.PreviousPageCommand.ExecutionTask!;
            await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("browse-test-key");
            await vm.OpenDetailsCommand.ExecuteAsync(vm.Mods[0]);
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            UiLocalization.Current.Attach(localization);
            foreach (var language in new[] { "en-US", "zh-TW", "zh-CN" })
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                await localization.SetLanguageAsync(language); app.RequestedThemeVariant = theme; Layout();
                Assert.Equal(localization["Browse.SearchPlaceholder"], input.PlaceholderText);
                Assert.Equal(localization["Browse.Search"], view.FindControl<Button>("BrowseSearchButton")!.Content);
                Assert.Equal(localization.Format("Browse.SearchScope", "Builder", 21, 1), view.FindControl<TextBlock>("BrowseScopeText")!.Text);
                foreach (var name in new[] { "BrowseSearchButton", "BrowseRefreshButton", "BrowseNextPageButton", "BrowsePreviousPageButton",
                    "BrowseDownloadButton", "BrowseDownloadAndImportButton", "BrowsePreviewCoverButton", "BrowseImagesWebsiteButton" })
                {
                    var button = view.FindControl<Button>(name)!;
                    var edge = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
                    Assert.InRange(edge.X, 0, window.Bounds.Width); Assert.InRange(edge.Y, 0, window.Bounds.Height);
                }
                Assert.Equal("HD2Mimnt Test Server Result", vm.Mods[0].Info.Name);
                var hint = view.FindControl<TextBlock>("BrowseDownloadHint")!;
                var hintEdge = hint.TranslatePoint(new Point(hint.Bounds.Width, hint.Bounds.Height), view)!.Value;
                Assert.InRange(hintEdge.Y, 0, view.Bounds.Height);
                Capture(window, theme.ToString(), "browse-search-" + language);
            }
            input.Text = "不存在"; Layout(); input.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await vm.SearchCommand.ExecutionTask!; Layout();
            Assert.True(vm.HasNoResults); Assert.False(vm.CanNextPage || vm.CanPreviousPage);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == localization["Browse.NoResults"] && text.IsEffectivelyVisible);
            handler.SearchError = true; input.Text = "Builder";
            Click("BrowseSearchButton"); await vm.SearchCommand.ExecutionTask!;
            foreach (var language in new[] { "en-US", "zh-TW", "zh-CN" })
            {
                await localization.SetLanguageAsync(language); Layout();
                Assert.Equal(localization["Browse.SearchFailed"], view.FindControl<TextBlock>("BrowseStatusText")!.Text);
            }
        }
        finally { await vm.CloseAsync(); window.DataContext = null; window.Close(); app.RequestedThemeVariant = originalTheme; }
    }

    [AvaloniaFact]
    public async Task BrowsePremium_ShouldSaveImportAndCancelWithoutOpeningBrowser()
    {
        using var handler = new BrowseContractHandler { Premium = true };
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("browse-test-key");
        var vm = main.BrowseModsViewModel!;
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        try
        {
            window.Show(); await main.NavigateToBrowseCommand.ExecuteAsync(null);
            vm.ModLink = "123"; await vm.OpenLinkCommand.ExecuteAsync(null);
            var destination = Path.Combine(scope.Root, "premium.zip");
            await vm.DownloadAsync(destination).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("test-archive", await File.ReadAllTextAsync(destination));
            Assert.Empty(scope.WebsiteLauncher.Urls); Assert.False(vm.IsWaitingForWebsite);
            Assert.Empty(await scope.Services.GetRequiredService<IModRepository>().GetAllAsync());
            using var data = new MemoryStream();
            using (var zip = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("0123456789abcdef.patch_0").Open())) writer.Write("premium patch");
            handler.Archive = data.ToArray();
            string? importSource = null;
            var importer = main.LibraryViewModel.Import;
            importer.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ModImportViewModel.Detail) && importer.Step == "准备添加") importSource = importer.Detail;
            };
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using (var frame = window.CaptureRenderedFrame()) { }
            var button = window.GetVisualDescendants().OfType<BrowseModsView>().Single().FindControl<Button>("BrowseDownloadAndImportButton")!;
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await vm.DownloadAndImportCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, importer.SuccessCount);
            Assert.NotNull(importSource); Assert.False(Directory.Exists(Path.GetDirectoryName(importSource)));
            var installed = Assert.Single(await scope.Services.GetRequiredService<IModRepository>().GetAllAsync());
            Assert.Equal("premium patch", await File.ReadAllTextAsync(Path.Combine(installed.Path, "0123456789abcdef.patch_0")));
            Assert.Empty(scope.WebsiteLauncher.Urls); Assert.Null(handler.ArchiveApiKey);
            Assert.All(handler.DownloadQueries, query => Assert.Equal("", query));
            Assert.DoesNotContain(handler.Paths, path => path.StartsWith("/v3/") && !path.EndsWith("trending-mods"));
            importer.CloseCommand.Execute(null);
            handler.DelayArchive = true;
            var archiveCount = handler.Paths.Count(path => path == "/archive.zip");
            var pending = vm.DownloadAndImportCommand.ExecuteAsync(null);
            for (var attempt = 0; attempt < 200 && handler.Paths.Count(path => path == "/archive.zip") == archiveCount; attempt++) await Task.Delay(10);
            Assert.True(handler.Paths.Count(path => path == "/archive.zip") > archiveCount);
            await vm.StopAsync(); await pending;
            Assert.True(handler.ArchiveCancelled); Assert.Contains("已取消", vm.Message);
            Assert.False(vm.IsBusy || vm.IsWaitingForWebsite);
            Assert.Single(await scope.Services.GetRequiredService<IModRepository>().GetAllAsync());
            Assert.Empty(Directory.GetDirectories(scope.Root, "hd2mimnt_browse_*", SearchOption.AllDirectories));
        }
        finally { await vm.CloseAsync(); window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task BrowseMembership_ShouldKeepSupporterOnWebsiteRejectAuthenticationAndLocalizePremiumErrors()
    {
        using var handler = new BrowseContractHandler { Supporter = true };
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("browse-test-key");
        var vm = main.BrowseModsViewModel!;
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        try
        {
            window.Show(); await main.NavigateToBrowseCommand.ExecuteAsync(null);
            vm.ModLink = "123"; await vm.OpenLinkCommand.ExecuteAsync(null);
            var pending = vm.DownloadAsync(Path.Combine(scope.Root, "supporter.zip"));
            await WaitForBrowseAuthorizationAsync(vm);
            Assert.Single(scope.WebsiteLauncher.Urls); Assert.Empty(handler.DownloadQueries);
            await vm.StopAsync(); await pending;
            handler.MembershipStatus = HttpStatusCode.Unauthorized;
            await vm.DownloadAsync(Path.Combine(scope.Root, "rejected.zip"));
            Assert.Single(scope.WebsiteLauncher.Urls); Assert.Empty(handler.DownloadQueries);
            Assert.Contains("HTTP 401", vm.Message); Assert.False(File.Exists(Path.Combine(scope.Root, "rejected.zip")));
            handler.MembershipStatus = HttpStatusCode.OK; handler.Premium = true;
            handler.DownloadLinkStatus = HttpStatusCode.Forbidden;
            await vm.DownloadAsync(Path.Combine(scope.Root, "forbidden.zip"));
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            UiLocalization.Current.Attach(localization);
            var view = window.GetVisualDescendants().OfType<BrowseModsView>().Single();
            foreach (var language in new[] { "en-US", "zh-TW", "zh-CN" })
            {
                await localization.SetLanguageAsync(language); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var status = view.FindControl<TextBlock>("BrowseStatusText")!.Text!;
                Assert.Contains(localization.Format("Browse.HttpError", localization["Browse.PremiumLinkOperation"], 403), status);
                Assert.Contains(localization["Browse.PremiumRejected"], status);
                Assert.Contains("Server diagnostic", status);
                Assert.Equal(localization["Browse.DownloadAndImport"], view.FindControl<Button>("BrowseDownloadAndImportButton")!.Content);
            }
            Assert.Single(scope.WebsiteLauncher.Urls); Assert.False(handler.ArchiveStarted);
        }
        finally { await vm.CloseAsync(); window.DataContext = null; window.Close(); }
    }

    private static async Task WaitForBrowseAuthorizationAsync(BrowseModsViewModel vm)
    {
        for (var attempt = 0; attempt < 200 && vm.IsBusy && !vm.IsWaitingForWebsite; attempt++) await Task.Delay(10);
        Assert.True(vm.IsWaitingForWebsite, vm.Message);
    }

    private sealed class BrowseContractHandler : HttpMessageHandler
    {
        public bool SearchError { get; set; }
        public bool IncludeQuota { get; set; }
        public bool DelayQuota { get; set; }
        public bool QuotaCancelled { get; private set; }
        public bool ArchiveStarted { get; private set; }
        public bool Premium { get; set; }
        public bool Supporter { get; set; }
        public HttpStatusCode MembershipStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode DownloadLinkStatus { get; set; } = HttpStatusCode.OK;
        public bool DelayArchive { get; set; }
        public bool ArchiveCancelled { get; private set; }
        public List<JsonElement> SearchVariables { get; } = new();
        public List<string> DownloadQueries { get; } = new();
        public byte[] Archive { get; set; } = Encoding.UTF8.GetBytes("test-archive");
        public byte[]? Cover { get; set; }
        public bool DelayCover { get; set; }
        public bool CoverCancelled { get; private set; }
        public List<string> Paths { get; } = new();
        public List<string?> ApiKeys { get; } = new();
        public string? ArchiveApiKey { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var key = request.Headers.TryGetValues("apikey", out var values) ? values.Single() : null;
            if (request.RequestUri.Host == "browse-images.test")
            {
                Assert.Null(key);
                if (DelayCover)
                {
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { CoverCancelled = true; throw; }
                }
                return new HttpResponseMessage(Cover == null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                    { Content = new ByteArrayContent(Cover ?? Array.Empty<byte>()) };
            }
            Paths.Add(path);
            ApiKeys.Add(key);
            if (path == "/v2/graphql")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var variables = body.RootElement.GetProperty("variables");
                SearchVariables.Add(variables.Clone());
                if (SearchError) return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"errors":[{"message":"Search unavailable"}]}""", Encoding.UTF8, "application/json") };
                var value = variables.GetProperty("filter").GetProperty("filter")[0].GetProperty("name")[0].GetProperty("value").GetString();
                var empty = value == "不存在";
                var next = variables.GetProperty("offset").GetInt32() > 0;
                var nodes = empty ? "[]" : next ? """[{"modId":789,"gameId":6119,"name":"HD2Mimnt Test Author Result","author":"Builder","summary":"Found by author"}]"""
                    : """[{"modId":123,"gameId":6119,"name":"HD2Mimnt Test Server Result","author":"Builder","summary":"Found by name"}]""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent($"{{\"data\":{{\"mods\":{{\"totalCount\":{(empty ? 0 : 21)},\"nodes\":{nodes}}}}}}}", Encoding.UTF8, "application/json") };
            }
            if (path == "/v1/users/validate.json")
            {
                if (DelayQuota)
                {
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { QuotaCancelled = true; throw; }
                }
                var response = new HttpResponseMessage(MembershipStatus)
                { Content = new StringContent(JsonSerializer.Serialize(new { user_id = 1, is_premium = Premium, is_supporter = Supporter }), Encoding.UTF8, "application/json") };
                if (IncludeQuota)
                {
                    response.Headers.Add("X-RL-Hourly-Limit", "100"); response.Headers.Add("X-RL-Hourly-Remaining", MembershipStatus == HttpStatusCode.TooManyRequests ? "0" : "96");
                    response.Headers.Add("X-RL-Daily-Limit", "2500"); response.Headers.Add("X-RL-Daily-Remaining", MembershipStatus == HttpStatusCode.TooManyRequests ? "0" : "2488");
                    if (MembershipStatus == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
                    response.Headers.Add("X-RL-Hourly-Reset", "2026-10-14T12:00:00+00:00");
                    response.Headers.Add("X-RL-Daily-Reset", "2026-10-15 00:00:00 +0000");
                }
                return response;
            }
            if (path.EndsWith("download_link.json"))
            {
                DownloadQueries.Add(request.RequestUri.Query);
                if (DownloadLinkStatus != HttpStatusCode.OK)
                    return new HttpResponseMessage(DownloadLinkStatus)
                    { Content = new StringContent("""{"message":"Server diagnostic"}""", Encoding.UTF8, "application/json") };
            }
            if (request.RequestUri.Host == "browse-download.test")
            {
                ArchiveApiKey = key;
                ArchiveStarted = true;
                if (DelayArchive)
                {
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { ArchiveCancelled = true; throw; }
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) };
            }
            var json = path switch
            {
                "/v3/games/helldivers2/trending-mods" => """{"data":{"mods":[{"name":"HD2Mimnt Test Armor","author":"Builder","summary":"盔甲","picture_url":"https://browse-images.test/cover.png","mod_page_url":"https://www.nexusmods.com/helldivers2/mods/123"},{"name":"HD2Mimnt Test Weapon","author":"Other","mod_page_url":"https://www.nexusmods.com/helldivers2/mods/789"}]}}""",
                "/v1/games/helldivers2/mods/latest_added.json" or "/v1/games/helldivers2/mods/latest_updated.json" => """[{"mod_id":123,"name":"HD2Mimnt Test Armor","author":"Builder","summary":"盔甲","version":"1.0"}]""",
                "/v1/games/helldivers2/mods/123.json" => """{"mod_id":123,"name":"HD2Mimnt Test Armor","author":"Builder","description":"[b]详细说明[/b]<br>第二行","picture_url":"https://browse-images.test/cover.png","version":"1.0"}""",
                "/v1/games/helldivers2/mods/123/files.json" => """{"files":[{"file_id":456,"category_id":1,"name":"Main","file_name":"armor.zip","version":"1.0"}]}""",
                "/v1/games/helldivers2/mods/123/files/456.json" => """{"file_id":456,"category_id":1,"name":"Main","file_name":"armor.zip","version":"1.0"}""",
                "/v1/games/helldivers2/mods/123/files/456/download_link.json" => """[{"URI":"https://browse-download.test/archive.zip"}]""",
                _ => throw new InvalidOperationException("Unexpected browse endpoint: " + path)
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
