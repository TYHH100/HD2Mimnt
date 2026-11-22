using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 对单个目标进程应用、验证和恢复调度。实现保存每个进程的原始值与最后应用值，
/// 恢复前必须确认当前值仍是最后应用值，避免覆盖其他软件后来的修改。
/// </summary>
public interface IProcessSchedulingController
{
    CpuSchedulingOperationResult Apply(int processId, CpuTopology topology, CpuSchedulingMode mode);

    CpuSchedulingVerification Verify(int processId, CpuSchedulingMode mode);

    CpuSchedulingOperationResult Restore(int processId, bool force = false);

    void Forget(int processId);
}