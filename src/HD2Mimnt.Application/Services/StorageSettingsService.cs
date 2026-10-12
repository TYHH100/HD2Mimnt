using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Storage;

namespace HD2Mimnt.Application.Services;

/// <summary>运行中仅保存搬迁计划；数据库和目录的切换在下一次启动、业务服务创建前完成。</summary>
public sealed class StorageSettingsService
{
    private readonly StorageLocationStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public StorageLocations Current => _store.State.Current;
    public StorageLocations Selected => _store.State.Pending ?? Current;
    public StorageLocations Defaults => _store.Defaults;
    public bool HasPendingMigration => _store.State.Pending != null;
    public bool RestartRequired => HasPendingMigration;
    public string? StartupMessage { get; private set; }

    public StorageSettingsService(StorageLocationStore store) => _store = store;

    public async Task SaveAsync(StorageLocations locations, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(async () =>
            {
                var migration = new StorageMigration(_store);
                var normalized = migration.Validate(locations);
                await migration.CheckLinkedDeploymentsAsync(normalized, cancellationToken);
                var changed = !StorageLocationStore.Same(normalized.AppDataPath, Current.AppDataPath) ||
                    !StorageLocationStore.Same(normalized.ModsPath, Current.ModsPath) ||
                    !StorageLocationStore.Same(normalized.CachePath, Current.CachePath);
                await _store.SaveAsync(new(Current)
                {
                    Pending = changed ? normalized : null,
                    MigrationId = changed ? Guid.NewGuid().ToString("N") : null,
                    CleanupFiles = _store.State.CleanupFiles
                }, cancellationToken);
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StartupMessage = null;
            if (HasPendingMigration)
                StartupMessage = await Task.Run(() => new StorageMigration(_store).ApplyAsync(cancellationToken), cancellationToken);
            else if (_store.State.MigrationId != null || _store.State.CleanupFiles.Count > 0)
                // 只重试已提交迁移的清理，不搬目录，也不触发迁移界面。
                StartupMessage = await Task.Run(() => new StorageMigration(_store).CleanupCommittedAsync(), cancellationToken);
        }
        finally { _gate.Release(); }
    }
}
