namespace HD2Mimnt.Core.Enums;

/// <summary>
/// Deployment Method
/// 三种部署方式：Copy、Symbolic Link、Hard Link
/// 互斥，一次部署只能使用一种方式
/// </summary>
public enum DeploymentMethod
{
    Copy,
    SymbolicLink,
    HardLink
}

/// <summary>
/// Deployment Order
/// 部署顺序：从上到下或从下到上
/// </summary>
public enum DeploymentOrder
{
    TopToBottom,
    BottomToTop
}

/// <summary>
/// Task 状态
/// </summary>
public enum TaskStatus
{
    Waiting,
    Running,
    Completed,
    PartiallyFailed,
    Failed,
    Cancelled,
    ForceTerminated
}

/// <summary>
/// Workflow 类型
/// </summary>
public enum WorkflowType
{
    Legacy,
    V1,
    IntegratedArmor
}

/// <summary>
/// 部署状态
/// </summary>
public enum DeploymentStatus
{
    Pending,
    Preparing,
    Cleaning,
    Deploying,
    Completed,
    PartiallyFailed,
    Failed,
    Cancelled
}

/// <summary>
/// 游戏来源。官方安装与 HellForge 离线服副本互斥，同一时间只使用其中一个目录。
/// </summary>
public enum GameInstallationMode
{
    Official,
    HellForge
}
