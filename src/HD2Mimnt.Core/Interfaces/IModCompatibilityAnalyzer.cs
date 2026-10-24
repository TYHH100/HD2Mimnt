using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>补丁兼容性检查、已知格式迁移、伴随恢复与备份管理的基础契约。</summary>
public interface IModCompatibilityAnalyzer
{
    Task<ModCompatibilityReport> AnalyzeAsync(
        string modPath,
        uint? referenceVersion = null,
        Guid? modId = null,
        string? referenceSource = null,
        CancellationToken cancellationToken = default);

    Task<CompatibilityRepairPlan> CreateRepairPlanAsync(
        string modPath,
        CancellationToken cancellationToken = default);

    Task<CompatibilityRepairResult> RepairAsync(
        string modPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetBackupsAsync(string modPath, CancellationToken cancellationToken = default);
    Task<CompatibilityRepairResult> RestoreBackupAsync(string modPath, string backupPath,
        CancellationToken cancellationToken = default);

    Task<CompatibilityFullRepairPlan> CreateFullRepairPlanAsync(string modPath, string? gameDirectory,
        IReadOnlyList<string> sourceModPaths, CompatibilityRepairOptions options, CancellationToken cancellationToken = default);
    Task<CompatibilityRepairResult> RepairFullAsync(string modPath, string? gameDirectory,
        IReadOnlyList<string> sourceModPaths, CompatibilityRepairOptions options, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CompatibilityBackupEntry>> GetBackupHistoryAsync(string modPath, CancellationToken cancellationToken = default);
    Task<CompatibilityRepairResult> RollbackToAsync(string modPath, DateTime targetUtc, CancellationToken cancellationToken = default);
    Task<int> DeleteBackupAsync(string modPath, string backupPath, CancellationToken cancellationToken = default);
    Task<int> CleanOldBackupsAsync(string modPath, int keepPerFile = 3, CancellationToken cancellationToken = default);
}
