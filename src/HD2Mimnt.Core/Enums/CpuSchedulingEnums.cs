namespace HD2Mimnt.Core.Enums;

/// <summary>CPU 调度模式。默认关闭；严格模式使用 Process Affinity，其余使用 CPU Sets。</summary>
public enum CpuSchedulingMode
{
    Off,
    PreferPerformanceCores,
    PerformanceCoresOnly
}

/// <summary>调度状态机对外展示的状态。</summary>
public enum CpuSchedulingStatus
{
    Disabled,
    WaitingForGame,
    Applying,
    Applied,
    Changed,
    PermissionDenied,
    Unsupported,
    Ineffective,
    Restored,
    Error
}

/// <summary>调度失败分类，用于 UI 提示与日志。</summary>
public enum CpuSchedulingFailureKind
{
    None,
    PermissionDenied,
    ProcessNotFound,
    Unsupported,
    VerificationFailed,
    Changed,
    Other
}