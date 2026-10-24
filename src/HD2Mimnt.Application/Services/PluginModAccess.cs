using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Implementation of IPluginModAccess that bridges plugin SDK to core repositories
/// with permission enforcement
/// </summary>
public class PluginModAccess : IPluginModAccess
{
    private readonly IModRepository _modRepository;
    private readonly IPluginService _pluginService;
    private readonly string _pluginId;

    public PluginModAccess(
        IModRepository modRepository,
        IPluginService pluginService,
        string pluginId)
    {
        _modRepository = modRepository;
        _pluginService = pluginService;
        _pluginId = pluginId;
    }

    public async Task<PluginModInfo?> GetModAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        // Check permission
        if (!await _pluginService.CheckPermissionAsync(_pluginId, "mods.read", cancellationToken))
        {
            throw new UnauthorizedAccessException($"Plugin '{_pluginId}' does not have 'mods.read' permission");
        }

        var mod = await _modRepository.GetByIdAsync(modId, cancellationToken);
        if (mod == null)
        {
            return null;
        }

        return MapToPluginModInfo(mod);
    }

    public async Task<List<PluginModInfo>> GetAllModsAsync(CancellationToken cancellationToken = default)
    {
        // Check permission
        if (!await _pluginService.CheckPermissionAsync(_pluginId, "mods.read", cancellationToken))
        {
            throw new UnauthorizedAccessException($"Plugin '{_pluginId}' does not have 'mods.read' permission");
        }

        var mods = await _modRepository.GetAllAsync(cancellationToken);
        return mods.Select(MapToPluginModInfo).ToList();
    }

    public async Task<List<PluginModInfo>> GetModsByCategoryAsync(string category, CancellationToken cancellationToken = default)
    {
        // Check permission
        if (!await _pluginService.CheckPermissionAsync(_pluginId, "mods.read", cancellationToken))
        {
            throw new UnauthorizedAccessException($"Plugin '{_pluginId}' does not have 'mods.read' permission");
        }

        // Parse category string to enum
        if (!Enum.TryParse<Core.Enums.ModCategory>(category, true, out var categoryEnum))
        {
            return new List<PluginModInfo>();
        }

        var mods = await _modRepository.GetByCategoryAsync(categoryEnum, cancellationToken);
        return mods.Select(MapToPluginModInfo).ToList();
    }

    public async Task<List<PluginModInfo>> GetModsByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        // Check permission
        if (!await _pluginService.CheckPermissionAsync(_pluginId, "mods.read", cancellationToken))
        {
            throw new UnauthorizedAccessException($"Plugin '{_pluginId}' does not have 'mods.read' permission");
        }

        var mods = await _modRepository.GetByTagAsync(tag, cancellationToken);
        return mods.Select(MapToPluginModInfo).ToList();
    }

    /// <summary>
    /// Map Core.Entities.Mod to PluginSDK.PluginModInfo (DTO)
    /// This prevents plugins from accessing internal implementation details
    /// </summary>
    private static PluginModInfo MapToPluginModInfo(Core.Entities.Mod mod)
    {
        return new PluginModInfo
        {
            Id = mod.Id,
            Name = mod.Name,
            Description = mod.Description,
            Version = mod.Version,
            Author = mod.Author,
            Category = mod.Category.ToString(),
            Tags = mod.Tags ?? new List<string>(),
            SourcePath = mod.Path,
            FileCount = 0,  // This would need to be calculated separately if needed
            TotalSize = mod.Size,
            ImportedAt = mod.ImportedAt,
            Metadata = new Dictionary<string, string>()
        };
    }
}
