using System.ComponentModel;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Scheduling;

/// <summary>
/// 应用/验证/恢复单个进程的 CPU 调度。原始值与最后应用值只保存在内存中，
/// 恢复前先确认当前值仍是最后应用值，避免覆盖其他软件后来的修改。
/// </summary>
public sealed class Win32ProcessSchedulingController : IProcessSchedulingController
{
    private sealed class AppliedState
    {
        public required long StartTimeUtcTicks { get; init; }
        public required CpuSchedulingMode Mode { get; init; }
        public uint[] OriginalCpuSets { get; init; } = [];
        public uint[] AppliedCpuSets { get; init; } = [];
        public ulong? OriginalAffinity { get; init; }
        public ulong? AppliedAffinity { get; init; }

        // 性能核跨组时，严格模式退化为按组掩码的进程默认 CPU Sets（Win11+，软限制）。
        public bool UsesMasks { get; init; }
        public ProcessorGroupMask[] OriginalMasks { get; init; } = [];
        public ProcessorGroupMask[] AppliedMasks { get; init; } = [];
    }

    private readonly IProcessSchedulingNativeApi _native;
    private readonly Dictionary<int, AppliedState> _states = new();
    private readonly object _gate = new();

    public Win32ProcessSchedulingController(IProcessSchedulingNativeApi native)
    {
        _native = native;
    }

    public CpuSchedulingOperationResult Apply(int processId, CpuTopology topology, CpuSchedulingMode mode)
    {
        if (mode == CpuSchedulingMode.Off)
            return CpuSchedulingOperationResult.Fail(CpuSchedulingFailureKind.Unsupported, "scheduling mode is off");
        if (!topology.Supported)
            return CpuSchedulingOperationResult.Fail(CpuSchedulingFailureKind.Unsupported, topology.Reason);
        if (mode == CpuSchedulingMode.PreferPerformanceCores && topology.PerformanceSets.Count == 0)
            return CpuSchedulingOperationResult.Fail(CpuSchedulingFailureKind.Unsupported, "no performance CPU sets");
        if (mode == CpuSchedulingMode.PerformanceCoresOnly)
        {
            var strictAvailable = topology.StrictAffinityMask is not (null or 0)
                                  || topology.UsablePerformanceGroupMasks.Count > 0;
            if (!strictAvailable)
            {
                return CpuSchedulingOperationResult.Fail(
                    CpuSchedulingFailureKind.Unsupported,
                    "strict mode is unavailable for this topology or processor group");
            }
        }

        if (!_native.TryOpenProcess(processId, out var handle, out var error))
            return CpuSchedulingOperationResult.Fail(MapOpenFailure(error), "OpenProcess failed", error);

        try
        {
            var startTicks = _native.TryGetStartTimeUtcTicks(processId);
            return mode == CpuSchedulingMode.PreferPerformanceCores
                ? ApplyCpuSets(processId, handle, topology, startTicks)
                : ApplyAffinity(processId, handle, topology, startTicks);
        }
        catch (Win32Exception ex)
        {
            return CpuSchedulingOperationResult.Fail(MapFailure(ex), ex.Message, ex.NativeErrorCode);
        }
        finally
        {
            _native.CloseHandle(handle);
        }
    }

    public CpuSchedulingVerification Verify(int processId, CpuSchedulingMode mode)
    {
        AppliedState? state;
        lock (_gate)
        {
            if (!_states.TryGetValue(processId, out state))
                return new CpuSchedulingVerification(false, false, "no applied state");
            if (state.Mode != mode)
                return new CpuSchedulingVerification(true, false, "applied mode differs");
        }

        var startTicks = _native.TryGetStartTimeUtcTicks(processId);
        if (state.StartTimeUtcTicks != 0 && startTicks != 0 && startTicks != state.StartTimeUtcTicks)
        {
            Forget(processId);
            return new CpuSchedulingVerification(false, false, "pid was reused by a new process");
        }

        if (!_native.TryOpenProcess(processId, out var handle, out var error))
        {
            if (error is 87 or 1168)
            {
                Forget(processId);
                return new CpuSchedulingVerification(false, false, "process ended");
            }

            return new CpuSchedulingVerification(true, false, "OpenProcess failed", error);
        }

        try
        {
            if (state.Mode == CpuSchedulingMode.PreferPerformanceCores)
            {
                var current = _native.GetProcessDefaultCpuSets(handle);
                return new CpuSchedulingVerification(true, SetEquals(current, state.AppliedCpuSets));
            }

            if (state.UsesMasks)
            {
                if (!_native.TryGetProcessDefaultCpuSetMasks(handle, out var currentMasks, out var masksError))
                    return new CpuSchedulingVerification(true, false, "GetProcessDefaultCpuSetMasks failed", masksError);

                return new CpuSchedulingVerification(true, MasksEquals(currentMasks, state.AppliedMasks));
            }

            var (processMask, _) = _native.GetProcessAffinityMask(handle);
            return new CpuSchedulingVerification(true, processMask == state.AppliedAffinity);
        }
        catch (Win32Exception ex)
        {
            return new CpuSchedulingVerification(true, false, ex.Message, ex.NativeErrorCode);
        }
        finally
        {
            _native.CloseHandle(handle);
        }
    }

    public CpuSchedulingOperationResult Restore(int processId, bool force = false)
    {
        AppliedState? state;
        lock (_gate)
        {
            if (!_states.TryGetValue(processId, out state))
                return CpuSchedulingOperationResult.Ok("no saved state");
        }

        var startTicks = _native.TryGetStartTimeUtcTicks(processId);
        if (state.StartTimeUtcTicks != 0 && startTicks != 0 && startTicks != state.StartTimeUtcTicks)
        {
            Forget(processId);
            return CpuSchedulingOperationResult.Ok("process ended or pid was reused");
        }

        if (!_native.TryOpenProcess(processId, out var handle, out var error))
        {
            if (error is 87 or 1168)
            {
                Forget(processId);
                return CpuSchedulingOperationResult.Ok("process ended");
            }

            return CpuSchedulingOperationResult.Fail(MapOpenFailure(error), "OpenProcess failed", error);
        }

        try
        {
            if (state.Mode == CpuSchedulingMode.PreferPerformanceCores)
            {
                var current = _native.GetProcessDefaultCpuSets(handle);
                if (!force && !SetEquals(current, state.AppliedCpuSets))
                {
                    return CpuSchedulingOperationResult.Fail(
                        CpuSchedulingFailureKind.Changed,
                        "current CPU sets differ from the applied sets; refusing to overwrite");
                }

                _native.SetProcessDefaultCpuSets(handle, state.OriginalCpuSets);
                var restored = _native.GetProcessDefaultCpuSets(handle);
                if (!SetEquals(restored, state.OriginalCpuSets))
                {
                    return CpuSchedulingOperationResult.Fail(
                        CpuSchedulingFailureKind.VerificationFailed,
                        "restored CPU sets do not match the original sets");
                }
            }
            else if (state.UsesMasks)
            {
                if (!_native.TryGetProcessDefaultCpuSetMasks(handle, out var currentMasks, out _)
                    || !MasksEquals(currentMasks, state.AppliedMasks))
                {
                    if (!force)
                    {
                        return CpuSchedulingOperationResult.Fail(
                            CpuSchedulingFailureKind.Changed,
                            "current CPU set masks differ from the applied masks; refusing to overwrite");
                    }
                }

                // 原始默认集合可能是按 Set ID 设置的；GetProcessDefaultCpuSetMasks 会把
                // 它映射到宿主处理器，因此按掩码写回等价于恢复原状。
                if (!_native.TrySetProcessDefaultCpuSetMasks(handle, state.OriginalMasks, out var restoreError)
                    || !_native.TryGetProcessDefaultCpuSetMasks(handle, out var restoredMasks, out _)
                    || !MasksEquals(restoredMasks, state.OriginalMasks))
                {
                    return CpuSchedulingOperationResult.Fail(
                        CpuSchedulingFailureKind.VerificationFailed,
                        "restored CPU set masks do not match the original masks",
                        restoreError == 0 ? null : restoreError);
                }
            }
            else
            {
                var (currentMask, _) = _native.GetProcessAffinityMask(handle);
                if (!force && currentMask != state.AppliedAffinity)
                {
                    return CpuSchedulingOperationResult.Fail(
                        CpuSchedulingFailureKind.Changed,
                        "current affinity differs from the applied mask; refusing to overwrite");
                }

                _native.SetProcessAffinityMask(handle, state.OriginalAffinity ?? currentMask);
                var (restoredMask, _) = _native.GetProcessAffinityMask(handle);
                if (restoredMask != state.OriginalAffinity)
                {
                    return CpuSchedulingOperationResult.Fail(
                        CpuSchedulingFailureKind.VerificationFailed,
                        "restored affinity does not match the original mask");
                }
            }

            Forget(processId);
            return CpuSchedulingOperationResult.Ok("original scheduling restored");
        }
        catch (Win32Exception ex)
        {
            return CpuSchedulingOperationResult.Fail(MapFailure(ex), ex.Message, ex.NativeErrorCode);
        }
        finally
        {
            _native.CloseHandle(handle);
        }
    }

    public void Forget(int processId)
    {
        lock (_gate)
        {
            _states.Remove(processId);
        }
    }

    private CpuSchedulingOperationResult ApplyCpuSets(
        int processId, IntPtr handle, CpuTopology topology, long startTicks)
    {
        var target = topology.UsablePerformanceSets.Select(set => set.Id).Distinct().ToArray();
        if (target.Length == 0)
            return CpuSchedulingOperationResult.Fail(CpuSchedulingFailureKind.Unsupported, "no performance CPU sets");

        var original = _native.GetProcessDefaultCpuSets(handle);
        _native.SetProcessDefaultCpuSets(handle, target);
        var applied = _native.GetProcessDefaultCpuSets(handle);

        lock (_gate)
        {
            _states[processId] = new AppliedState
            {
                StartTimeUtcTicks = startTicks,
                Mode = CpuSchedulingMode.PreferPerformanceCores,
                OriginalCpuSets = original,
                AppliedCpuSets = applied
            };
        }

        return SetEquals(applied, target)
            ? CpuSchedulingOperationResult.Ok("performance-core CPU sets applied")
            : CpuSchedulingOperationResult.Fail(
                CpuSchedulingFailureKind.VerificationFailed,
                "applied CPU sets do not match the requested sets");
    }

    private CpuSchedulingOperationResult ApplyAffinity(
        int processId, IntPtr handle, CpuTopology topology, long startTicks)
    {
        // 单组（或 P 核都在组 0 且编号 <64）走硬亲和；跨组时 Win11+ 退化为
        // 按组掩码的进程默认 CPU Sets（软限制），旧系统明确报告不支持。
        if (topology.StrictAffinityMask is { } target && target != 0)
            return ApplyHardAffinity(processId, handle, target, startTicks);

        return ApplyDefaultCpuSetMasks(processId, handle, topology, startTicks);
    }

    private CpuSchedulingOperationResult ApplyHardAffinity(
        int processId, IntPtr handle, ulong target, long startTicks)
    {
        var (original, systemMask) = _native.GetProcessAffinityMask(handle);
        if ((target & ~systemMask) != 0)
        {
            return CpuSchedulingOperationResult.Fail(
                CpuSchedulingFailureKind.Unsupported,
                "performance-core mask is outside the system affinity mask");
        }

        _native.SetProcessAffinityMask(handle, target);
        var (applied, _) = _native.GetProcessAffinityMask(handle);

        lock (_gate)
        {
            _states[processId] = new AppliedState
            {
                StartTimeUtcTicks = startTicks,
                Mode = CpuSchedulingMode.PerformanceCoresOnly,
                OriginalAffinity = original,
                AppliedAffinity = applied
            };
        }

        return applied == target
            ? CpuSchedulingOperationResult.Ok("performance-core affinity applied")
            : CpuSchedulingOperationResult.Fail(
                CpuSchedulingFailureKind.VerificationFailed,
                "applied affinity does not match the requested mask");
    }

    private CpuSchedulingOperationResult ApplyDefaultCpuSetMasks(
        int processId, IntPtr handle, CpuTopology topology, long startTicks)
    {
        var target = topology.UsablePerformanceGroupMasks.ToArray();
        if (target.Length == 0)
        {
            return CpuSchedulingOperationResult.Fail(
                CpuSchedulingFailureKind.Unsupported,
                "no expressible performance-core group masks");
        }

        if (!_native.TryGetProcessDefaultCpuSetMasks(handle, out var original, out var error))
        {
            return error == 127
                ? CpuSchedulingOperationResult.Fail(
                    CpuSchedulingFailureKind.Unsupported,
                    "strict mode across processor groups requires Windows 11 or Server 2022+", error)
                : CpuSchedulingOperationResult.Fail(
                    MapFailure(new Win32Exception(error)), "GetProcessDefaultCpuSetMasks failed", error);
        }

        if (!_native.TrySetProcessDefaultCpuSetMasks(handle, target, out error))
        {
            return CpuSchedulingOperationResult.Fail(
                MapFailure(new Win32Exception(error)), "SetProcessDefaultCpuSetMasks failed", error);
        }

        if (!_native.TryGetProcessDefaultCpuSetMasks(handle, out var applied, out _))
        {
            return CpuSchedulingOperationResult.Fail(
                CpuSchedulingFailureKind.VerificationFailed, "GetProcessDefaultCpuSetMasks failed after set");
        }

        lock (_gate)
        {
            _states[processId] = new AppliedState
            {
                StartTimeUtcTicks = startTicks,
                Mode = CpuSchedulingMode.PerformanceCoresOnly,
                UsesMasks = true,
                OriginalMasks = original,
                AppliedMasks = applied
            };
        }

        return MasksEquals(applied, target)
            ? CpuSchedulingOperationResult.Ok("performance-core CPU set masks applied (soft affinity)")
            : CpuSchedulingOperationResult.Fail(
                CpuSchedulingFailureKind.VerificationFailed,
                "applied CPU set masks do not match the requested masks");
    }

    private static bool SetEquals(IReadOnlyCollection<uint> left, IReadOnlyCollection<uint> right)
        => left.Count == right.Count
           && left.OrderBy(value => value).SequenceEqual(right.OrderBy(value => value));

    private static bool MasksEquals(IReadOnlyCollection<ProcessorGroupMask> left, IReadOnlyCollection<ProcessorGroupMask> right)
        => left.Count == right.Count
           && left.OrderBy(mask => mask.Group)
               .SequenceEqual(right.OrderBy(mask => mask.Group));

    private static CpuSchedulingFailureKind MapOpenFailure(int error) => error switch
    {
        5 => CpuSchedulingFailureKind.PermissionDenied,
        87 or 1168 => CpuSchedulingFailureKind.ProcessNotFound,
        _ => CpuSchedulingFailureKind.Other
    };

    private static CpuSchedulingFailureKind MapFailure(Win32Exception exception) => exception.NativeErrorCode switch
    {
        5 => CpuSchedulingFailureKind.PermissionDenied,
        87 => CpuSchedulingFailureKind.Unsupported,
        _ => CpuSchedulingFailureKind.Other
    };
}