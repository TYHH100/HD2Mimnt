using HD2Mimnt.Core;

namespace HD2Mimnt.Infrastructure.Updates;

/// <summary>GitHub Releases 主渠道。</summary>
public sealed class GitHubAppUpdateClient(HttpClient http, TimeProvider? time = null)
    : ReleaseFeedAppUpdateClient(http, AppUpdateChannels.GitHub,
        "https://api.github.com/repos/TYHH100/HD2Mimnt/releases/latest",
        "application/vnd.github+json", time);