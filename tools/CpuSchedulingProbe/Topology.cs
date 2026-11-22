using Microsoft.Win32;
using CpuSchedulingProbe.Native;

namespace CpuSchedulingProbe;

internal sealed record CpuSetEntry(
    uint Id, ushort Group, byte LogicalProcessorIndex, byte CoreIndex, byte EfficiencyClass, bool Parked);

internal sealed record CpuCore(
    ushort Group, byte CoreIndex, byte EfficiencyClass, IReadOnlyList<CpuSetEntry> Sets);

internal sealed class CpuTopology
{
    private CpuTopology(
        string cpuName,
        IReadOnlyList<CpuSetEntry> allSets,
        IReadOnlyList<CpuSetEntry> usableSets,
        IReadOnlyList<CpuCore> cores,
        IReadOnlyList<CpuCore> performanceCores,
        IReadOnlyList<CpuCore> efficiencyCores,
        byte performanceEfficiencyClass,
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
    public IReadOnlyList<CpuSetEntry> AllSets { get; }
    public IReadOnlyList<CpuSetEntry> UsableSets { get; }
    public IReadOnlyList<CpuCore> Cores { get; }
    public IReadOnlyList<CpuCore> PerformanceCores { get; }
    public IReadOnlyList<CpuCore> EfficiencyCores { get; }
    public byte PerformanceEfficiencyClass { get; }
    public bool Supported { get; }
    public string Reason { get; }
    public int ParkedCount => AllSets.Count(s => s.Parked);

    public IReadOnlyList<uint> PerformanceCpuSetIds =>
        PerformanceCores.SelectMany(core => core.Sets).Select(set => set.Id).ToArray();

    public int PerformanceLogicalCount => PerformanceCores.Sum(core => core.Sets.Count);
    public int EfficiencyLogicalCount => EfficiencyCores.Sum(core => core.Sets.Count);

    public ulong StrictMask
    {
        get
        {
            if (!Supported || PerformanceCores.Count == 0)
                return 0;
            if (PerformanceCores.Select(core => core.Group).Distinct().Count() != 1)
                return 0;

            ulong mask = 0;
            foreach (var set in PerformanceCores.SelectMany(core => core.Sets))
            {
                if (set.LogicalProcessorIndex >= 64)
                    return 0;
                mask |= 1UL << set.LogicalProcessorIndex;
            }

            return mask;
        }
    }

    public bool StrictModeAvailable => StrictMask != 0;

    public static CpuTopology Load()
    {
        var allSets = CpuSetApi.Enumerate()
            .Select(set => new CpuSetEntry(
                set.Id, set.Group, set.LogicalProcessorIndex, set.CoreIndex, set.EfficiencyClass, set.Parked))
            .ToArray();

        var usableSets = allSets.Where(set => !set.Parked).ToArray();
        if (usableSets.Length == 0)
            return new CpuTopology(ReadCpuName(), allSets, usableSets, [], [], [], (byte)0, false, "no usable CPU sets");

        var cores = usableSets
            .GroupBy(set => (set.Group, set.CoreIndex))
            .Select(group => new CpuCore(
                group.Key.Group,
                group.Key.CoreIndex,
                group.First().EfficiencyClass,
                group.ToArray()))
            .ToArray();

        var efficiencyClasses = cores.Select(core => core.EfficiencyClass).Distinct().OrderByDescending(value => value).ToArray();
        if (efficiencyClasses.Length <= 1)
        {
            var value = efficiencyClasses.Length == 0 ? (byte)0 : efficiencyClasses[0];
            return new CpuTopology(
                ReadCpuName(), allSets, usableSets, cores, [], [], value, false,
                $"no heterogeneous cores (all EfficiencyClass = {value})");
        }

        var bestClass = efficiencyClasses[0];
        var performanceCores = cores.Where(core => core.EfficiencyClass == bestClass).ToArray();
        var efficiencyCores = cores.Where(core => core.EfficiencyClass != bestClass).ToArray();
        return new CpuTopology(
            ReadCpuName(), allSets, usableSets, cores, performanceCores, efficiencyCores, bestClass, true, "heterogeneous cores detected");
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string ?? "Unknown";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return "Unknown";
        }
    }
}