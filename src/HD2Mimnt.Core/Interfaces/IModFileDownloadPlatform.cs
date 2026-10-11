using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>按用户选定的不可变文件版本下载，避免检查后默认版本变化。</summary>
public interface IModFileDownloadPlatform
{
    Task<Stream> DownloadFileAsync(string modId, string fileVersionId,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
