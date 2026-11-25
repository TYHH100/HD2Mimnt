using System.Text.Json;

namespace HD2Mimnt.Application.Services;

/// <summary>HellForge CLI <c>status</c> 命令的输出（camelCase JSON）。</summary>
public sealed record HellForgeStatus
{
    public string? Install { get; init; }
    public string? GameBuild { get; init; }
    public string? Hellforge { get; init; }
    public string? Engine { get; init; }
    public int? ModSdk { get; init; }
    public HellForgeReadiness Ready { get; init; } = new();
    public string? NetworkBlock { get; init; }
    public HellForgeLauncherSettings Settings { get; init; } = new();
    public HellForgeSessionState Session { get; init; } = new();
    public List<string> Languages { get; init; } = new();
}

public sealed record HellForgeReadiness
{
    public bool GameCopy { get; init; }
    public bool SessionRunner { get; init; }
    public bool LinuxNamespace { get; init; }
}

public sealed record HellForgeLauncherSettings
{
    public string? Profile { get; init; }
    public string? War { get; init; }
    public string? WarServer { get; init; }
    public bool LocalPlayFab { get; init; }
    public bool Multiplayer { get; init; }
    public bool EngineLog { get; init; }
    public bool CrashDump { get; init; }
    public string? GameOptions { get; init; }
    public bool Sandbox { get; init; }
    public bool SteamUdp { get; init; }
    public bool SteamBridge { get; init; }
    public bool PlayWithStrangers { get; init; }
    /// <summary>true=显式开启、false=显式关闭、null=未选择（沙箱会话默认开启）。</summary>
    public bool? PlayWithStrangersChosen { get; init; }
    public bool LiveChanges { get; init; }
    public int ForgeCreditChance { get; init; }
    public bool? DealSubfactions { get; init; }
    public bool? DealSeaf { get; init; }
    public int? WarSeed { get; init; }
    public bool? ShareMods { get; init; }
    public bool? ListPublic { get; init; }
    public bool? ShowEditedStats { get; init; }
    public bool? HostStats { get; init; }
    public string? File { get; init; }
}

public sealed record HellForgeSessionState
{
    public bool Running { get; init; }
    public string? LastRun { get; init; }
    public string? LastStatus { get; init; }
    public string? LastFailure { get; init; }
}

/// <summary>HellForge CLI <c>profiles</c> 命令的输出：玩家档案与可用模板。</summary>
public sealed record HellForgeProfiles
{
    public List<HellForgeProfileInfo> Profiles { get; init; } = new();
    public List<HellForgeTemplateInfo> Templates { get; init; } = new();
}

public sealed record HellForgeProfileInfo
{
    public string Name { get; init; } = string.Empty;
    public string? Template { get; init; }
    public string? Folder { get; init; }
    public bool Used { get; init; }
    public bool Selected { get; init; }
}

public sealed record HellForgeTemplateInfo
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int Items { get; init; }
}

/// <summary>HellForge CLI <c>mods</c> 命令的输出：Mods 页列表（顶部在前）、问题、modlist code 与设置文件路径。</summary>
public sealed record HellForgeModsList
{
    public List<HellForgeModInfo> Mods { get; init; } = new();
    public List<string> Problems { get; init; } = new();
    public string? Modlist { get; init; }
    public string? Settings { get; init; }
}

public sealed record HellForgeModInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    /// <summary>gameplay / per-player / cosmetic。</summary>
    public string? Group { get; init; }
    public List<string> Tags { get; init; } = new();
    public string? Folder { get; init; }
    public string? Archive { get; init; }
    public bool Removable { get; init; }
    public string? Unsupported { get; init; }
    public List<HellForgeModPremiumItem>? Premium { get; init; }
    public string? Url { get; init; }
    public string? Icon { get; init; }
    public HellForgeModBuild? Build { get; init; }
    /// <summary>仅 cosmetic pack 有选项；其余模组为 null。</summary>
    public List<HellForgeModOption>? Options { get; init; }
}

public sealed record HellForgeModPremiumItem
{
    public string? Requirement { get; init; }
    public string? Name { get; init; }
}

public sealed record HellForgeModBuild
{
    public string? State { get; init; }
    public DateTime? Built { get; init; }
    public string? Error { get; init; }
}

/// <summary>cosmetic pack 的一个选项：on=启用，variants=子选项名（无则 null），picked=当前选中的子选项名。</summary>
public sealed record HellForgeModOption
{
    public string Name { get; init; } = string.Empty;
    public bool On { get; init; }
    public List<string>? Variants { get; init; }
    public string? Picked { get; init; }
}

public sealed record HellForgeModAddResult
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string Installed { get; init; } = string.Empty;
    public string? Replaced { get; init; }
}

public sealed record HellForgeModRemoveResult
{
    public string Id { get; init; } = string.Empty;
    public string Removed { get; init; } = string.Empty;
}

public sealed record HellForgeModSwitchResult
{
    public string Id { get; init; } = string.Empty;
    public bool Enabled { get; init; }
}

public sealed record HellForgeModOrderResult
{
    public List<string> Order { get; init; } = new();
}

public sealed record HellForgeModPackResult
{
    public string Id { get; init; } = string.Empty;
    public List<HellForgeModOption> Options { get; init; } = new();
}

/// <summary>HellForge CLI <c>mod-sync-offer</c> 的当前提示；inGamePrompt 为 true 时只能由游戏内编辑器应答。</summary>
public sealed record HellForgeModSyncOfferResult
{
    public string? Run { get; init; }
    public bool InGamePrompt { get; init; }
    public string? Sent { get; init; }
    public HellForgeModSyncOffer? Offer { get; init; }
}

public sealed record HellForgeModSyncOffer
{
    public int Version { get; init; }
    public string? State { get; init; }
    public string? Host { get; init; }
    public bool Stranger { get; init; }
    public bool Live { get; init; }
    public long Bytes { get; init; }
    public long Done { get; init; }
    public string? Message { get; init; }
    public List<string> Answers { get; init; } = new();
    public List<HellForgeModSyncMod> Mods { get; init; } = new();
    public string? TrustNote { get; init; }
}

public sealed record HellForgeModSyncMod
{
    public string Id { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Class { get; init; }
    public string? Action { get; init; }
    public long Bytes { get; init; }
    public string? Url { get; init; }
    public List<string> Answers { get; init; } = new();
    public string? TrustWarning { get; init; }
    public bool CanTrust => Answers.Contains("trust");
    public bool CanBlock => Answers.Contains("block");
    public bool CanOpen => Answers.Contains("open");
}

/// <summary>HellForge CLI <c>versions</c>：安装版本与当前会话实际使用的保留版本。</summary>
public sealed record HellForgeVersions
{
    public HellForgeVersionInfo? Current { get; init; }
    public HellForgeVersionInfo? Playing { get; init; }
    public string? Supported { get; init; }
    public List<HellForgeKeptVersion> Kept { get; init; } = new();
    public int Keeps { get; init; }
    public long KeptBytes { get; init; }
    public string? Log { get; init; }
}

public record HellForgeVersionInfo
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Commit { get; init; }
    public string? Engine { get; init; }
    public string? GameBuild { get; init; }
    public string? Family { get; init; }
}

public sealed record HellForgeKeptVersion : HellForgeVersionInfo
{
    public DateTime? KeptUtc { get; init; }
    public string? Data { get; init; }
    public bool Newer { get; init; }
    public bool Playing { get; init; }
    public string? Problem { get; init; }
    public string? GameBuildNote { get; init; }
}

/// <summary>HellForge CLI <c>planets --catalogue yes</c>：自定义星球覆盖与可用阵营目录。</summary>
public sealed record HellForgePlanetsResult
{
    public string? File { get; init; }
    public string? War { get; init; }
    public bool Active { get; init; }
    public List<string> Problems { get; init; } = new();
    public bool SessionRunning { get; init; }
    public List<HellForgePlanetInfo> Planets { get; init; } = new();
    public HellForgePlanetCatalogue? Catalogue { get; init; }
}

public sealed record HellForgePlanetInfo
{
    public int Index { get; init; }
    public string? Name { get; init; }
    public string Faction { get; init; } = "war";
    /// <summary>CLI 用字符串 <c>war</c> 或字符串数组表示“沿用战争”与显式选择。</summary>
    public JsonElement Subfactions { get; init; }
    public Dictionary<string, string> Enemies { get; init; } = new();
    public HellForgePlanetServed? Served { get; init; }

    public bool UsesWarSubfactions => Subfactions.ValueKind != JsonValueKind.Array;
    public IReadOnlyList<string> SubfactionKeys => Subfactions.ValueKind == JsonValueKind.Array
        ? Subfactions.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).ToArray()
        : Array.Empty<string>();
}

public sealed record HellForgePlanetServed
{
    public string? Owner { get; init; }
    public string? Enemy { get; init; }
    public bool Playable { get; init; }
    public List<string> Subfactions { get; init; } = new();
    public List<string> Surges { get; init; } = new();
    public List<string> Left { get; init; } = new();
}

public sealed record HellForgePlanetCatalogue
{
    public List<string> Factions { get; init; } = new();
    public List<HellForgePlanetCatalogueEntry> Subfactions { get; init; } = new();
    public List<HellForgePlanetCatalogueEntry> Enemies { get; init; } = new();
}

public sealed record HellForgePlanetCatalogueEntry
{
    public string Key { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? Faction { get; init; }
}

/// <summary>HellForge CLI <c>logs</c> 命令的输出：会话列表（最新在前）、可选的单会话文件列表与日志路径。</summary>
public sealed record HellForgeLogsResult
{
    public List<HellForgeLogRun> Runs { get; init; } = new();
    /// <summary>仅 <c>--run</c> 指定时非 null。</summary>
    public List<HellForgeLogFile>? Files { get; init; }
    public string? SessionRunnerLog { get; init; }
    public string? GameLogs { get; init; }
}

public sealed record HellForgeLogRun
{
    public string Id { get; init; } = string.Empty;
    public string? Folder { get; init; }
    public DateTime? StartedUtc { get; init; }
    public string? Status { get; init; }
    public string? Failure { get; init; }
    /// <summary>形如 0x00000000；会话未结束时为 null。</summary>
    public string? GameExitCode { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public bool Crashed { get; init; }
}

public sealed record HellForgeLogFile
{
    public string Path { get; init; } = string.Empty;
    public long Bytes { get; init; }
}

public sealed record HellForgeLogExportResult
{
    public HellForgeLogRun? Run { get; init; }
    public string? Zip { get; init; }
    public int Files { get; init; }
    public int Skipped { get; init; }
}

public sealed record HellForgeCrashReportResult
{
    public HellForgeLogRun? Run { get; init; }
    public string? Zip { get; init; }
    public int Files { get; init; }
    public int Skipped { get; init; }
    public bool Dump { get; init; }
    public bool DumpAvailable { get; init; }
    public string? IssuePage { get; init; }
}

/// <summary>CLI play --dry-run 的输出：会话运行器路径与完整参数（不启动任何程序）。</summary>
public sealed record HellForgePlayDryRun
{
    public string? SessionRunner { get; init; }
    public List<string> Arguments { get; init; } = new();
    public bool CrashDump { get; init; }
}

/// <summary>MOD SYNC 后自动重启失败的原因；None 表示没有失败。</summary>
public enum HellForgeRestartIssue
{
    None,
    UnsupportedPlatform,
    MissingSyncFile,
    DryRunFailed,
    StartFailed,
    NewRunTimeout,
    TooManyRestarts
}

/// <summary>会话阶段，映射 CLI play --wait 的 status 事件（docs/external-launchers.md）。</summary>
public enum HellForgeSessionPhase
{
    Idle,
    Preparing,
    AwaitingNetworkBlock,
    AwaitingSteamBroker,
    Patching,
    Running,
    Ended,
    Failed
}

/// <summary>会话进度快照；由 HellForgeSessionService 在后台更新，UI 订阅 StateChanged 后自行派发。</summary>
public sealed record HellForgeSessionProgress
{
    public static HellForgeSessionProgress Idle { get; } = new();

    public HellForgeSessionPhase Phase { get; init; }
    public string? RunId { get; init; }
    public string? Failure { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    public string? Notice { get; init; }
    /// <summary>当前可由外部启动器应答的 MOD SYNC 提示；游戏内编辑器自己显示时为 null。</summary>
    public HellForgeModSyncOffer? ModSyncOffer { get; init; }
    /// <summary>是否由本管理器启动并正在观察事件流。</summary>
    public bool Tracked { get; init; }
    /// <summary>MOD SYNC 要求重启：正在按房主模组重启，或等待重启开始。</summary>
    public bool Restarting { get; init; }
    /// <summary>自动重启失败原因；None 表示没有。</summary>
    public HellForgeRestartIssue RestartIssue { get; init; }
    public string? RestartIssueDetail { get; init; }

    public bool IsActive => Phase is HellForgeSessionPhase.Preparing or HellForgeSessionPhase.AwaitingNetworkBlock
        or HellForgeSessionPhase.AwaitingSteamBroker or HellForgeSessionPhase.Patching or HellForgeSessionPhase.Running;

    public static HellForgeSessionPhase PhaseFromStatus(string? status) => status switch
    {
        "preparing" => HellForgeSessionPhase.Preparing,
        "awaiting_network_block_install" or "awaiting_windows_firewall_consent" => HellForgeSessionPhase.AwaitingNetworkBlock,
        "awaiting_steam_broker" => HellForgeSessionPhase.AwaitingSteamBroker,
        "patching" => HellForgeSessionPhase.Patching,
        "observing" => HellForgeSessionPhase.Running,
        "observed" => HellForgeSessionPhase.Ended,
        "failed" or "cleanup_failed" => HellForgeSessionPhase.Failed,
        _ => HellForgeSessionPhase.Idle
    };
}