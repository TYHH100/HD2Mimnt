using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Interface for managing platform registrations.
/// </summary>
public interface IPlatformRegistry
{
    /// <summary>
    /// Registers a platform adapter.
    /// </summary>
    void RegisterPlatform(IModPlatform platform);

    /// <summary>
    /// Gets a platform by its ID.
    /// </summary>
    IModPlatform? GetPlatform(string platformId);

    /// <summary>
    /// Gets all registered platforms.
    /// </summary>
    List<IModPlatform> GetAllPlatforms();

    /// <summary>
    /// Gets all platforms that are currently available.
    /// </summary>
    Task<List<IModPlatform>> GetAvailablePlatformsAsync(CancellationToken cancellationToken = default);
}
