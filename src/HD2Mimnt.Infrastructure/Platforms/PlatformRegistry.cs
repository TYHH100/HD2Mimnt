using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Platforms;

/// <summary>
/// Registry for managing platform adapters.
/// </summary>
public class PlatformRegistry : IPlatformRegistry
{
    private readonly Dictionary<string, IModPlatform> _platforms = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public void RegisterPlatform(IModPlatform platform)
    {
        if (platform == null)
        {
            throw new ArgumentNullException(nameof(platform));
        }

        if (string.IsNullOrWhiteSpace(platform.Id))
        {
            throw new ArgumentException("Platform ID cannot be null or empty.", nameof(platform));
        }

        lock (_lock)
        {
            if (_platforms.ContainsKey(platform.Id))
            {
                throw new InvalidOperationException($"Platform with ID '{platform.Id}' is already registered.");
            }

            _platforms[platform.Id] = platform;
        }
    }

    /// <inheritdoc/>
    public IModPlatform? GetPlatform(string platformId)
    {
        if (string.IsNullOrWhiteSpace(platformId))
        {
            return null;
        }

        lock (_lock)
        {
            return _platforms.TryGetValue(platformId, out var platform) ? platform : null;
        }
    }

    /// <inheritdoc/>
    public List<IModPlatform> GetAllPlatforms()
    {
        lock (_lock)
        {
            return _platforms.Values.ToList();
        }
    }

    /// <inheritdoc/>
    public async Task<List<IModPlatform>> GetAvailablePlatformsAsync(CancellationToken cancellationToken = default)
    {
        var platforms = GetAllPlatforms();
        var availablePlatforms = new List<IModPlatform>();

        foreach (var platform in platforms)
        {
            try
            {
                if (await platform.IsAvailableAsync(cancellationToken))
                {
                    availablePlatforms.Add(platform);
                }
            }
            catch
            {
                // Platform not available, skip it
            }
        }

        return availablePlatforms;
    }
}
