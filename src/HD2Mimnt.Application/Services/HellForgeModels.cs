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
}

public sealed record HellForgeReadiness
{
    public bool GameCopy { get; init; }
    public bool SessionRunner { get; init; }
}

public sealed record HellForgeLauncherSettings
{
    public string? Profile { get; init; }
    public string? War { get; init; }
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
    /// <summary>仅 cosmetic pack 有选项；其余模组为 null。</summary>
    public List<HellForgeModOption>? Options { get; init; }
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
    /// <summary>是否由本管理器启动并正在观察事件流。</summary>
    public bool Tracked { get; init; }

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