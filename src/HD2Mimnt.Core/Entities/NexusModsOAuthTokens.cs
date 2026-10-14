namespace HD2Mimnt.Core.Entities;

/// <summary>敏感数据，仅允许写入加密凭据存储。</summary>
public sealed class NexusModsOAuthTokens
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string UserName { get; init; }
    public required string UserId { get; init; }
    public override string ToString() => "Nexus Mods OAuth tokens [redacted]";
}

public sealed class NexusModsOAuthException(string message, bool requiresLogin = false) : Exception(message)
{
    public bool RequiresLogin { get; } = requiresLogin;
}
