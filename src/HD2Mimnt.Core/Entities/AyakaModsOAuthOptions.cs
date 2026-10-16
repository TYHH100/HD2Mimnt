namespace HD2Mimnt.Core.Entities;

/// <summary>Public client registration. Ayaka allows a dynamic port on numeric loopback hosts.</summary>
public sealed record AyakaModsOAuthOptions
{
    public const string DefaultRedirectUri = "http://127.0.0.1/oauth/ayaka/callback";
    public string ClientId { get; init; } = "";
    public string RedirectUri { get; init; } = DefaultRedirectUri;
    public Uri Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Any(char.IsControl))
            throw new InvalidOperationException("尚未配置 A 网 OAuth Client ID，请向管理员申请 Public Client 后配置。");
        if (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Host != "127.0.0.1" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("A 网 OAuth 回调必须使用 http://127.0.0.1/ 本机地址。");
        return uri;
    }
}

public sealed record AyakaModsOAuthTokens
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    public string Scope { get; init; } = "";
    public override string ToString() => "AyakaModsOAuthTokens { credentials redacted }";
}

public sealed class AyakaModsOAuthException(string message, bool requiresLogin = false) : Exception(message)
{
    public bool RequiresLogin { get; } = requiresLogin;
}
