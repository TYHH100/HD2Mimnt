using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Moq;

namespace HD2Mimnt.Tests.ViewModels;

/// <summary>
/// 导入后的自动兼容性检查：默认关闭；开启后在导入批次完成后统一检查本次导入的模组，
/// 完成后通过通知提供查看结果入口，不自动打开检查窗口。
/// </summary>
public sealed class ImportCompatibilityCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2mimnt-import-check-" + Guid.NewGuid().ToString("N"));

    public ImportCompatibilityCheckTests() => Directory.CreateDirectory(_root);

    private (ModCompatibilityService Service, Mock<IModRepository> Repository) CreateService(params Mod[] mods)
    {
        var repository = new Mock<IModRepository>();
        repository.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(mods);
        var service = new ModCompatibilityService(repository.Object, new ModCompatibilityAnalyzer(),
            new ModService(repository.Object, Path.Combine(_root, "storage")));
        return (service, repository);
    }

    private Mod CreateEmptyMod(string name)
    {
        // 空模组目录：真实分析器返回 NotApplicable 报告，不触发游戏参考读取。
        var modDir = Path.Combine(_root, name);
        Directory.CreateDirectory(modDir);
        return new Mod { Id = Guid.NewGuid(), Path = modDir, Name = name };
    }

    [Fact]
    public async Task CheckAfterImport_DisabledByDefault_SkipsCheckWithoutNotification()
    {
        var settings = new SettingsService(_root);
        var mod = CreateEmptyMod("mod");
        var (service, repository) = CreateService(mod);
        using var vm = new ModCompatibilityViewModel(service, new Mock<IConfirmationService>().Object, settings);
        var notifications = new NotificationService();

        await vm.CheckAfterImportAsync([new ModItemViewModel(mod)], notifications);

        Assert.Empty(vm.Mods);
        Assert.False(vm.IsOpen);
        Assert.Empty(notifications.GetRecentNotifications());
        repository.Verify(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAfterImport_Enabled_ChecksImportedBatchAndNotifiesWithoutOpening()
    {
        var settings = new SettingsService(_root);
        await settings.SetSettingAsync(SettingKeys.CompatibilityAutoCheckImport, true);
        var mods = new[] { CreateEmptyMod("first"), CreateEmptyMod("second") };
        var (service, repository) = CreateService(mods);
        using var vm = new ModCompatibilityViewModel(service, new Mock<IConfirmationService>().Object, settings);
        var notifications = new NotificationService();

        await vm.CheckAfterImportAsync(mods.Select(m => new ModItemViewModel(m)), notifications);

        Assert.Equal(2, vm.Mods.Count);
        Assert.All(vm.Mods, row => Assert.NotNull(row.Report));
        Assert.Equal(UiLocalization.Current["Compatibility.ImportScope"], vm.ScopeName);
        Assert.False(vm.IsOpen);
        Assert.Contains(notifications.GetRecentNotifications(),
            notification => notification.ActionText == UiLocalization.Current["Settings.Compatibility.ShowResults"]);
        repository.Verify(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAfterImport_WhileWindowOpen_QueuesBatchAndChecksAfterClose()
    {
        var settings = new SettingsService(_root);
        await settings.SetSettingAsync(SettingKeys.CompatibilityAutoCheckImport, true);
        var other = CreateEmptyMod("other");
        var imported = CreateEmptyMod("imported");
        var (service, repository) = CreateService(other, imported);
        using var vm = new ModCompatibilityViewModel(service, new Mock<IConfirmationService>().Object, settings);
        var notifications = new NotificationService();

        // 检查窗口已打开时导入的单模组批次不能静默丢失，要排队到窗口关闭后补检。
        await vm.OpenAsync([new ModItemViewModel(other)], "manual");
        Assert.True(vm.IsOpen);
        await vm.CheckAfterImportAsync([new ModItemViewModel(imported)], notifications);

        Assert.DoesNotContain(notifications.GetRecentNotifications(),
            notification => notification.ActionText == UiLocalization.Current["Settings.Compatibility.ShowResults"]);
        await vm.CloseCommand.ExecuteAsync(null);
        await WaitAsync(() => notifications.GetRecentNotifications().Any(
            notification => notification.ActionText == UiLocalization.Current["Settings.Compatibility.ShowResults"]));

        Assert.False(vm.IsOpen);
        Assert.Equal(imported.Id, Assert.Single(vm.Mods).Id);
        Assert.NotNull(vm.Mods[0].Report);
        Assert.Equal(UiLocalization.Current["Compatibility.ImportScope"], vm.ScopeName);
        repository.Verify(r => r.GetByIdsAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { imported.Id })), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(20);
        Assert.True(condition());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
