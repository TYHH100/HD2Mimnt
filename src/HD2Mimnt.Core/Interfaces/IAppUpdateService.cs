using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>GitHub Release 上的一个应用版本及其安装资产信息。</summary>
public sealed record AppUpdateInfo(
    string Version,
    string ChangelogBody,
    string ReleaseUrl,
    string? AssetUrl,
    string? AssetName,
    DateTimeOffset? PublishedAt,
    /// <summary>安装包的 GitHub 资产摘要（"sha256:&lt;hex&gt;"）；缺失时下载不做完整性校验。</summary>
    string? AssetDigest = null);

/// <summary>一次应用更新检查的结果；<see cref="HasUpdate"/> 为 true 时 <see cref="Info"/> 非空。</summary>
public sealed record AppUpdateCheckResult(bool HasUpdate, string CurrentVersion, AppUpdateInfo? Info);

/// <summary>已下载解包完成、等待重启安装的一个更新包。</summary>
public sealed record AppUpdateStaging(
    string Version,
    string TargetDirectory,
    string StagingDirectory,
    string InstallerScriptPath);

/// <summary>
/// 读取应用自身最新发布版本（GitHub Release）的客户端；实现位于 Infrastructure，
/// 接口放 Core 供 Application 服务编排。
/// </summary>
public interface IAppUpdateReleaseClient
{
    Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);
}

/// <summary>GitHub Releases API 的最小响应模型。</summary>
public sealed record AppUpdateRelease(
    string TagName,
    string Body,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<AppUpdateReleaseAsset> Assets);

/// <summary>
/// Release 附带的一个可下载资产。<see cref="Digest"/> 是 GitHub 服务端计算的资产摘要
/// （"sha256:&lt;hex&gt;"，2024-04 起提供），与安装包 CDN 分属不同主机，可作为下载通道的
/// 完整性锚点；旧版本资产可能没有该字段。
/// </summary>
public sealed record AppUpdateReleaseAsset(string Name, string DownloadUrl, string? Digest = null);

/// <summary>
/// 应用自更新：检查新版本、下载解包到暂存区并生成安装脚本；
/// 安装由外部脚本在应用退出后覆盖安装目录并重启，UI 层负责触发应用退出。
/// </summary>
public interface IAppUpdateService
{
    /// <summary>查询最新发布版本并与当前版本比较。网络失败或解析失败抛异常。</summary>
    Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载指定更新的安装包、解压到暂存目录并生成安装脚本。
    /// 安装目录不可写或下载解包失败时抛异常；成功后旧的暂存目录会被清理。
    /// </summary>
    Task<AppUpdateStaging> PrepareAsync(AppUpdateInfo update, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>启动安装脚本（独立进程，等待本应用退出后覆盖文件并重启），随后调用方应退出应用。</summary>
    void LaunchPendingInstaller(AppUpdateStaging staging);
}
