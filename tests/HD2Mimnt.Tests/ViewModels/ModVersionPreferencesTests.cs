using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Services;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Moq;

namespace HD2Mimnt.Tests.ViewModels;

public sealed class ModVersionPreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2mimnt-version-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnablingRetention_RequiresConfirmation_CancelDoesNotEnable_AcceptPersistsAndReloadDoesNotPrompt()
    {
        var settings = new SettingsService(_root);
        var cache = new Mock<IImageCacheService>(); cache.Setup(c => c.GetStats()).Returns(new ImageCacheStats());
        var confirmation = new Mock<IConfirmationService>();
        var pending = new TaskCompletionSource<bool>();
        confirmation.Setup(c => c.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(pending.Task);
        using var vm = new SettingsViewModel(settings, new GameDetectionService(settings), new NotificationService(), cache.Object,
            confirmationService: confirmation.Object);
        await vm.LoadAsync();
        Assert.False(vm.KeepModVersions); Assert.False(vm.AllowDuplicateImports);
        var changing = vm.ToggleKeepModVersionsCommand.ExecuteAsync(null);
        Assert.False(vm.KeepModVersions);
        pending.SetResult(false); await changing;
        Assert.False(vm.KeepModVersions);
        confirmation.Setup(c => c.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        await vm.ToggleKeepModVersionsCommand.ExecuteAsync(null);
        Assert.True(vm.KeepModVersions);
        Assert.False(settings.GetSetting(SettingKeys.KeepModVersions, false));
        vm.SelectedCategory = "模组库"; vm.AllowDuplicateImports = true;
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.LoadAsync();
        Assert.True(vm.KeepModVersions); Assert.True(vm.AllowDuplicateImports);
        confirmation.Verify(c => c.ConfirmAsync(It.IsAny<string>(), It.Is<string>(message => message.Contains("磁盘空间") && message.Contains("重新部署"))), Times.Exactly(2));
        await vm.ToggleKeepModVersionsCommand.ExecuteAsync(null);
        Assert.False(vm.KeepModVersions);
        confirmation.Verify(c => c.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(new SettingsService(_root).GetSetting(SettingKeys.KeepModVersions, true));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
