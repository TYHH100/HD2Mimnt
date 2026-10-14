namespace HD2Mimnt.Core.Entities;

/// <summary>公开应用配置，可以随开源代码发布；桌面应用不使用 Client Secret。</summary>
public sealed record NexusModsOAuthOptions
{
    public const string DefaultRedirectUri = "http://127.0.0.1:47821/oauth/nexus/callback/";
    public string ClientId { get; init; } = "";
    public string RedirectUri { get; init; } = DefaultRedirectUri;

    public Uri Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Any(char.IsControl))
            throw new InvalidOperationException("尚未配置 Nexus Mods OAuth Client ID，请在应用注册获批后配置。");
        if (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Host != "127.0.0.1" || uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith('/'))
            throw new InvalidOperationException("OAuth 回调必须是带端口、以 / 结尾的 http://127.0.0.1/ 本机地址。");
        return uri;
    }
}
