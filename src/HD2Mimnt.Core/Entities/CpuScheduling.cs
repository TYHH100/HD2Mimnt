using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>单个 CPU Set 的只读快照；Id 仅用于设置进程默认集合，不持久化。</summary>
public sealed record CpuSetSnapshot(
    uint Id,
    ushort Group,
    byte LogicalProcessorIndex,
    byte CoreIndex,
    byte EfficiencyClass,
    bool Parked);

/// <summary>处理器组亲和掩码（GROUP_AFFINITY 的语义投影）；Mask 是组内逻辑处理器位图。</summary>
public readonly record struct ProcessorGroupMask(ushort Group, ulong Mask);

/// <summary>物理核心快照；同一 CoreIndex 的多个 Set 是 SMT 兄弟线程。</summary>
public sealed record CpuCoreSnapshot(
    ushort Group,
    byte CoreIndex,
    byte EfficiencyClass,
    IReadOnlyList<CpuSetSnapshot> Sets)
{
    public bool HasSmt => Sets.Count > 1;
}

/// <summary>
/// 统一 CPU 拓扑模型。分类纯函数（<see cref="Analyze"/>）可在测试中直接使用固定输入，
/// 不依赖 Windows API；无法可靠识别时 Supported=false，不猜测 P/E。
/// </summary>
public sealed class CpuTopology
{
    private CpuTopology(
        string cpuName,
        IReadOnlyList<CpuSetSnapshot> allSets,
        IReadOnlyList<CpuSetSnapshot> usableSets,
        IReadOnlyList<CpuCoreSnapshot> cores,
        IReadOnlyList<CpuCoreSnapshot> performanceCores,
        IReadOnlyList<CpuCoreSnapshot> efficiencyCores,
        byte? performanceEfficiencyClass,
        bool supported,
        string reason)
    {
        CpuName = cpuName;
        AllSets = allSets;
        UsableSets = usableSets;
        Cores = cores;
        PerformanceCores = performanceCores;
        EfficiencyCores = efficiencyCores;
        PerformanceEfficiencyClass = performanceEfficiencyClass;
        Supported = supported;
        Reason = reason;
    }

    public string CpuName { get; }
    public IReadOnlyList<CpuSetSnapshot> AllSets { get; }
    public IReadOnlyList<CpuSetSnapshot> UsableSets { get; }
    public IReadOnlyList<CpuCoreSnapshot> Cores { get; }
    public IReadOnlyList<CpuCoreSnapshot> PerformanceCores { get; }
    public IReadOnlyList<CpuCoreSnapshot> EfficiencyCores { get; }

    /// <summary>异构系统中性能核的 EfficiencyClass；未识别时为 null。</summary>
    public byte? PerformanceEfficiencyClass { get; }
    public bool Supported { get; }
    public string Reason { get; }
    public int ParkedCount => AllSets.Count(set => set.Parked);
    public int PerformanceCoreCount => PerformanceCores.Count;
    public int EfficiencyCoreCount => EfficiencyCores.Count;
    public int PerformanceLogicalProcessorCount => PerformanceCores.Sum(core => core.Sets.Count);
    public int EfficiencyLogicalProcessorCount => EfficiencyCores.Sum(core => core.Sets.Count);
    public bool HasSmt => Cores.Any(core => core.HasSmt);

    public IReadOnlyList<CpuSetSnapshot> PerformanceSets =>
        PerformanceCores.SelectMany(core => core.Sets).OrderBy(set => set.LogicalProcessorIndex).ToArray();

    public IReadOnlyList<CpuSetSnapshot> EfficiencySets =>
        EfficiencyCores.SelectMany(core => core.Sets).OrderBy(set => set.LogicalProcessorIndex).ToArray();

    /// <summary>未停车的性能核集合；是实际可用的应用目标，停车集合不参与应用。</summary>
    public IReadOnlyList<CpuSetSnapshot> UsablePerformanceSets =>
        PerformanceSets.Where(set => !set.Parked).ToArray();

    /// <summary>
    /// 可用性能核按处理器组聚合的亲和掩码（GROUP_AFFINITY 语义）。
    /// 跨组时单掩码不可用，但 Windows 11+ 可用它逐组设置进程默认 CPU Set 掩码；
    /// 编号 ≥64 的集合无法用掩码表达，直接排除。
    /// </summary>
    public IReadOnlyList<ProcessorGroupMask> UsablePerformanceGroupMasks =>
        UsablePerformanceSets
            .Where(set => set.LogicalProcessorIndex < 64)
            .GroupBy(set => set.Group)
            .Select(group => new ProcessorGroupMask(
                group.Key,
                group.Aggregate(0UL, (mask, set) => mask | (1UL << set.LogicalProcessorIndex))))
            .OrderBy(mask => mask.Group)
            .ToArray();

    /// <summary>
    /// 严格模式的组内掩码。只有性能核全部位于组 0 且编号小于 64 时可用；
    /// 跨组或高编号返回 null，由调用方明确降级，不产生错误掩码。
    /// </summary>
    public ulong? StrictAffinityMask
    {
        get
        {
            if (!Supported || PerformanceCores.Count == 0 || UsablePerformanceSets.Count == 0)
                return null;
            if (UsableSets.Any(set => set.Group != 0))
                return null;

            ulong mask = 0;
            foreach (var set in UsablePerformanceSets)
            {
                if (set.LogicalProcessorIndex >= 64)
                    return null;
                mask |= 1UL << set.LogicalProcessorIndex;
            }

            return mask;
        }
    }

    public static CpuTopology Analyze(IEnumerable<CpuSetSnapshot> sets, string? cpuName = null)
    {
        var allSets = sets.ToArray();
        var usableSets = allSets.Where(set => !set.Parked).ToArray();
        var name = cpuName ?? string.Empty;

        if (usableSets.Length == 0)
            return new CpuTopology(name, allSets, usableSets, [], [], [], null, false, "no usable CPU sets");

        // 分类必须覆盖全部集合：Windows 可能停车大量核心（笔记本上常见 E-Core 全部停车），
        // Parked 集合的 EfficiencyClass 仍然有效；只用可用集合分类会把大小核误判为同构。
        var cores = allSets
            .GroupBy(set => (set.Group, set.CoreIndex))
            .Select(group => new CpuCoreSnapshot(
                group.Key.Group,
                group.Key.CoreIndex,
                group.Max(set => set.EfficiencyClass),
                group.OrderBy(set => set.LogicalProcessorIndex).ToArray()))
            .OrderBy(core => core.Group)
            .ThenBy(core => core.CoreIndex)
            .ToArray();

        var classes = cores.Select(core => core.EfficiencyClass).Distinct().OrderByDescending(value => value).ToArray();
        if (classes.Length <= 1)
        {
            var value = classes.Length == 0 ? 0 : classes[0];
            // 同档位（无大小核之分）：全部核心按性能核展示，保持 Supported=false——
            // 全部核心都是性能核时，"优先/仅性能核"都等价于无操作，调度没有意义；
            // 展示计数让用户看到识别结果而不是 0/0。
            return new CpuTopology(
                name, allSets, usableSets, cores, cores, [], null, false,
                $"no heterogeneous cores (all EfficiencyClass = {value})");
        }

        var bestClass = classes[0];
        var performanceCores = cores.Where(core => core.EfficiencyClass == bestClass).ToArray();
        var efficiencyCores = cores.Where(core => core.EfficiencyClass != bestClass).ToArray();
        return new CpuTopology(
            name, allSets, usableSets, cores, performanceCores, efficiencyCores, bestClass, true,
            "heterogeneous cores detected");
    }
}

/// <summary>游戏 CPU 调度设置；默认全部关闭，旧配置缺键时自动落到这里。</summary>
public sealed record CpuSchedulingSettings(
    bool Enabled,
    CpuSchedulingMode Mode,
    bool ApplyOnLaunch,
    bool ReapplyOnRestart,
    bool MonitorChanges,
    bool RestoreOriginal)
{
    public static CpuSchedulingSettings Default { get; } =
        new(false, CpuSchedulingMode.Off, true, true, true, true);

    public bool IsActive => Enabled && Mode != CpuSchedulingMode.Off;
}

public sealed record CpuSchedulingOperationResult(
    bool Success,
    CpuSchedulingFailureKind Failure,
    string Message,
    int? Win32Error = null)
{
    public static CpuSchedulingOperationResult Ok(string message = "OK") =>
        new(true, CpuSchedulingFailureKind.None, message);

    public static CpuSchedulingOperationResult Fail(
        CpuSchedulingFailureKind failure, string message, int? win32Error = null) =>
        new(false, failure, message, win32Error);
}

public sealed record CpuSchedulingVerification(
    bool ProcessAlive,
    bool Matches,
    string? Detail = null,
    int? Win32Error = null);

public sealed class CpuSchedulingStatusChangedEventArgs : EventArgs
{
    public required CpuSchedulingStatus Status { get; init; }
    public required string Detail { get; init; }
    public int? ProcessId { get; init; }
    public CpuTopology? Topology { get; init; }
}