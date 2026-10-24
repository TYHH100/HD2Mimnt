using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

public sealed record BrowsedModDetails(PlatformModInfo Mod, IReadOnlyList<PlatformModFile> Files);

/// <summary>浏览与首次下载共用已注册的平台和下载管理器，不依赖本地已安装模组。</summary>
public sealed class ModBrowseService(IPlatformRegistry platforms, DownloadManagerService downloads, HttpClient? images = null,
    string? tempDirectory = null)
{
    public string PlatformId { get; private set; } = "nexusmods";
    public void SelectPlatform(string id)
    {
        if (id is not ("nexusmods" or "ayakamods") || platforms.GetPlatform(id) is not IModBrowsePlatform)
            throw new ArgumentException("此平台不支持模组浏览。");
        PlatformId = id;
    }
    private IModPlatform Platform => platforms.GetPlatform(PlatformId)
        ?? throw new InvalidOperationException("模组平台未注册。");
    public string HomePageUrl => PlatformId == "ayakamods" ? "https://ayakamods.com/games/helldivers-2.119/" : "https://www.nexusmods.com/helldivers2/mods/";
    private IModBrowsePlatform Browser => Platform as IModBrowsePlatform
        ?? throw new NotSupportedException("此平台不支持模组浏览。");
    private IModProtocolDownloadPlatform Protocol => Platform as IModProtocolDownloadPlatform
        ?? throw new NotSupportedException("此平台不支持网页授权下载。");

    public Task<PlatformSearchResult> BrowseAsync(ModBrowseFeed feed, CancellationToken token = default)
        => Browser.BrowseAsync(feed, token);

    public Task<PlatformSearchResult> BrowsePageAsync(ModBrowseFeed feed, int page, CancellationToken token)
        => Platform is HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsPlatformAdapter ayaka
            ? ayaka.BrowsePageAsync(feed, page, token) : BrowseAsync(feed, token);

    public Task<PlatformSearchResult> SearchAsync(string query, int page, CancellationToken token)
        => Browser.SearchBrowseAsync(new() { Query = query, Page = page, PageSize = 20 }, token);

    public async Task<BrowsedModDetails> GetDetailsAsync(string modId, CancellationToken token = default)
    {
        var mod = await Browser.GetBrowseDetailsAsync(modId, token);
        var files = Platform is IModProtocolDownloadPlatform protocol
            ? await protocol.GetProtocolFilesAsync(modId, token) : await Platform.GetFilesAsync(modId, cancellationToken: token);
        return new(mod, files);
    }

    public string GetModPageUrl(string modId)
    {
        if (!long.TryParse(modId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("模组编号无效。");
        return PlatformId == "ayakamods" ? $"https://ayakamods.com/mods/{id}/" : $"https://www.nexusmods.com/helldivers2/mods/{id}";
    }

    public string ParseModId(string input)
    {
        input = input.Trim();
        if (long.TryParse(input, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return PlatformId == "ayakamods"
            ? HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsLinkParser.Parse(input).ModId
            : HD2Mimnt.Infrastructure.Platforms.NexusMods.NexusModsLinkParser.Parse(input).ModId;
    }

    public ModPlatformLink CreateSourceLink(PlatformModInfo mod, PlatformModFile file)
        => new(PlatformId, "helldivers2", mod.Id, GetModPageUrl(mod.Id),
            Name: mod.Name, GlobalModId: mod.Metadata.GetValueOrDefault("nexus_global_mod_id")?.ToString(),
            InstalledFileVersionId: PlatformId == "nexusmods" && file.Id.StartsWith("nexus-v1-file:", StringComparison.Ordinal) ? null : file.Id,
            InstalledWebsiteFileId: file.WebsiteFileId,
            InstalledVersion: string.IsNullOrWhiteSpace(file.Version) ? null : file.Version,
            Author: string.IsNullOrWhiteSpace(mod.Author) ? null : mod.Author, AuthorUrl: mod.AuthorUrl);

    public async Task<byte[]?> GetPreviewAsync(string url, CancellationToken token)
    {
        if (images == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        using var response = await images.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 4194304) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var data = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (data.Length + read > 4194304) return null;
            data.Write(buffer, 0, read);
        }
        return data.ToArray();
    }

    public string GetWebsiteDownloadUrl(string modId, PlatformModFile file) => PlatformId == "ayakamods"
        ? GetModPageUrl(modId).TrimEnd('/') + "/download" : Protocol.GetWebsiteDownloadUrl(modId, file);

    public Task<bool> HasDirectDownloadAccessAsync(CancellationToken token)
        => Platform is IModDownloadAccessPlatform access ? access.HasDirectDownloadAccessAsync(token)
            : Task.FromResult(false);

    /// <summary>网页直接打开 NXM 时，依据链接解析所授权的文件，无需预先选择模组。</summary>
    public async Task<(PlatformModInfo Mod, PlatformModFile File)> ResolveProtocolDownloadAsync(
        string input, CancellationToken token = default)
    {
        var platform = platforms.GetPlatform("nexusmods");
        if (platform is not IModProtocolDownloadPlatform protocol || platform is not IModBrowsePlatform browser)
            throw new NotSupportedException("此平台不支持网页授权下载。");
        var target = protocol.ReadProtocolTarget(input);
        var file = await protocol.ResolveProtocolFileAsync(target.ModId, input, token);
        var mod = await browser.GetBrowseDetailsAsync(target.ModId, token);
        return (mod, file);
    }

    /// <summary>下载到独立临时目录，交给现有导入流程；导入返回后才清理源文件。</summary>
    public async Task DownloadAndImportAsync(string modId, PlatformModFile file, string? input,
        Func<string, CancellationToken, Task> importArchive, IProgress<DownloadProgress>? progress = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var directory = Path.Combine(tempDirectory ?? Path.GetTempPath(), $"hd2mimnt_browse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var name = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                name = "mod-download.zip";
            var archive = Path.Combine(directory, name);
            await DownloadAsync(modId, file, input, archive, progress, token);
            token.ThrowIfCancellationRequested();
            await importArchive(archive, token);
        }
        finally { Directory.Delete(directory, true); }
    }

    public void ValidateProtocolTarget(string modId, PlatformModFile file, string input)
    {
        var protocol = platforms.GetPlatform("nexusmods") as IModProtocolDownloadPlatform
            ?? throw new NotSupportedException("此平台不支持网页授权下载。");
        var target = protocol.ReadProtocolTarget(input);
        if (target.PlatformId != "nexusmods" || target.GameDomain != "helldivers2" || target.ModId != modId || target.WebsiteFileId != file.WebsiteFileId)
            throw new ArgumentException("网页授权与所选模组或文件不一致，请从所选文件网页重新获取授权。");
    }

    public async Task DownloadAsync(string modId, PlatformModFile file, string? input, string destination,
        IProgress<DownloadProgress>? progress = null, CancellationToken token = default)
    {
        // N 网的两种方式使用相同网站文件编号；A 网按不可变版本/附件身份下载。
        if (Platform is IModProtocolDownloadPlatform protocol) _ = protocol.GetWebsiteDownloadUrl(modId, file);
        if (input != null)
        {
            ValidateProtocolTarget(modId, file, input);
            var resolved = await Protocol.ResolveProtocolFileAsync(modId, input, token);
            if (resolved.Id != file.Id) throw new InvalidOperationException("网页授权文件与所选文件不一致。");
        }
        token.ThrowIfCancellationRequested();
        var taskId = downloads.EnqueueDownload(PlatformId, modId, destination, file.Id, progress, input, file.Name);
        using var registration = token.Register(() => downloads.CancelDownload(taskId));
        await downloads.WaitForDownloadAsync(taskId);
        var task = downloads.GetDownloadTask(taskId)!;
        if (task.Status == DownloadStatus.Cancelled) throw new OperationCanceledException(token);
        if (task.Failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(task.Failure).Throw();
        if (task.Status != DownloadStatus.Completed) throw new InvalidOperationException("下载失败：" + task.ErrorMessage);
    }
}
