using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Platforms;

/// <summary>
/// Base class for platform adapters.
/// </summary>
public abstract class BasePlatformAdapter : IModPlatform
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Id { get; }

    /// <inheritdoc/>
    public abstract PlatformCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the HTTP client for making requests.
    /// </summary>
    protected HttpClient HttpClient { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="BasePlatformAdapter"/> class.
    /// </summary>
    protected BasePlatformAdapter(HttpClient httpClient)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc/>
    public virtual Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        // Default implementation - assume platform is available
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public abstract Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract Task<PlatformModInfo> GetModInfoAsync(string modId, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract Task<List<PlatformModVersion>> GetVersionsAsync(string modId, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public abstract Task<List<PlatformModFile>> GetFilesAsync(string modId, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a capability is supported.
    /// </summary>
    protected bool HasCapability(PlatformCapabilities capability)
    {
        return Capabilities.HasFlag(capability);
    }

    /// <summary>
    /// Throws an exception if the capability is not supported.
    /// </summary>
    protected void EnsureCapability(PlatformCapabilities capability)
    {
        if (!HasCapability(capability))
        {
            throw new NotSupportedException($"Platform '{Name}' does not support {capability}.");
        }
    }
}
