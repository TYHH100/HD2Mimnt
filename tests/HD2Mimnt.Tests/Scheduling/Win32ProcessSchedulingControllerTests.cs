using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Scheduling;

namespace HD2Mimnt.Tests.Scheduling;

public sealed class Win32ProcessSchedulingControllerTests
{
    private const int Pid = 4242;

    [Fact]
    public void ApplyPrefer_AppliesPerformanceSetsAndSavesOriginal()
    {
        var native = new FakeNativeApi { DefaultSets = [7, 8] };
        var controller = new Win32ProcessSchedulingController(native);

        var result = controller.Apply(Pid, HybridTopology(), CpuSchedulingMode.PreferPerformanceCores);

        Assert.True(result.Success);
        Assert.Equal(new uint[] { 256, 257, 258, 259 }, native.DefaultSets.OrderBy(id => id));
        Assert.True(controller.Verify(Pid, CpuSchedulingMode.PreferPerformanceCores).Matches);
    }

    [Fact]
    public void ApplyPrefer_WhenAppliedSetsDiffer_ReportsVerificationFailure()
    {
        var native = new FakeNativeApi { IgnoreSetDefaultSets = true };
        var controller = new Win32ProcessSchedulingController(native);

        var result = controller.Apply(Pid, HybridTopology(), CpuSchedulingMode.PreferPerformanceCores);

        Assert.False(result.Success);
        Assert.Equal(CpuSchedulingFailureKind.VerificationFailed, result.Failure);
    }

    [Fact]
    public void Verify_WhenAnotherToolChangesSets_ReportsMismatch()
    {
        var native = new FakeNativeApi();
        var controller = new Win32ProcessSchedulingController(native);
        controller.Apply(Pid, HybridTopology(), CpuSchedulingMode.PreferPerformanceCores);

        native.DefaultSets = [999];

        var verification = controller.Verify(Pid, CpuSchedulingMode.PreferPerformanceCores);

        Assert.True(verification.ProcessAlive);
        Assert.False(verification.Matches);
    }

    [Fact]
    public void Restore_WhenStillApplied_RestoresOriginalSets()
    {
        var native = new FakeNativeApi { DefaultSets = [7, 8] };
        var controller = new Win32ProcessSchedulingController(native);
        controller.Apply(Pid, HybridTopology(), CpuSchedulingMode.PreferPerformanceCores);

        var result = controller.Restore(Pid);

        Assert.True(result.Success);
        Assert.Equal(new uint[] { 7, 8 }, native.DefaultSets);
        Assert.False(controller.Verify(Pid, CpuSchedulingMode.PreferPerformanceCores).ProcessAlive);
    }

    [Fact]
    public void Restore_WhenChangedExternally_RefusesUnlessForced()
    {
        var native = new FakeNativeApi();
        var controller = new Win32ProcessSchedulingController(native);
        controller.Apply(Pid, HybridTopology(), CpuSchedulingMode.PreferPerformanceCores);
        native.DefaultSets = [999];

        var refused = controller.Restore(Pid);
        Assert.False(refused.Success);
        Assert.Equal(CpuSchedulingFailureKind.Changed, refused.Failure);
        Assert.Equal(new uint[] { 999 }, native.DefaultSets);

        var forced = controller.Restore(Pid, force: true);
        Assert.True(forced.Success);
        Assert.Empty(native.DefaultSets);
    }

    [Fact]
    public void ApplyStrict_AppliesAndRestoresAffinityMask()
    {
        var native = new FakeNativeApi { Affinity = 0xFFF, SystemMask = 0xFFF };
        var controller = new Win32ProcessSchedulingController(native);

        var applied = controller.Apply(Pid, HybridTopology(), CpuSchedulingMode.PerformanceCoresOnly);
        Assert.True(applied.Success);
        Assert.Equal(0xFUL, native.Affinity);
        Assert.True(controller.Verify(Pid, CpuSchedulingMode.PerformanceCoresOnly).Matches);

        var restored = controller.Restore(Pid);
        Assert.True(restored.Success);
        Assert.Equal(0xFFFUL, native.Affinity);
    }

    [Fact]
    public void ApplyStrict_WhenPerformanceCoresCrossGroups_AndMasksApiExists_AppliesGroupMasks()
    {
        var native = new FakeNativeApi { Affinity = 0xFFF, SystemMask = 0xFFF, MasksAvailable = true };
        var controller = new Win32ProcessSchedulingController(native);
        var topology = CrossGroupTopology();

        var result = controller.Apply(Pid, topology, CpuSchedulingMode.PerformanceCoresOnly);

        Assert.True(result.Success);
        var masks = Assert.IsType<ProcessorGroupMask[]>(native.DefaultMasks);
        Assert.Equal(2, masks.Length);
        Assert.Equal(3UL, Assert.Single(masks, mask => mask.Group == 0).Mask);
        Assert.Equal(1UL, Assert.Single(masks, mask => mask.Group == 1).Mask);
        Assert.True(controller.Verify(Pid, CpuSchedulingMode.PerformanceCoresOnly).Matches);

        var restored = controller.Restore(Pid);
        Assert.True(restored.Success);
        Assert.Empty(native.DefaultMasks);
    }

    [Fact]
    public void ApplyStrict_WhenPerformanceCoresCrossGroups_AndMasksApiMissing_ReportsUnsupported()
    {
        var native = new FakeNativeApi { Affinity = 0xFFF, SystemMask = 0xFFF, MasksAvailable = false };
        var controller = new Win32ProcessSchedulingController(native);

        var result = controller.Apply(Pid, CrossGroupTopology(), CpuSchedulingMode.PerformanceCoresOnly);

        Assert.False(result.Success);
        Assert.Equal(CpuSchedulingFailureKind.Unsupported, result.Failure);
        Assert.Equal(127, result.Win32Error);
    }

    [Fact]
    public void StrictMasks_WhenChangedExternally_RestoreRefusesUnlessForced()
    {
        var native = new FakeNativeApi { MasksAvailable = true };
        var controller = new Win32ProcessSchedulingController(native);
        controller.Apply(Pid, CrossGroupTopology(), CpuSchedulingMode.PerformanceCoresOnly);

        native.DefaultMasks = [new ProcessorGroupMask(0, 0xFFFUL)];

        var refused = controller.Restore(Pid);
        Assert.False(refused.Success);
        Assert.Equal(CpuSchedulingFailureKind.Changed, refused.Failure);

        var forced = controller.Restore(Pid, force: true);
        Assert.True(forced.Success);
        Assert.Empty(native.DefaultMasks);
    }

    [Fact]
    public void OpenProcessDenied_ReturnsPermissionDeniedWithErrorCode()
    {
        var native = new FakeNativeApi { OpenDenied = true };
        var controller = new Win32ProcessSchedulingController(native);

        var result = controller.Apply(Pid, HybridTopology(), CpuSchedulingMode.PreferPerformanceCores);

        Assert.False(result.Success);
        Assert.Equal(CpuSchedulingFailureKind.PermissionDenied, result.Failure);
        Assert.Equal(5, result.Win32Error);
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

    private static CpuTopology CrossGroupTopology() => CpuTopology.Analyze(new[]
    {
        new CpuSetSnapshot(256, 0, 0, 0, 2, false),
        new CpuSetSnapshot(257, 0, 1, 0, 2, false),
        new CpuSetSnapshot(400, 1, 0, 0, 2, false),
        new CpuSetSnapshot(300, 0, 4, 4, 1, false)
    });

    private sealed class FakeNativeApi : IProcessSchedulingNativeApi
    {
        public uint[] DefaultSets { get; set; } = [];
        public ulong Affinity { get; set; } = 0xFFF;
        public ulong SystemMask { get; set; } = 0xFFF;
        public bool OpenDenied { get; init; }
        public bool IgnoreSetDefaultSets { get; init; }
        public bool Opened { get; private set; }
        public long StartTicks { get; set; } = 123;

        // MasksAvailable=false 模拟 Win10 缺少掩码 API（error=127）。
        public bool MasksAvailable { get; init; } = true;
        public ProcessorGroupMask[] DefaultMasks { get; set; } = [];

        public bool TryOpenProcess(int processId, out IntPtr handle, out int error)
        {
            if (OpenDenied)
            {
                handle = IntPtr.Zero;
                error = 5;
                return false;
            }

            Opened = true;
            handle = (IntPtr)(10000 + processId);
            error = 0;
            return true;
        }

        public void CloseHandle(IntPtr handle)
        {
        }

        public uint[] GetProcessDefaultCpuSets(IntPtr handle) => DefaultSets.ToArray();

        public void SetProcessDefaultCpuSets(IntPtr handle, IReadOnlyCollection<uint> cpuSetIds)
        {
            if (!IgnoreSetDefaultSets)
                DefaultSets = cpuSetIds.ToArray();
        }

        public (ulong ProcessMask, ulong SystemMask) GetProcessAffinityMask(IntPtr handle)
            => (Affinity, SystemMask);

        public void SetProcessAffinityMask(IntPtr handle, ulong mask) => Affinity = mask;

        public bool TryGetProcessDefaultCpuSetMasks(IntPtr handle, out ProcessorGroupMask[] masks, out int error)
        {
            if (!MasksAvailable)
            {
                masks = [];
                error = 127;
                return false;
            }

            masks = DefaultMasks.ToArray();
            error = 0;
            return true;
        }

        public bool TrySetProcessDefaultCpuSetMasks(IntPtr handle, ProcessorGroupMask[] masks, out int error)
        {
            if (!MasksAvailable)
            {
                error = 127;
                return false;
            }

            DefaultMasks = masks.ToArray();
            error = 0;
            return true;
        }

        public long TryGetStartTimeUtcTicks(int processId) => StartTicks;
    }
}