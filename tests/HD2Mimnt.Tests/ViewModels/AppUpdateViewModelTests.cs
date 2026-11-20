using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.ViewModels;

public sealed class AppUpdateViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntUpdateBackupTests",
        Guid.NewGuid().ToString("N"));
    private readonly List<HD2MimntDbContext> _contexts = [];
    private HD2MimntDbContext? _dbContext;

    [Fact]
    public async Task AutoCheckOnStartup_DefaultsOn_WhenSettingWasNeverStored()
    {
        var (viewModel, _) = await CreateAsync(ConfirmationChoice.Cancel);

        Assert.True(viewModel.AutoCheckOnStartup);
        Assert.False(new SettingsService(_root).GetSetting(SettingKeys.UpdatesCheckOnStartup, false));
    }

    [Fact]
    public async Task DownloadAndInstall_BacksUpFirst_WhenUserChoosesBackup()
    {
        var (viewModel, update) = await CreateAsync(ConfirmationChoice.Primary);

        await RunUpdateAsync(viewModel);

        Assert.Single(BackupFiles());
        Assert.Equal(1, update.PrepareCalls);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task DownloadAndInstall_SkipsBackup_WhenUserDeclines()
    {
        var (viewModel, update) = await CreateAsync(ConfirmationChoice.Secondary);

        await RunUpdateAsync(viewModel);

        Assert.Empty(BackupFiles());
        Assert.Equal(1, update.PrepareCalls);
    }

    [Fact]
    public async Task DownloadAndInstall_DoesNotPrepare_WhenUserCancels()
    {
        var (viewModel, update) = await CreateAsync(ConfirmationChoice.Cancel);

        await RunUpdateAsync(viewModel);

        Assert.Empty(BackupFiles());
        Assert.Equal(0, update.PrepareCalls);
    }

    [Fact]
    public async Task DownloadAndInstall_AbortsUpdate_WhenBackupFails()
    {
        var (viewModel, update) = await CreateAsync(ConfirmationChoice.Primary);
        // 先释放上下文，模拟备份时数据库连接不可用。
        _dbContext!.Dispose();

        await RunUpdateAsync(viewModel);

        Assert.Equal(0, update.PrepareCalls);
        Assert.True(viewModel.HasError);
    }

    private async Task<(AppUpdateViewModel ViewModel, FakeUpdateService Update)> CreateAsync(
        ConfirmationChoice choice)
    {
        Directory.CreateDirectory(_root);
        _dbContext = new HD2MimntDbContext(Path.Combine(_root, "hd2mimnt.db"));
        await _dbContext.InitializeDatabaseAsync();
        _contexts.Add(_dbContext);

        var settings = new SettingsService(_root);
        var update = new FakeUpdateService();
        var viewModel = new AppUpdateViewModel(
            update,
            new FakeBrowserLauncher(),
            new BackupService(_root, _dbContext, settings),
            new FakeConfirmationService(choice),
            new NotificationService(),
            settings);
        return (viewModel, update);
    }

    private static async Task RunUpdateAsync(AppUpdateViewModel viewModel)
    {
        await viewModel.CheckCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasUpdate);
        await viewModel.DownloadAndInstallCommand.ExecuteAsync(null);
    }

    private string[] BackupFiles()
    {
        var directory = Path.Combine(_root, "Backups");
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.hd2backup") : [];
    }

    public void Dispose()
    {
        foreach (var context in _contexts) context.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeConfirmationService(ConfirmationChoice choice) : IConfirmationService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<ConfirmationChoice> AskAsync(string title, string message, string primaryText,
            string secondaryText, string closeText = "Common.Cancel") => Task.FromResult(choice);
    }

    private sealed class FakeBrowserLauncher : IBrowserLauncher
    {
        public void Open(string url) { }
    }

    private sealed class FakeUpdateService : IAppUpdateService
    {
        public int PrepareCalls { get; private set; }

        public Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppUpdateCheckResult(true, "0.0.0",
                new AppUpdateInfo("9.9.9", string.Empty, "https://example.invalid/releases",
                    "https://example.invalid/update.zip", "update.zip", null)));

        public Task<AppUpdateStaging> PrepareAsync(AppUpdateInfo update,
            IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return Task.FromResult(new AppUpdateStaging(update.Version, "target", "staged", "script.ps1"));
        }

        public void LaunchPendingInstaller(AppUpdateStaging staging) { }
    }
}
