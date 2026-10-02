using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Repository for plugin data
/// </summary>
public interface IPluginRepository
{
    Task<Plugin?> GetByIdAsync(string pluginId, CancellationToken cancellationToken = default);
    Task<List<Plugin>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<List<Plugin>> GetEnabledAsync(CancellationToken cancellationToken = default);
    Task AddAsync(Plugin plugin, CancellationToken cancellationToken = default);
    Task UpdateAsync(Plugin plugin, CancellationToken cancellationToken = default);
    Task DeleteAsync(string pluginId, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string pluginId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Repository for plugin permissions
/// </summary>
public interface IPluginPermissionRepository
{
    Task<List<PluginPermission>> GetByPluginIdAsync(string pluginId, CancellationToken cancellationToken = default);
    Task AddAsync(PluginPermission permission, CancellationToken cancellationToken = default);
    Task UpdateAsync(PluginPermission permission, CancellationToken cancellationToken = default);
    Task DeleteByPluginIdAsync(string pluginId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Repository for plugin execution history
/// </summary>
public interface IPluginExecutionRepository
{
    Task<List<PluginExecutionHistory>> GetByPluginIdAsync(string pluginId, int limit = 100, CancellationToken cancellationToken = default);
    Task AddAsync(PluginExecutionHistory execution, CancellationToken cancellationToken = default);
    Task UpdateAsync(PluginExecutionHistory execution, CancellationToken cancellationToken = default);
}
