using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Tests.Scheduling;

public sealed class CpuTopologyTests
{
    [Fact]
    public void Analyze_HybridWithSmt_ClassifiesCoresAndKeepsSiblings()
    {
        var topology = HybridTopology();

        Assert.True(topology.Supported);
        Assert.Equal(2, topology.PerformanceCoreCount);
        Assert.Equal(2, topology.EfficiencyCoreCount);
        Assert.Equal(4, topology.PerformanceLogicalProcessorCount);
        Assert.Equal(2, topology.EfficiencyLogicalProcessorCount);
        Assert.True(topology.HasSmt);
        Assert.Equal(0xFUL, topology.StrictAffinityMask);
        Assert.Equal(new uint[] { 256, 257, 258, 259 }, topology.PerformanceSets.Select(set => set.Id));
    }

    [Fact]
    public void Analyze_AllEfficiencyClassZero_ReportsUnsupportedButCountsCores()
    {
        var sets = new[]
        {
            Set(256, 0, 0, 0, 0), Set(257, 0, 1, 0, 0), Set(258, 0, 2, 2, 0), Set(259, 0, 3, 2, 0)
        };

        var topology = CpuTopology.Analyze(sets);

        // 同档位机器（如全 P-Core 的 i5-12400F）：保持不支持，但展示按"全部为性能核"计数。
        Assert.False(topology.Supported);
        Assert.Equal(2, topology.PerformanceCoreCount);
        Assert.Equal(4, topology.PerformanceLogicalProcessorCount);
        Assert.Empty(topology.EfficiencyCores);
        Assert.Contains("no heterogeneous", topology.Reason);
        Assert.Null(topology.StrictAffinityMask);
        Assert.Null(topology.PerformanceEfficiencyClass);
    }

    [Fact]
    public void Analyze_ParkedSet_IsExcludedFromUsableSets()
    {
        var sets = new[]
        {
            Set(256, 0, 0, 2), Set(257, 0, 1, 2, parked: true),
            Set(300, 0, 2, 4, 1), Set(301, 0, 3, 6, 1)
        };

        var topology = CpuTopology.Analyze(sets);

        Assert.Equal(4, topology.AllSets.Count);
        Assert.Equal(3, topology.UsableSets.Count);
        Assert.Equal(1, topology.ParkedCount);
        Assert.Equal(2, topology.PerformanceLogicalProcessorCount);
        Assert.Single(topology.UsablePerformanceSets);
        Assert.Equal((uint)256, Assert.Single(topology.UsablePerformanceSets).Id);
    }

    [Fact]
    public void Analyze_ParkedEfficiencyCores_StillDetectsHeterogeneousTopology()
    {
        // 真机场景（i7-14650HX 笔记本）：Windows 停车了全部 E-Core，
        // 可用集合只剩同档位的 P-Core；分类必须包含停车集合才能识别大小核。
        var sets = new[]
        {
            Set(256, 0, 0, 0, 1), Set(257, 0, 1, 0, 1),
            Set(258, 0, 2, 2, 1), Set(259, 0, 3, 2, 1),
            Set(260, 0, 4, 4, 1), Set(261, 0, 5, 4, 1),
            Set(262, 0, 6, 6, 1), Set(263, 0, 7, 6, 1),
            Set(300, 0, 8, 8, 0, parked: true),
            Set(301, 0, 9, 10, 0, parked: true),
            Set(302, 0, 10, 12, 0, parked: true)
        };

        var topology = CpuTopology.Analyze(sets);

        Assert.True(topology.Supported);
        Assert.Equal(4, topology.PerformanceCoreCount);
        Assert.Equal(3, topology.EfficiencyCoreCount);
        Assert.Equal(8, topology.PerformanceLogicalProcessorCount);
        Assert.Equal(8, topology.UsablePerformanceSets.Count);
        Assert.Equal(3, topology.ParkedCount);
        Assert.Equal(0xFFUL, topology.StrictAffinityMask);
    }

    [Fact]
    public void Analyze_AllPerformanceCoresParked_StrictMaskUnavailable()
    {
        var sets = new[]
        {
            Set(256, 0, 0, 0, 1, parked: true), Set(257, 0, 1, 0, 1, parked: true),
            Set(300, 0, 2, 2, 0)
        };

        var topology = CpuTopology.Analyze(sets);

        Assert.True(topology.Supported);
        Assert.Empty(topology.UsablePerformanceSets);
        Assert.Null(topology.StrictAffinityMask);
    }

    [Fact]
    public void Analyze_CrossGroupPerformanceMasks_AggregatePerGroupAndSkipParked()
    {
        var sets = new[]
        {
            Set(256, 0, 0, 0, 2), Set(257, 0, 1, 0, 2), Set(258, 0, 3, 2, 2, parked: true),
            Set(400, 1, 0, 0, 2), Set(401, 1, 4, 4, 2),
            Set(300, 0, 8, 8, 1)
        };

        var topology = CpuTopology.Analyze(sets);

        Assert.True(topology.Supported);
        Assert.Null(topology.StrictAffinityMask);
        var masks = topology.UsablePerformanceGroupMasks;
        Assert.Equal(2, masks.Count);
        Assert.Equal((ushort)0, masks[0].Group);
        Assert.Equal(0b011UL, masks[0].Mask);
        Assert.Equal((ushort)1, masks[1].Group);
        Assert.Equal(0b10001UL, masks[1].Mask);
    }

    [Fact]
    public void Analyze_PerformanceCoresAcrossGroups_DisablesStrictMask()
    {
        var sets = new[]
        {
            Set(256, 0, 0, 0, 2), Set(257, 0, 1, 0, 2),
            Set(400, 1, 0, 0, 2),
            Set(300, 0, 4, 4, 1), Set(301, 1, 4, 4, 1)
        };

        var topology = CpuTopology.Analyze(sets);

        Assert.True(topology.Supported);
        Assert.Equal(3, topology.PerformanceLogicalProcessorCount);
        Assert.Null(topology.StrictAffinityMask);
    }

    [Fact]
    public void Analyze_EfficiencyCoresInAnotherGroup_DisablesStrictMask()
    {
        var sets = new[]
        {
            Set(256, 0, 0, 0, 2), Set(257, 0, 1, 1, 2),
            Set(400, 1, 0, 0, 1), Set(401, 1, 1, 2, 1)
        };

        var topology = CpuTopology.Analyze(sets);

        Assert.True(topology.Supported);
        Assert.Equal(2, topology.PerformanceCoreCount);
        Assert.Null(topology.StrictAffinityMask);
    }

    [Fact]
    public void Analyze_MoreThanTwoClasses_UsesHighestAsPerformance()
    {
        var sets = new[]
        {
            Set(256, 0, 0, 0, 3), Set(300, 0, 2, 2, 2), Set(400, 0, 4, 4, 1)
        };

        var topology = CpuTopology.Analyze(sets);

        Assert.True(topology.Supported);
        Assert.Equal((byte)3, topology.PerformanceEfficiencyClass);
        Assert.Single(topology.PerformanceCores);
        Assert.Equal(2, topology.EfficiencyCoreCount);
    }

    [Fact]
    public void Analyze_NoUsableSets_ReportsUnsupported()
    {
        var topology = CpuTopology.Analyze([Set(256, 0, 0, 0, 2, parked: true)]);

        Assert.False(topology.Supported);
        Assert.Equal(0, topology.UsableSets.Count);
        Assert.Contains("no usable", topology.Reason);
    }

    private static CpuTopology HybridTopology() => CpuTopology.Analyze(new[]
    {
        Set(256, 0, 0, 0, 2), Set(257, 0, 1, 0, 2),
        Set(258, 0, 2, 2, 2), Set(259, 0, 3, 2, 2),
        Set(300, 0, 4, 4, 1), Set(302, 0, 5, 6, 1)
    }, "test-cpu");

    private static CpuSetSnapshot Set(
        uint id, ushort group, byte logicalProcessor, byte core, byte efficiency = 2, bool parked = false)
        => new(id, group, logicalProcessor, core, efficiency, parked);
}