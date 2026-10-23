using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = HD2Mimnt.Core.Entities.TaskStatus;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task NexusBackgroundDownload_ShouldSurviveNavigationAndReportCompletionAndCancellationInTaskCenter()
    {
        using var handler = new BrowseContractHandler { DelayArchive = true };
        await using var scope = await UiScope.CreateAsync(nexusHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("browse-test-key");
        var vm = main.BrowseModsViewModel!;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        const string link = "nxm://helldivers2/mods/123/files/456?key=background-secret&expires=4102444800&user_id=1";
        var path = Path.Combine(scope.Root, "background.zip");
        try
        {
            window.Show();
            await main.NavigateToBrowseCommand.ExecuteAsync(null);
            vm.ModLink = "123";
            await vm.OpenLinkCommand.ExecuteAsync(null);
            var pending = vm.DownloadAsync(path);
            await WaitForBrowseAuthorizationAsync(vm);
            main.NavigateToLibraryCommand.Execute(null);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(vm.IsBusy && vm.IsWaitingForWebsite);
            await main.TaskCenterViewModel.LoadAsync();
            var task = Assert.Single(main.TaskCenterViewModel.Tasks, task => task.Type == "PlatformDownload");
            Assert.Equal(TaskStatus.Running, task.Status);
            Assert.Contains("N 网下载", task.Name);
            main.ReceiveBrowserProtocolLink(link);
            await WaitForPlatformStateAsync(() => handler.ArchiveStarted);
            Assert.Equal(1, main.SelectedTabIndex);
            await main.TaskCenterViewModel.CancelCommand.ExecuteAsync(task);
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(handler.ArchiveCancelled);
            Assert.False(vm.IsBusy);
            Assert.False(File.Exists(path));
            Assert.Equal(TaskStatus.Cancelled, main.TaskCenterViewModel.Tasks.Single(t => t.Id == task.Id).Status);

            handler.DelayArchive = false;
            await main.NavigateToBrowseCommand.ExecuteAsync(null);
            pending = vm.DownloadAsync(path);
            await WaitForBrowseAuthorizationAsync(vm);
            main.NavigateToProfilesCommand.Execute(null);
            main.ReceiveBrowserProtocolLink(link);
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, main.SelectedTabIndex);
            Assert.Equal("test-archive", await File.ReadAllTextAsync(path));
            await main.TaskCenterViewModel.LoadAsync();
            Assert.Contains(main.TaskCenterViewModel.Tasks, t => t.Type == "PlatformDownload" && t.Status == TaskStatus.Completed);
            Assert.DoesNotContain("background-secret", JsonSerializer.Serialize(main.TaskCenterViewModel.Tasks));
        }
        finally { await vm.CloseAsync(); window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task AyakaBackgroundDownload_ShouldContinueBrowserHandoffAndImportAfterViewDetaches()
    {
        using var handler = new AyakaUiHandler();
        await using var scope = await UiScope.CreateAsync(ayakaHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await scope.Services.GetRequiredService<AyakaModsAuthService>().ApplyApiKeyAsync("synthetic-ui-ayaka-key");
        var vm = main.BrowseModsViewModel!;
        vm.SelectedPlatform = vm.Platforms[1]; await vm.PlatformChange;
        vm.ModLink = "42"; await vm.OpenLinkCommand.ExecuteAsync(null);
        var importing = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ImportDownloadedArchiveAsync = async (path, token) => { importing.SetResult(path); await release.Task.WaitAsync(token); };
        var downloads = scope.Services.GetRequiredService<BrowserDownloadHandoffService>();
        Directory.CreateDirectory(downloads.DownloadsDirectory);
        var source = Path.Combine(downloads.DownloadsDirectory, "armor.zip");
        var window = new Window { Content = new BrowseModsView { DataContext = vm } };
        try
        {
            window.Show();
            var pending = vm.DownloadAndImportCommand.ExecuteAsync(null);
            await WaitForAyakaBrowserAsync(() => vm.IsWaitingForBrowser);
            window.Content = new Border();
            await vm.LeavePageAsync();
            Assert.True(vm.IsBusy && vm.IsWaitingForBrowser);
            await main.TaskCenterViewModel.LoadAsync();
            var task = Assert.Single(main.TaskCenterViewModel.Tasks, t => t.Type == "PlatformDownload");
            Assert.Contains("A 网下载", task.Name);
            Assert.Equal(TaskStatus.Running, task.Status);
            await File.WriteAllTextAsync(source, "test");
            Assert.Equal(source, await importing.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(pending.IsCompleted);
            Assert.Equal(TaskStatus.Running, (await scope.Services.GetRequiredService<TaskService>().GetTaskAsync(task.Id))!.Status);
            release.SetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(source));
            await main.TaskCenterViewModel.LoadAsync();
            Assert.Equal(TaskStatus.Completed, main.TaskCenterViewModel.Tasks.Single(t => t.Id == task.Id).Status);

            pending = vm.DownloadAndImportCommand.ExecuteAsync(null);
            await WaitForAyakaBrowserAsync(() => vm.IsWaitingForBrowser);
            await main.TaskCenterViewModel.LoadAsync();
            task = Assert.Single(main.TaskCenterViewModel.Tasks, t => t.Status == TaskStatus.Running);
            await main.TaskCenterViewModel.CancelCommand.ExecuteAsync(task);
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(vm.IsBusy || vm.IsWaitingForBrowser);
            Assert.True(File.Exists(source));
            using var next = await downloads.StartAsync(vm.SelectedFile!, default);
            Assert.DoesNotContain(handler.Paths, path => path.Contains("/download"));
        }
        finally { release.TrySetResult(); await vm.CloseAsync(); window.Content = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task ConcurrentNxmBackgroundTasks_ShouldRouteCallbackToMatchingHiddenUpdate()
    {
        using var handler = new ModLinkApiHandler();
        await using var scope = await UiScope.CreateAsync(seedMods: true, nexusHandler: handler);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await scope.Services.GetRequiredService<NexusModsTestService>().ApplyApiKeyAsync("ui-mod-link-key");
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        var target = library.FilteredMods[0];
        var link = new ModPlatformLink("nexusmods", "helldivers2", "16633", "https://www.nexusmods.com/helldivers2/mods/16633");
        target.ApplyPlatformLink(await scope.Services.GetRequiredService<ModLinkService>().SaveAsync(target.Id, link));
        var browse = main.BrowseModsViewModel!;
        var online = library.ModOnline!;
        try
        {
            browse.ModLink = "16633"; await browse.OpenLinkCommand.ExecuteAsync(null);
            var first = browse.DownloadAsync(Path.Combine(scope.Root, "first.zip"));
            await WaitForBrowseAuthorizationAsync(browse);
            await online.OpenAsync(target);
            online.SelectedFile = online.Files[1];
            var second = online.DownloadAsync(Path.Combine(scope.Root, "second.zip"));
            await WaitForOnlineAuthorizationAsync(online);
            await online.CloseCommand.ExecuteAsync(null);
            Assert.False(online.IsOpen);
            Assert.True(online.IsWaitingForWebsite);
            main.SelectedTabIndex = 2;
            main.ReceiveBrowserProtocolLink("nxm://helldivers2/mods/16633/files/457?key=test-ui-free-key&expires=4102444800&user_id=789");
            await second.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, main.SelectedTabIndex);
            Assert.True(File.Exists(Path.Combine(scope.Root, "second.zip")));
            Assert.True(browse.IsWaitingForWebsite);
            await browse.StopAsync(); await first;
        }
        finally { await browse.CloseAsync(); await online.StopAsync(); }
    }

    private static Task WaitForOnlineAuthorizationAsync(ModOnlineViewModel vm)
        => WaitForPlatformStateAsync(() => vm.IsWaitingForWebsite, () => vm.Message);

    private static async Task WaitForPlatformStateAsync(Func<bool> ready, Func<string>? diagnostic = null)
    {
        for (var attempt = 0; attempt < 500 && !ready(); attempt++) await Task.Delay(10);
        Assert.True(ready(), diagnostic?.Invoke());
    }
}
