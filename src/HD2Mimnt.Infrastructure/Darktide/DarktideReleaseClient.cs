using System.Net;
using System.Security.Cryptography;
using System.IO.Compression;
using HD2Mimnt.Core.Darktide;

namespace HD2Mimnt.Infrastructure.Darktide;

public sealed class DarktideReleaseClient
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/Darktide-Mod-Framework/Darktide-Mod-Loader/releases/latest";
    private const long MaxDownloadBytes = 200L * 1024 * 1024;

    private static readonly string[] AllowedDownloadHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };

    private readonly HttpClient _httpClient;

    public DarktideReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarktideMimnt/0.1");
    }

    public async Task<DarktideReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new DarktideReleaseInfo("", "", null, $"{DarktideReleaseParser.RepositoryPage}/releases", "",
                    Array.Empty<DarktideReleasePackage>(),
                    $"GitHub Release 查询失败：HTTP {(int)response.StatusCode}。");
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return DarktideReleaseParser.Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (ex is TaskCanceledException && cancellationToken.IsCancellationRequested) throw;
            return new DarktideReleaseInfo("", "", null, $"{DarktideReleaseParser.RepositoryPage}/releases", "",
                Array.Empty<DarktideReleasePackage>(), $"无法访问 GitHub Release：{ex.Message}");
        }
    }

    public async Task<DarktideDownloadedPackage> DownloadPackageAsync(DarktideReleasePackage package,
        string destinationDirectory, IProgress<DarktideDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!DarktideReleaseParser.IsOfficialDownloadUrl(package.DownloadUrl))
            throw new InvalidOperationException("下载链接不是 DML 官方 GitHub 地址。");
        Directory.CreateDirectory(destinationDirectory);
        var temporary = Path.Combine(destinationDirectory, $"dml-{Guid.NewGuid():N}.zip.tmp");
        try
        {
            using var response = await _httpClient.GetAsync(package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"下载失败：HTTP {(int)response.StatusCode}。");
            EnsureFinalHostIsOfficial(response.RequestMessage?.RequestUri);
            var total = response.Content.Headers.ContentLength;
            if (total is > MaxDownloadBytes)
                throw new InvalidDataException("安装包体积超过 200MB 限制。");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long received = 0;
            var lastReport = 0L;
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, bufferSize: 81920, useAsync: true))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    received += read;
                    if (received > MaxDownloadBytes) throw new InvalidDataException("安装包体积超过 200MB 限制。");
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    var now = Environment.TickCount64;
                    if (progress != null && now - lastReport >= 100)
                    {
                        progress.Report(new DarktideDownloadProgress(received, total));
                        lastReport = now;
                    }
                }
                await destination.FlushAsync(cancellationToken);
            }
            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            ValidateZip(temporary);
            var safeName = SanitizeFileName(package.FileName);
            var finalPath = Path.Combine(destinationDirectory, $"{sha256[..12]}-{safeName}");
            File.Move(temporary, finalPath, overwrite: true);
            progress?.Report(new DarktideDownloadProgress(received, total ?? received));
            return new DarktideDownloadedPackage(package, finalPath, sha256, received);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void ValidateZip(string path)
    {
        var header = new byte[4];
        try
        {
            using var stream = File.OpenRead(path);
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("下载内容不是有效的 ZIP 文件。");
        }
        if (header[0] != 'P' || header[1] != 'K') throw new InvalidDataException("下载内容不是有效的 ZIP 文件。");
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count == 0) throw new InvalidDataException("下载的 ZIP 为空。");
    }

    private static void EnsureFinalHostIsOfficial(Uri? uri)
    {
        if (uri == null) throw new InvalidDataException("下载响应缺少最终地址。");
        if (!AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"下载重定向到非官方主机：{uri.Host}");
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = "Darktide-Mod-Loader.zip";
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name : name + ".zip";
    }
}
