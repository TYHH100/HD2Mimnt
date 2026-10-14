using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

public interface INexusModsOAuthClient
{
    Task<NexusModsOAuthTokens> LoginAsync(NexusModsOAuthOptions options, Action<Uri> openBrowser,
        CancellationToken cancellationToken = default);
    Task<NexusModsOAuthTokens> RefreshAsync(NexusModsOAuthOptions options, NexusModsOAuthTokens tokens,
        CancellationToken cancellationToken = default);
}
