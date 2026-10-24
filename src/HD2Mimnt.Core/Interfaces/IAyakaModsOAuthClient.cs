using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

public interface IAyakaModsOAuthClient
{
    Task<AyakaModsOAuthTokens> LoginAsync(AyakaModsOAuthOptions options, Action<Uri> openBrowser,
        CancellationToken cancellationToken = default);
    Task<AyakaModsOAuthTokens> RefreshAsync(AyakaModsOAuthOptions options, AyakaModsOAuthTokens tokens,
        CancellationToken cancellationToken = default);
}
