using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Tests.Scheduling;

public sealed class GameCpuSchedulingServiceTests
{
    [Fact]
    public async Task Refresh_WhenDisabled_ReportsDisabledAndDoesNotApply()
    {
        var settings = await CreateSettingsAsync(enabled: false, mode: CpuSchedulingMode.Off);
        var controller = new FakeController();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());

        await service.RefreshAsync();

        Assert.Equal(CpuSchedulingStatus.Disabled, service.Status);
        Assert.Equal(0, controller.ApplyCalls);
    }

    [Fact]
    public async Task Refresh_WhenEnabledWithoutProcess_WaitsForGame()
    {
        var settings = await CreateSettingsAsync();
        var locator = new FakeLocator { Process = null };
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), new FakeController(), locator);

        await service.RefreshAsync();

        Assert.Equal(CpuSchedulingStatus.WaitingForGame, service.Status);
    }

    [Fact]
    public async Task Refresh_WhenProcessAppears_AppliesConfiguredMode()
    {
        var settings = await CreateSettingsAsync();
        var controller = new FakeController();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());

        await service.RefreshAsync();

        Assert.Equal(CpuSchedulingStatus.Applied, service.Status);
        Assert.Equal(1, controller.ApplyCalls);
        Assert.Equal(CpuSchedulingMode.PreferPerformanceCores, controller.LastMode);
        Assert.Equal(4242, service.CurrentProcessId);
    }

    [Fact]
    public async Task Refresh_WhenVerificationMatches_DoesNotReapply()
    {
        var settings = await CreateSettingsAsync();
        var controller = new FakeController();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());

        await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal(1, controller.ApplyCalls);
        Assert.Equal(1, controller.VerifyCalls);
        Assert.Equal(CpuSchedulingStatus.Applied, service.Status);
    }

    [Fact]
    public async Task Refresh_WhenVerificationMismatches_RestoresAndReapplies()
    {
        var settings = await CreateSettingsAsync();
        var controller = new FakeController { VerifyResult = new CpuSchedulingVerification(true, false, "changed") };
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());

        await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal(2, controller.ApplyCalls);
        Assert.Equal(1, controller.RestoreCalls);
        Assert.Equal(CpuSchedulingStatus.Applied, service.Status);
    }

    [Fact]
    public async Task Refresh_WhenPermissionDenied_StopsAutomaticRetry()
    {
        var settings = await CreateSettingsAsync();
        var controller = new FakeController
        {
            ApplyResult = CpuSchedulingOperationResult.Fail(
                CpuSchedulingFailureKind.PermissionDenied, "access denied", 5)
        };
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());

        await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal(CpuSchedulingStatus.PermissionDenied, service.Status);
        Assert.Equal(1, controller.ApplyCalls);
        Assert.Contains("5", service.StatusDetail);
    }

    [Fact]
    public async Task Refresh_WhenTopologyUnsupported_ReportsUnsupported()
    {
        var settings = await CreateSettingsAsync();
        var topology = CpuTopology.Analyze(new[]
        {
            new CpuSetSnapshot(256, 0, 0, 0, 0, false),
            new CpuSetSnapshot(257, 0, 1, 0, 0, false)
        });
        var controller = new FakeController();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider { Topology = topology }, controller, new FakeLocator());

        await service.RefreshAsync();

        Assert.Equal(CpuSchedulingStatus.Unsupported, service.Status);
        Assert.Equal(0, controller.ApplyCalls);
    }

    [Fact]
    public async Task Refresh_WhenDisabledAfterApply_RestoresCurrentProcess()
    {
        var settings = await CreateSettingsAsync();
        var controller = new FakeController();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());

        await service.RefreshAsync();
        await settings.SetSettingAsync(SettingKeys.CpuSchedulingEnabled, false);
        await service.RefreshAsync();

        Assert.Equal(1, controller.RestoreCalls);
        Assert.Equal(CpuSchedulingStatus.Disabled, service.Status);
        Assert.Null(service.CurrentProcessId);
    }

    [Fact]
    public async Task Refresh_WhenDisabledAndRestoreRefused_KeepsStateAndDoesNotOverwrite()
    {
        var settings = await CreateSettingsAsync();
        var controller = new FakeController
        {
            RestoreResult = CpuSchedulingOperationResult.Fail(
                CpuSchedulingFailureKind.Changed, "externally modified")
        };
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());
        await service.RefreshAsync();

        await settings.SetSettingAsync(SettingKeys.CpuSchedulingEnabled, false);
        await service.RefreshAsync();

        Assert.Equal(CpuSchedulingStatus.Changed, service.Status);
        Assert.Equal(4242, service.CurrentProcessId);
        Assert.Equal(1, controller.RestoreCalls);
    }

    [Fact]
    public async Task Refresh_WhenApplyOnLaunchDisabled_DoesNotApply()
    {
        var settings = await CreateSettingsAsync(applyOnLaunch: false);
        var controller = new FakeController();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());

        await service.RefreshAsync();

        Assert.Equal(CpuSchedulingStatus.WaitingForGame, service.Status);
        Assert.Equal(0, controller.ApplyCalls);
    }

    [Fact]
    public async Task Refresh_WhenGameRestartsAndReapplyDisabled_WaitsForManualAction()
    {
        var settings = await CreateSettingsAsync(reapplyOnRestart: false);
        var locator = new FakeLocator();
        var controller = new FakeController();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, locator);

        await service.RefreshAsync();
        locator.Process = new GameProcessInfo(5252, @"C:\games\Helldivers 2\bin\helldivers2.exe", 999);
        await service.RefreshAsync();

        Assert.Equal(CpuSchedulingStatus.WaitingForGame, service.Status);
        Assert.Equal(1, controller.ApplyCalls);
    }

    [Fact]
    public async Task Refresh_WhenMonitoringDisabled_DoesNotVerify()
    {
        var settings = await CreateSettingsAsync(monitorChanges: false);
        var controller = new FakeController();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());

        await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal(0, controller.VerifyCalls);
        Assert.Equal(1, controller.ApplyCalls);
    }

    [Fact]
    public async Task Refresh_WhenModeChanges_RestoresOldStateBeforeReapply()
    {
        var settings = await CreateSettingsAsync(mode: CpuSchedulingMode.PreferPerformanceCores);
        var controller = new FakeController();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());
        await service.RefreshAsync();

        await service.SaveSettingsAsync(new CpuSchedulingSettings(
            true, CpuSchedulingMode.PerformanceCoresOnly, true, true, true, true));
        await service.RefreshAsync();

        Assert.Equal(2, controller.ApplyCalls);
        Assert.Equal(1, controller.RestoreCalls);
        Assert.Equal(CpuSchedulingMode.PerformanceCoresOnly, controller.LastMode);
        Assert.Equal(CpuSchedulingStatus.Applied, service.Status);
    }

    [Fact]
    public async Task Refresh_AfterPermissionDeniedAndSettingsChange_RetriesOnce()
    {
        var settings = await CreateSettingsAsync();
        var controller = new FakeController
        {
            ApplyResult = CpuSchedulingOperationResult.Fail(
                CpuSchedulingFailureKind.PermissionDenied, "access denied", 5)
        };
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());
        await service.RefreshAsync();
        await service.RefreshAsync();
        Assert.Equal(1, controller.ApplyCalls);

        await service.SaveSettingsAsync(new CpuSchedulingSettings(
            true, CpuSchedulingMode.PreferPerformanceCores, true, true, false, true));
        await service.RefreshAsync();

        Assert.Equal(2, controller.ApplyCalls);
        Assert.Equal(CpuSchedulingStatus.PermissionDenied, service.Status);
    }

    [Fact]
    public async Task ApplyToCurrentGame_WhenProcessMissing_ReturnsProcessNotFound()
    {
        var settings = await CreateSettingsAsync();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), new FakeController(), new FakeLocator { Process = null });

        var result = await service.ApplyToCurrentGameAsync(CpuSchedulingMode.PreferPerformanceCores);

        Assert.False(result.Success);
        Assert.Equal(CpuSchedulingFailureKind.ProcessNotFound, result.Failure);
        Assert.Equal(CpuSchedulingStatus.WaitingForGame, service.Status);
    }

    [Fact]
    public async Task RestoreCurrentGame_WhenApplied_RestoresAndClearsProcess()
    {
        var settings = await CreateSettingsAsync();
        var controller = new FakeController();
        using var service = new GameCpuSchedulingService(
            settings, new FakeTopologyProvider(), controller, new FakeLocator());
        await service.RefreshAsync();

        var result = await service.RestoreCurrentGameAsync();

        Assert.True(result.Success);
        Assert.Equal(1, controller.RestoreCalls);
        Assert.Equal(CpuSchedulingStatus.Restored, service.Status);
        Assert.Null(service.CurrentProcessId);
    }

    private static async Task<SettingsService> CreateSettingsAsync(
        bool enabled = true,
        CpuSchedulingMode mode = CpuSchedulingMode.PreferPerformanceCores,
        bool applyOnLaunch = true,
        bool reapplyOnRestart = true,
        bool monitorChanges = true,
        bool restoreOriginal = true)
    {
        var settings = new SettingsService(new FakeSettingsRepository(), "unused-test");
        await settings.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.GamePath] = Path.GetTempPath(),
            [SettingKeys.CpuSchedulingEnabled] = enabled,
            [SettingKeys.CpuSchedulingMode] = mode.ToString(),
            [SettingKeys.CpuSchedulingApplyOnLaunch] = applyOnLaunch,
            [SettingKeys.CpuSchedulingReapplyOnRestart] = reapplyOnRestart,
            [SettingKeys.CpuSchedulingMonitorChanges] = monitorChanges,
            [SettingKeys.CpuSchedulingRestoreOriginal] = restoreOriginal
        });
        return settings;
    }

    private static CpuTopology HybridTopology() => CpuTopology.Analyze(new[]
    {
        new CpuSetSnapshot(256, 0, 0, 0, 2, false),
        new CpuSetSnapshot(257, 0, 1, 0, 2, false),
        new CpuSetSnapshot(258, 0, 2, 2, 2, false),
        new CpuSetSnapshot(259, 0, 3, 2, 2, false),
        new CpuSetSnapshot(300, 0, 4, 4, 1, false),
        new CpuSetSnapshot(302, 0, 5, 6, 1, false)
    });

    private sealed class FakeTopologyProvider : ICpuTopologyProvider
    {
        public CpuTopology Topology { get; set; } = HybridTopology();
        public CpuTopology GetTopology() => Topology;
    }

    private sealed class FakeLocator : IGameProcessLocator
    {
        public GameProcessInfo? Process { get; set; } =
            new(4242, @"C:\games\Helldivers 2\bin\helldivers2.exe", 100);

        public GameProcessInfo? FindGameProcess(string gameDirectory) => Process;
    }

    private sealed class FakeController : IProcessSchedulingController
    {
        public CpuSchedulingOperationResult ApplyResult { get; set; } = CpuSchedulingOperationResult.Ok();
        public CpuSchedulingVerification VerifyResult { get; set; } = new(true, true);
        public CpuSchedulingOperationResult RestoreResult { get; set; } = CpuSchedulingOperationResult.Ok();
        public int ApplyCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public CpuSchedulingMode? LastMode { get; private set; }

        public CpuSchedulingOperationResult Apply(
            int processId, CpuTopology topology, CpuSchedulingMode mode)
        {
            ApplyCalls++;
            LastMode = mode;
            return ApplyResult;
        }

        public CpuSchedulingVerification Verify(int processId, CpuSchedulingMode mode)
        {
            VerifyCalls++;
            return VerifyResult;
        }

        public CpuSchedulingOperationResult Restore(int processId, bool force = false)
        {
            RestoreCalls++;
            return RestoreResult;
        }

        public void Forget(int processId)
        {
        }
    }

    private sealed class FakeSettingsRepository : ISettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Dictionary<string, string> ReadAll() => new(_values, StringComparer.Ordinal);
        public bool IsLegacyMigrationComplete() => true;
        public void ImportLegacy(IReadOnlyDictionary<string, string> values) { }

        public Task ApplyAsync(IReadOnlyDictionary<string, string> values, IReadOnlyCollection<string> removedKeys)
        {
            foreach (var (key, value) in values)
                _values[key] = value;
            foreach (var key in removedKeys)
                _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task ResetAsync(string? category)
        {
            if (string.IsNullOrEmpty(category))
                _values.Clear();
            else
                RemovePrefix(category);
            return Task.CompletedTask;
        }

        public Task ResetCategoriesAsync(IReadOnlyCollection<string> categories)
        {
            foreach (var category in categories)
                RemovePrefix(category);
            return Task.CompletedTask;
        }

        private void RemovePrefix(string category)
        {
            var prefix = category.TrimEnd('.') + ".";
            foreach (var key in _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                _values.Remove(key);
        }
    }
}