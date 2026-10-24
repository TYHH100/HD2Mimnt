using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Application.Services;

public sealed partial class ModCompatibilityService
{
    public async Task<CompatibilityFullRepairPlan> CreateFullRepairPlanAsync(Guid modId,
        CompatibilityRepairOptions? options = null, CancellationToken cancellationToken = default)
    {
        var context = await GetRepairContextAsync(cancellationToken);
        return await _analyzer.CreateFullRepairPlanAsync(await ResolvePathAsync(modId, cancellationToken),
            context.GamePath, context.Sources, options ?? new(), cancellationToken);
    }

    public async Task<CompatibilityRepairResult> RepairFullAsync(Guid modId,
        CompatibilityRepairOptions? options = null, CancellationToken cancellationToken = default)
    {
        var context = await GetRepairContextAsync(cancellationToken);
        return await _mods.MutateCompatibilityAsync(modId, path => _analyzer.RepairFullAsync(path,
            context.GamePath, context.Sources, options ?? new(), cancellationToken), cancellationToken);
    }

    public Task<CompatibilityFullRepairPlan> CreateCompanionRecoveryPlanAsync(Guid modId, CancellationToken cancellationToken = default)
        => CreateFullRepairPlanAsync(modId, CompanionOptions(), cancellationToken);

    public Task<CompatibilityRepairResult> RecoverCompanionsAsync(Guid modId, CancellationToken cancellationToken = default)
        => RepairFullAsync(modId, CompanionOptions(), cancellationToken);

    private static CompatibilityRepairOptions CompanionOptions() => new()
        { RepairMetadata = false, RepairUnits = false, MigrateMaterials = false, RecoverCompanions = true };

    public async Task<IReadOnlyList<CompatibilityBackupEntry>> GetBackupHistoryAsync(Guid modId, CancellationToken cancellationToken = default)
        => await _analyzer.GetBackupHistoryAsync(await ResolvePathAsync(modId, cancellationToken), cancellationToken);

    public Task<CompatibilityRepairResult> RollbackToAsync(Guid modId, DateTime targetUtc, CancellationToken cancellationToken = default)
        => _mods.MutateCompatibilityAsync(modId, path => _analyzer.RollbackToAsync(path, targetUtc, cancellationToken), cancellationToken);

    public async Task<int> DeleteBackupAsync(Guid modId, string backupPath, CancellationToken cancellationToken = default)
        => await _analyzer.DeleteBackupAsync(await ResolvePathAsync(modId, cancellationToken), backupPath, cancellationToken);

    public async Task<int> CleanOldBackupsAsync(Guid modId, int keepPerFile = 3, CancellationToken cancellationToken = default)
        => await _analyzer.CleanOldBackupsAsync(await ResolvePathAsync(modId, cancellationToken), keepPerFile, cancellationToken);

    public Task<IReadOnlyList<CompatibilityBatchItem>> CreateBatchRepairPlanAsync(IEnumerable<Guid> modIds,
        CompatibilityRepairOptions? options = null, IProgress<CompatibilityBatchProgress>? progress = null,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<Guid, CompatibilityRepairOptions>? perModOptions = null)
        => ProcessBatchAsync(modIds, options ?? new(), false, progress, cancellationToken, perModOptions);

    public Task<IReadOnlyList<CompatibilityBatchItem>> RepairBatchAsync(IEnumerable<Guid> modIds,
        CompatibilityRepairOptions? options = null, IProgress<CompatibilityBatchProgress>? progress = null,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<Guid, CompatibilityRepairOptions>? perModOptions = null)
        => ProcessBatchAsync(modIds, options ?? new(), true, progress, cancellationToken, perModOptions);

    private async Task<IReadOnlyList<CompatibilityBatchItem>> ProcessBatchAsync(IEnumerable<Guid> modIds,
        CompatibilityRepairOptions options, bool execute, IProgress<CompatibilityBatchProgress>? progress, CancellationToken token,
        IReadOnlyDictionary<Guid, CompatibilityRepairOptions>? perModOptions)
    {
        var ids = modIds.Distinct().ToArray();
        var mods = (await _modRepository.GetByIdsAsync(ids, token)).ToDictionary(mod => mod.Id);
        var context = await GetRepairContextAsync(token);
        var items = new List<CompatibilityBatchItem>(ids.Length);
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            var itemOptions = perModOptions?.GetValueOrDefault(id) ?? options;
            CompatibilityBatchItem item;
            if (!mods.TryGetValue(id, out var mod))
                item = new CompatibilityBatchItem { ModId = id, State = CompatibilityBatchState.Failed, Error = "模组不存在。" };
            else
            {
                try
                {
                    if (execute)
                    {
                        var result = await _mods.MutateCompatibilityAsync(id, path => _analyzer.RepairFullAsync(path,
                            context.GamePath, context.Sources, itemOptions, token), token);
                        item = new CompatibilityBatchItem { ModId = id, Name = mod.Name, Result = result,
                            State = result.Success ? result.Changed ? CompatibilityBatchState.Repaired : CompatibilityBatchState.NoAction
                                : CompatibilityBatchState.Failed, Error = result.ErrorMessage };
                    }
                    else
                    {
                        var plan = await _analyzer.CreateFullRepairPlanAsync(mod.Path, context.GamePath, context.Sources, itemOptions, token);
                        item = new CompatibilityBatchItem { ModId = id, Name = mod.Name, Plan = plan,
                            State = plan.BlockingReasons.Count > 0 ? CompatibilityBatchState.Blocked
                                : plan.CanRepair ? CompatibilityBatchState.Repairable : CompatibilityBatchState.NoAction };
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { item = new CompatibilityBatchItem { ModId = id, Name = mod.Name, State = CompatibilityBatchState.Failed, Error = ex.Message }; }
            }
            items.Add(item);
            progress?.Report(new CompatibilityBatchProgress { Completed = items.Count, Total = ids.Length, Item = item });
        }
        return items;
    }

    private async Task<(string? GamePath, string[] Sources)> GetRepairContextAsync(CancellationToken token)
    {
        var gamePath = _settings?.GetEffectiveGamePath();
        var mods = await _modRepository.GetAllAsync(token);
        return (gamePath, mods.Select(mod => mod.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
