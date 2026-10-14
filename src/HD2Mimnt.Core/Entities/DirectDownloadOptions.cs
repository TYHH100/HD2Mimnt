namespace HD2Mimnt.Core.Entities;

public enum DownloadProxyMode { System, Direct, Custom }

/// <summary>Proxy credentials are session-only and must not be persisted with settings.</summary>
public sealed record DownloadProxyOptions(DownloadProxyMode Mode = DownloadProxyMode.System,
    string Address = "", string UserName = "", string Password = "")
{
    public Uri? Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("代理模式无效。");
        if (Mode != DownloadProxyMode.Custom) return null;
        if (!Uri.TryCreate(Address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks5") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath is not ("" or "/"))
            throw new ArgumentException("请输入有效的 HTTP、HTTPS 或 SOCKS5 代理地址；账号和密码请填写在单独的输入框中。");
        return uri;
    }
}

public sealed record DirectDownloadInfo(string FileName, long? Length);
