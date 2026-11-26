using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 上层调度协调器：读取设置、识别拓扑、观察游戏进程、应用/验证/恢复并对外发布状态。
/// UI 与 CLI 只依赖本接口，不直接调用 Win32。
/// </summary>
public interface IGameCpuSchedulingService
{
    CpuSchedulingStatus Status { get; }
    string StatusDetail { get; }
    CpuTopology? Topology { get; }
    CpuSchedulingSettings Settings { get; }
    int? CurrentProcessId { get; }

    event EventHandler<CpuSchedulingStatusChangedEventArgs>? StatusChanged;

    /// <summary>启动后台观察循环；重复调用无副作用。</summary>
    void Start();

    /// <summary>停止观察；RestoreOriginal 开启时尽力恢复当前游戏进程。</summary>
    Task StopAsync();

    /// <summary>立即执行一轮设置读取、进程识别、应用与验证；测试和手动刷新复用此入口。</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>重新读取系统拓扑并发布状态。</summary>
    Task RefreshTopologyAsync();

    Task<CpuSchedulingSettings> LoadSettingsAsync();

    Task SaveSettingsAsync(CpuSchedulingSettings settings, CancellationToken cancellationToken = default);

    /// <summary>忽略设置开关，立即对当前游戏进程应用指定模式。</summary>
    Task<CpuSchedulingOperationResult> ApplyToCurrentGameAsync(
        CpuSchedulingMode mode, CancellationToken cancellationToken = default);

    /// <summary>恢复当前游戏进程的原始调度。</summary>
    Task<CpuSchedulingOperationResult> RestoreCurrentGameAsync(CancellationToken cancellationToken = default);
}