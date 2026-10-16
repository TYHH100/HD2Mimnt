namespace HD2Mimnt.Core.Services;

public static class AuthorWebsite
{
    public static string? Normalize(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri : null;
}
