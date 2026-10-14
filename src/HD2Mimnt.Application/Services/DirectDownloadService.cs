using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>Direct links use the same segmented/resumable engine as platform downloads.</summary>
public sealed class DirectDownloadService(IDirectDownloadClientFactory clients, IDownloadOptionsProvider optionsProvider,
    SettingsService settings, string tempDirectory)
{
    public DownloadProxyOptions GetProxy() => new(
        settings.GetSetting("DirectDownload.ProxyMode", DownloadProxyMode.System),
        settings.GetSetting("DirectDownload.ProxyAddress", ""));

    public string GetDestination() => settings.GetSetting("DirectDownload.Destination", "");

    public Task SavePreferencesAsync(DownloadProxyOptions proxy, string destination)
    {
        proxy.Validate();
        return settings.SetSettingsAsync(new Dictionary<string, object>
        {
            ["DirectDownload.ProxyMode"] = proxy.Mode,
            ["DirectDownload.ProxyAddress"] = proxy.Mode == DownloadProxyMode.Custom ? proxy.Address.Trim() : "",
            ["DirectDownload.Destination"] = destination.Trim()
        });
    }

    public static Uri ParseLink(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 下载链接。");
        return uri;
    }

    public async Task<string> DownloadAsync(string link, string fileName, string? destination, DownloadProxyOptions proxy,
        Func<string, CancellationToken, Task>? importArchive = null, IProgress<DownloadProgress>? progress = null,
        Action<DirectDownloadInfo>? resolved = null, CancellationToken token = default)
    {
        var uri = ParseLink(link);
        proxy.Validate();
        if (importArchive == null && string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("请选择下载文件夹。");
        var options = await optionsProvider.GetOptionsAsync();
        using var client = clients.Create(proxy);
        var info = await client.GetInfoAsync(uri, token);
        var name = SanitizeFileName(string.IsNullOrWhiteSpace(fileName) ? info.FileName : fileName);
        resolved?.Invoke(info with { FileName = name });
        token.ThrowIfCancellationRequested();
        if (importArchive == null)
        {
            var target = GetAvailablePath(Path.GetFullPath(destination!), name);
            await client.DownloadToFileAsync(uri, target, new DownloadOptions
            {
                MaxParallelSegments = options.MaxParallelSegments, MinimumSegmentSize = options.MinimumSegmentSize,
                MaxRetries = options.MaxRetries, RetryDelay = options.RetryDelay, EnableResume = options.EnableResume,
                OverwriteExisting = false
            }, progress, token);
            return target;
        }

        // The cache belongs to the download engine. Import sources are independent and always cleaned up.
        var directory = Path.Combine(tempDirectory, $"direct-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var archive = Path.Combine(directory, name);
            await using (var source = await client.DownloadAsync(uri, options, progress, token))
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
                await source.CopyToAsync(output, token);
            token.ThrowIfCancellationRequested();
            await importArchive(archive, token);
            return name;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>The browser owns this file. Import it without deleting or moving the user's source.</summary>
    public Task ImportBrowserFileAsync(string path, Func<string, CancellationToken, Task> importArchive, CancellationToken token)
    {
        var source = Path.GetFullPath(path);
        if (!File.Exists(source)) throw new FileNotFoundException("未找到浏览器下载的文件。");
        token.ThrowIfCancellationRequested();
        return importArchive(source, token);
    }

    public static string SanitizeFileName(string name)
    {
        name = name.Trim().Trim('"').Replace('\\', '/').Split('/').Last();
        name = new string(name.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) return "download.zip";
        var stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            name = "_" + name;
        if (name.Length > 180)
        {
            var extension = Path.GetExtension(name);
            name = extension.Length < 30 ? name[..(180 - extension.Length)] + extension : name[..180];
        }
        return name;
    }

    private static string GetAvailablePath(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 1; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(directory, $"{stem} ({i}){extension}");
        return path;
    }
}
