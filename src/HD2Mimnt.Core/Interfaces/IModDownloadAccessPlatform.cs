namespace HD2Mimnt.Core.Interfaces;

/// <summary>查询当前认证账户是否有无需网页临时授权的下载权限。</summary>
public interface IModDownloadAccessPlatform
{
    Task<bool> HasDirectDownloadAccessAsync(CancellationToken cancellationToken = default);
}
