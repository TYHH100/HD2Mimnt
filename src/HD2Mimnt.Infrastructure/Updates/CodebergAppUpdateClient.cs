using HD2Mimnt.Core;

namespace HD2Mimnt.Infrastructure.Updates;

/// <summary>
/// Codeberg（Forgejo）发布镜像渠道，由 mirror-release 工作流从 GitHub Release 单向同步。
/// 镜像资产没有 GitHub 服务端计算的 digest，下载时跳过摘要校验。
/// </summary>
public sealed class CodebergAppUpdateClient(HttpClient http, TimeProvider? time = null)
    : ReleaseFeedAppUpdateClient(http, AppUpdateChannels.Codeberg,
        "https://codeberg.org/api/v1/repos/TYHH10/HD2Mimnt/releases/latest", time: time);