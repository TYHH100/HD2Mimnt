using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Interface for Mod platform adapters.
/// </summary>
public interface IModPlatform
{
    /// <summary>
    /// Gets the platform name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the unique platform identifier.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the capabilities supported by this platform.
    /// </summary>
    PlatformCapabilities Capabilities { get; }

    /// <summary>
    /// Checks if the platform is currently available.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for Mods on the platform.
    /// </summary>
    Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed information about a specific Mod.
    /// </summary>
    Task<PlatformModInfo> GetModInfoAsync(string modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a Mod from the platform.
    /// </summary>
    Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available versions of a Mod.
    /// </summary>
    Task<List<PlatformModVersion>> GetVersionsAsync(string modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all files associated with a Mod.
    /// </summary>
    Task<List<PlatformModFile>> GetFilesAsync(string modId, string? version = null, CancellationToken cancellationToken = default);
}
