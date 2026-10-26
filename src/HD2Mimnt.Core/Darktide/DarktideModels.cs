namespace HD2Mimnt.Core.Darktide;

public enum DarktideGameStatus
{
    NotConfigured,
    InvalidPath,
    GameWithoutLoader,
    LoaderIncomplete,
    LoaderInstalled
}

public enum DarktideIssueSeverity
{
    Info,
    Warning,
    Error
}

public enum DarktideIssueKind
{
    MissingGamePath,
    InvalidGamePath,
    LoaderNotInstalled,
    AlternativeLoader,
    DmfMissing,
    LoadOrderMissing,
    LoadOrderUnreadable,
    DuplicateRegistration,
    MissingRegisteredMod,
    DirectoryNameMismatch,
    MissingModFile,
    InvalidMetadata,
    MissingDependency,
    DisabledDependency,
    DependencyOrder,
    DependencyCycle,
    FrameworkRegistration,
    OrphanLibraryEntry,
    MissingInstalledFolder,
    UnsafePath,
    PatchStateUnknown
}

public sealed record DarktideDiagnostic(
    DarktideIssueSeverity Severity,
    DarktideIssueKind Kind,
    string? ModName = null,
    string? RelatedName = null,
    string? Detail = null);

public sealed record DarktideGameState(
    string? RootPath,
    DarktideGameStatus Status,
    bool HasGameExecutable,
    bool HasBundleDirectory,
    bool HasModsDirectory,
    bool HasLoadOrderFile,
    bool HasDmlCore,
    bool HasDmf,
    bool HasToggleScript,
    bool HasPatcher,
    bool PatchStateKnown,
    bool PatchLikelyEnabled,
    string? AlternativeLoader,
    IReadOnlyList<string> Notes)
{
    public static DarktideGameState NotConfigured { get; } = new(
        null, DarktideGameStatus.NotConfigured, false, false, false, false, false, false, false, false,
        false, false, null, Array.Empty<string>());
}

public sealed record DarktideModMetadata(
    string InternalName,
    string DisplayName,
    string Version,
    string Author,
    string Description,
    string? Homepage,
    string? SourceUrl,
    IReadOnlyList<string> RequiredDependencies,
    IReadOnlyList<string> LoadAfter,
    IReadOnlyList<string> LoadBefore,
    bool HasInfoJson,
    bool HasModFile,
    bool HasScripts,
    string? MetadataError = null);

public sealed record DarktideInstalledMod(
    string InternalName,
    string FolderName,
    string FolderPath,
    DarktideModMetadata Metadata,
    bool FolderNameMatchesModFile,
    bool IsFramework);

public sealed record DarktideLibraryEntry(
    string InternalName,
    string DisplayName,
    string Version,
    string Author,
    string Description,
    string ArchivePath,
    string ArchiveOriginalName,
    string ArchiveSha256,
    long ArchiveSize,
    IReadOnlyList<string> RequiredDependencies,
    DateTime ImportedAt,
    DateTime UpdatedAt);

public sealed record DarktideModOverview(
    string InternalName,
    string DisplayName,
    string Version,
    string Author,
    string Description,
    bool InLibrary,
    bool Installed,
    bool Registered,
    bool FolderNameMatches,
    bool HasModFile,
    string? InstalledPath,
    string? ArchivePath,
    long ArchiveSize,
    IReadOnlyList<string> RequiredDependencies,
    IReadOnlyList<string> LoadAfter,
    IReadOnlyList<string> LoadBefore,
    IReadOnlyList<DarktideDiagnostic> Diagnostics);

/// <summary>
/// 模组库的显示分组分隔符。Order 是分隔符之前的库模组数量，MemberCount 是组内模组数量
/// （null 表示延伸到下一个分隔符之前），Color 为 #RRGGBB 或空值跟随主题。
/// 只组织库列表显示，不写入 mod_load_order.txt，也不是可安装模组。
/// </summary>
public sealed record DarktideModSeparator(
    Guid Id,
    string Name,
    int Order,
    int? MemberCount,
    bool IsCollapsed,
    string Color);

public sealed record DarktideArchiveCandidate(
    string RelativeRoot,
    string InternalName,
    string ModFileName,
    bool HasScripts,
    bool HasInfoJson,
    int FileCount,
    long TotalBytes)
{
    public string DisplayPath => string.IsNullOrEmpty(RelativeRoot) ? "." : RelativeRoot;
}

public enum DarktideArchivePackageKind
{
    /// <summary>没有可用的模组结构。</summary>
    NotAMod,
    /// <summary>单个标准 DMF 模组。</summary>
    StandardMod,
    /// <summary>多个标准 DMF 模组打包在一起。</summary>
    ModBundle,
    /// <summary>除模组目录外还携带 mods 之外的可执行文件或游戏根文件，需要作者自己的安装流程。</summary>
    CustomInstallRequired
}

public sealed record DarktideArchiveInspection(
    string ArchivePath,
    IReadOnlyList<DarktideArchiveCandidate> Candidates,
    string? Error,
    DarktideArchivePackageKind Kind = DarktideArchivePackageKind.NotAMod,
    IReadOnlyList<string>? NonModFiles = null)
{
    public IReadOnlyList<string> ExtraFiles { get; } = NonModFiles ?? Array.Empty<string>();
}

public sealed record DarktideInstallResult(
    string InternalName,
    string InstalledPath,
    bool ReplacedExisting,
    bool Registered,
    IReadOnlyList<string> Warnings);

public sealed class DarktideInstallConflictException(string internalName)
    : InvalidOperationException($"mods 目录中已存在同名模组：{internalName}")
{
    public string InternalName { get; } = internalName;
}

public sealed record DarktideScanResult(
    DarktideGameState Game,
    IReadOnlyList<DarktideModOverview> Mods,
    IReadOnlyList<string> ActiveOrder,
    IReadOnlyList<string> DisabledMods,
    IReadOnlyList<DarktideDiagnostic> Diagnostics,
    DateTime ScannedAt);

public sealed record DarktideProfileApplyResult(
    string ProfileName,
    IReadOnlyList<string> Enabled,
    IReadOnlyList<string> Missing,
    string? BackupPath);

public sealed record DarktideLoadOrderSaveResult(string? BackupPath, bool Changed);

public sealed record DarktideProfileEntry(string Name, bool Enabled, int Order);

public sealed record DarktideProfile(
    string Name,
    DateTime CreatedAt,
    DateTime ModifiedAt,
    bool IsActive,
    IReadOnlyList<DarktideProfileEntry> Entries);

public sealed record DarktideProfileApplyPreview(
    string ProfileName,
    IReadOnlyList<string> ToEnable,
    IReadOnlyList<string> ToDisable,
    IReadOnlyList<string> Missing,
    bool OrderChanged,
    IReadOnlyList<string> UnknownEntries)
{
    public bool HasChanges => ToEnable.Count > 0 || ToDisable.Count > 0 || OrderChanged;
}

public enum DarktideReleasePackageType
{
    Full,
    UpdateOnly
}

public sealed record DarktideReleasePackage(
    DarktideReleasePackageType Type,
    string FileName,
    string DownloadUrl,
    string Source);

public sealed record DarktideReleaseInfo(
    string Tag,
    string Name,
    DateTimeOffset? PublishedAt,
    string PageUrl,
    string Body,
    IReadOnlyList<DarktideReleasePackage> Packages,
    string? Error)
{
    public bool HasFullPackage => Packages.Any(package => package.Type == DarktideReleasePackageType.Full);
    public bool HasUpdateOnlyPackage => Packages.Any(package => package.Type == DarktideReleasePackageType.UpdateOnly);
}

public sealed record DarktideLoaderPackageInfo(
    string PackagePath,
    DarktideReleasePackageType? Type,
    bool HasLoadOrder,
    bool HasDmlCore,
    bool HasToggleScript,
    bool HasPatcher,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> UnexpectedFiles,
    string? Error)
{
    public bool IsInstallable => Error == null && Type != null && HasDmlCore && HasToggleScript;
}

public sealed record DarktideLoaderInstallResult(
    bool Success,
    DarktideReleasePackageType PackageType,
    string? BackupDirectory,
    bool PreservedLoadOrder,
    IReadOnlyList<string> InstalledFiles,
    bool RolledBack,
    string? Error);

public sealed record DarktideLoaderToggleLaunch(
    bool Started,
    int? ProcessId,
    int? ExitCode,
    string? Error);

public sealed record DarktideGameLaunchResult(
    bool Started,
    int? ProcessId,
    string? Error);

public sealed record DarktideDownloadProgress(long ReceivedBytes, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? ReceivedBytes * 100d / TotalBytes.Value : null;
}

public sealed record DarktideDownloadedPackage(
    DarktideReleasePackage Package,
    string FilePath,
    string Sha256,
    long Size);
