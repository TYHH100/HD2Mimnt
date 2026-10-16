using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

public sealed record ModOnlineInfo(Mod Mod, IReadOnlyList<PlatformModFile> Files);

/// <summary>使用已保存的链接检查文件、下载指定版本并替换原模组。</summary>
public sealed class ModOnlineService(IModRepository repository, IPlatformRegistry platforms,
    ModService mods, DownloadManagerService downloads, ILogger<ModOnlineService>? logger = null,
    StorageSettingsService? storage = null)
{
    public string GetWebsiteDownloadUrl(ModPlatformLink link, PlatformModFile file)
        => link.PlatformId == "ayakamods" ? $"https://ayakamods.com/mods/{Uri.EscapeDataString(link.ModId)}/download"
            : GetProtocolPlatform(GetPlatform(link)).GetWebsiteDownloadUrl(link.ModId, file);

    public void ValidateProtocolTarget(ModPlatformLink link, PlatformModFile file, string protocolLink)
    {
        var target = GetProtocolPlatform(GetPlatform(link)).ReadProtocolTarget(protocolLink);
        if (target.PlatformId != link.PlatformId || target.GameDomain != link.GameDomain || target.ModId != link.ModId)
            throw new ArgumentException("临时链接属于另一个模组，请在已打开的文件网页重新获取授权。");
        if (target.WebsiteFileId != file.WebsiteFileId)
            throw new ArgumentException("临时链接属于另一个文件，请在已打开的所选文件网页重新获取授权。");
    }

    public async Task<ModOnlineInfo> CheckAsync(Guid id, CancellationToken cancellationToken = default, bool useWebsiteAuthorization = false)
    {
        var mod = await GetBoundModAsync(id, cancellationToken);
        var link = mod.PlatformLink!;
        var platform = GetPlatform(link);
        var files = useWebsiteAuthorization
            ? await GetProtocolPlatform(platform).GetProtocolFilesAsync(link.ModId, cancellationToken)
            : await platform.GetFilesAsync(link.ModId, cancellationToken: cancellationToken);
        mod = await mods.RecordUpdateCheckAsync(id, link,
            string.IsNullOrWhiteSpace(files.FirstOrDefault()?.Version) ? null : files[0].Version, cancellationToken);
        return new(mod, files);
    }

    public async Task<PlatformModFile> ResolveProtocolFileAsync(Guid id, ModPlatformLink expectedLink, string protocolLink,
        CancellationToken cancellationToken = default)
    {
        var mod = await GetBoundModAsync(id, cancellationToken);
        if (mod.PlatformId != expectedLink.PlatformId || mod.PlatformModId != expectedLink.ModId)
            throw new InvalidOperationException("模组链接已变更，请重新检查更新。");
        return await GetProtocolPlatform(GetPlatform(expectedLink)).ResolveProtocolFileAsync(expectedLink.ModId, protocolLink, cancellationToken);
    }

    private static IModProtocolDownloadPlatform GetProtocolPlatform(IModPlatform platform)
        => platform as IModProtocolDownloadPlatform ?? throw new NotSupportedException("此平台尚不支持网页授权下载。");

    public async Task DownloadAsync(Guid id, ModPlatformLink expectedLink, PlatformModFile file, string destination,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, string? protocolLink = null)
    {
        var mod = await GetBoundModAsync(id, cancellationToken);
        if (mod.PlatformId != expectedLink.PlatformId || mod.PlatformModId != expectedLink.ModId)
            throw new InvalidOperationException("模组链接已变更，请重新检查更新。");
        GetPlatform(expectedLink);
        cancellationToken.ThrowIfCancellationRequested();
        var taskId = downloads.EnqueueDownload(expectedLink.PlatformId, expectedLink.ModId, destination, file.Id, progress, protocolLink);
        // 取消后仍等待流和临时文件释放，再交还调用方。
        using var registration = cancellationToken.Register(() => downloads.CancelDownload(taskId));
        await downloads.WaitForDownloadAsync(taskId);
        var task = downloads.GetDownloadTask(taskId)!;
        if (task.Status == DownloadStatus.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (task.Failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(task.Failure).Throw();
        if (task.Status != DownloadStatus.Completed)
            throw new InvalidOperationException("下载失败：" + task.ErrorMessage);
    }

    public async Task<ModReplacementResult> UpdateAsync(Guid id, ModPlatformLink link, PlatformModFile file,
        IProgress<DownloadProgress>? downloadProgress = null, IProgress<ModImportProgress>? importProgress = null,
        CancellationToken cancellationToken = default, string? protocolLink = null)
    {
        if (string.IsNullOrWhiteSpace(file.Version))
            throw new InvalidOperationException("所选文件未提供版本号，无法更新；可以单独下载压缩包。");
        var directory = Path.Combine(storage?.Current.TempPath ?? Path.GetTempPath(), "HD2Mimnt-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var archive = Path.Combine(directory, "update.zip");
            await DownloadAsync(id, link, file, archive, downloadProgress, cancellationToken, protocolLink);
            return await Task.Run(() => mods.ReplaceFromArchiveAsync(id, archive, link, file,
                cancellationToken, importProgress, protocolLink == null ? null : file.WebsiteFileId), cancellationToken);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger?.LogWarning(ex, "更新临时目录清理失败: {Directory}", directory); }
        }
    }

    /// <summary>Replace from an explicitly selected browser download; the source belongs to the user.</summary>
    public async Task<ModReplacementResult> UpdateFromBrowserFileAsync(Guid id, ModPlatformLink link, PlatformModFile file,
        string path, IProgress<ModImportProgress>? progress = null, CancellationToken token = default)
    {
        if (link.PlatformId != "ayakamods") throw new NotSupportedException("此平台不使用浏览器文件接管。");
        if (string.IsNullOrWhiteSpace(file.Version)) throw new InvalidOperationException("所选文件未提供版本号，无法更新；可以单独下载压缩包。");
        var bound = await GetBoundModAsync(id, token);
        if (bound.PlatformId != link.PlatformId || bound.PlatformModId != link.ModId)
            throw new InvalidOperationException("模组链接已变更，请重新检查更新。");
        var visible = await GetPlatform(link).GetFilesAsync(link.ModId, cancellationToken: token);
        if (!visible.Any(candidate => candidate.Id == file.Id && candidate.Version == file.Version && candidate.Size == file.Size))
            throw new InvalidOperationException("所选 A 网文件已不可用，请重新检查。");
        ModReplacementResult? result = null;
        await BrowserDownloadHandoffService.UseFileAsync(path, file, async (source, cancellation) =>
            result = await Task.Run(() => mods.ReplaceFromArchiveAsync(id, source, link, file,
                cancellation, progress), cancellation), token);
        return result!;
    }

    private async Task<Mod> GetBoundModAsync(Guid id, CancellationToken cancellationToken)
    {
        var mod = await mods.GetModByIdAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("模组已删除。");
        if (mod.PlatformLink is not { SupportsUpdates: true } || mod.PlatformId != mod.PlatformLink.PlatformId || mod.PlatformModId != mod.PlatformLink.ModId)
            throw new InvalidOperationException("请先通过右键菜单保存模组链接。");
        return mod;
    }

    private IModPlatform GetPlatform(ModPlatformLink link)
    {
        var platform = platforms.GetPlatform(link.PlatformId)
            ?? throw new InvalidOperationException("模组平台未注册。");
        if (platform is NexusModsPlatformAdapter nexus && !nexus.IsAuthenticated)
            throw new InvalidOperationException("请先在“设置 → 账号”中登录 N 网，或在开发工具应用个人 API Key 测试。");
        return platform;
    }
}
