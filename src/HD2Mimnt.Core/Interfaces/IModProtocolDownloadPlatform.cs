using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>网页协议链接授权的文件列表与下载；授权只随本次操作传递。</summary>
public interface IModProtocolDownloadPlatform
{
    string GetWebsiteDownloadUrl(string modId, PlatformModFile file);
    ModProtocolTarget ReadProtocolTarget(string protocolLink);
    Task<List<PlatformModFile>> GetProtocolFilesAsync(string modId, CancellationToken cancellationToken = default);
    Task<PlatformModFile> ResolveProtocolFileAsync(string modId, string protocolLink, CancellationToken cancellationToken = default);
    Task<Stream> DownloadProtocolFileAsync(string modId, string fileId, string protocolLink,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
