using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Service for checking mod updates from platforms.
/// </summary>
public class UpdateCheckerService
{
    private readonly IPlatformRegistry _platformRegistry;
    private readonly IModRepository _modRepository;

    public UpdateCheckerService(IPlatformRegistry platformRegistry, IModRepository modRepository)
    {
        _platformRegistry = platformRegistry ?? throw new ArgumentNullException(nameof(platformRegistry));
        _modRepository = modRepository ?? throw new ArgumentNullException(nameof(modRepository));
    }

    /// <summary>
    /// Checks for updates for all mods with platform information.
    /// </summary>
    public async Task<List<ModUpdateInfo>> CheckAllUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var allMods = await _modRepository.GetAllAsync(cancellationToken);
        var modsWithPlatform = allMods.Where(m => !string.IsNullOrEmpty(m.PlatformId) && !string.IsNullOrEmpty(m.PlatformModId)).ToList();

        var updates = new List<ModUpdateInfo>();

        foreach (var mod in modsWithPlatform)
        {
            try
            {
                var updateInfo = await CheckUpdateAsync(mod.Id, cancellationToken);
                if (updateInfo != null && updateInfo.HasUpdate)
                {
                    updates.Add(updateInfo);
                }
            }
            catch
            {
                // Skip mods that fail update check
                continue;
            }
        }

        return updates;
    }

    /// <summary>
    /// Checks for updates for a specific mod.
    /// </summary>
    public async Task<ModUpdateInfo?> CheckUpdateAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(modId, cancellationToken);
        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {modId}");
        }

        if (string.IsNullOrEmpty(mod.PlatformId) || string.IsNullOrEmpty(mod.PlatformModId))
        {
            return null; // No platform information
        }

        var platform = _platformRegistry.GetPlatform(mod.PlatformId);
        if (platform == null)
        {
            return null; // Platform not available
        }

        try
        {
            // Get latest mod info from platform
            var platformModInfo = await platform.GetModInfoAsync(mod.PlatformModId, cancellationToken);

            var updateInfo = new ModUpdateInfo
            {
                ModId = mod.Id,
                ModName = mod.Name,
                CurrentVersion = mod.Version,
                LatestVersion = platformModInfo.Version,
                PlatformId = mod.PlatformId,
                PlatformModId = mod.PlatformModId,
                UpdatedAt = platformModInfo.UpdatedAt,
                DownloadUrl = platformModInfo.DownloadUrl
            };

            // Simple version comparison - if versions differ, assume update available
            updateInfo.HasUpdate = !string.Equals(mod.Version, platformModInfo.Version, StringComparison.OrdinalIgnoreCase);

            // Update last check time
            mod.LastUpdateCheck = DateTime.UtcNow;
            await _modRepository.UpdateAsync(mod, cancellationToken);

            return updateInfo;
        }
        catch
        {
            // Update check failed
            return null;
        }
    }

    /// <summary>
    /// Gets mods that haven't been checked for updates recently.
    /// </summary>
    public async Task<List<Core.Entities.Mod>> GetModsNeedingUpdateCheckAsync(TimeSpan checkInterval, CancellationToken cancellationToken = default)
    {
        var allMods = await _modRepository.GetAllAsync(cancellationToken);
        var threshold = DateTime.UtcNow - checkInterval;

        return allMods
            .Where(m => !string.IsNullOrEmpty(m.PlatformId) &&
                       !string.IsNullOrEmpty(m.PlatformModId) &&
                       (!m.LastUpdateCheck.HasValue || m.LastUpdateCheck.Value < threshold))
            .ToList();
    }

    /// <summary>
    /// Gets the changelog for a mod update.
    /// </summary>
    public async Task<string?> GetChangelogAsync(Guid modId, string version, CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(modId, cancellationToken);
        if (mod == null || string.IsNullOrEmpty(mod.PlatformId) || string.IsNullOrEmpty(mod.PlatformModId))
        {
            return null;
        }

        var platform = _platformRegistry.GetPlatform(mod.PlatformId);
        if (platform == null)
        {
            return null;
        }

        try
        {
            var versions = await platform.GetVersionsAsync(mod.PlatformModId, cancellationToken);
            var versionInfo = versions.FirstOrDefault(v => v.Version == version);
            return versionInfo?.ChangeLog;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Represents mod update information.
/// </summary>
public class ModUpdateInfo
{
    public Guid ModId { get; set; }
    public string ModName { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public bool HasUpdate { get; set; }
    public string PlatformId { get; set; } = string.Empty;
    public string PlatformModId { get; set; } = string.Empty;
    public DateTime? UpdatedAt { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
}
