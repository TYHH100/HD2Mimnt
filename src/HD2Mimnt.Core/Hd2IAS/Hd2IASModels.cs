namespace HD2Mimnt.Core.Hd2IAS;

public enum Hd2IASRoute { Lua, Native }
public enum Hd2IASFileState { Missing, Match, RecognizedPrevious, Unknown }
public enum Hd2IASInstallationState { NotChecked, Absent, IncompleteOrUnknown, RuntimeMatches }

public sealed record Hd2IASFile(string Path, string? Target, string Sha256, long? Bytes,
    IReadOnlyList<string> PreviousSha256);
public sealed record Hd2IASLuaIdentity(string Module, string ResourceId, string BodySha256,
    string Identity, string SourceSha256);
public sealed record Hd2IASRuntimeManifest(string Version, Hd2IASRoute Route, string AppearanceSource,
    string GameExeSha256, string BundleDatabaseSha256, string Lua51Sha256,
    Hd2IASLuaIdentity IndependentLua, IReadOnlyList<Hd2IASFile> Files,
    IReadOnlyList<Hd2IASFile> RetiredFiles);
public sealed record Hd2IASFoundationManifest(string Version, string GameExeSha256,
    string BundleDatabaseSha256, IReadOnlyList<Hd2IASFile> Files);
public sealed record Hd2IASFileCheck(string Path, Hd2IASFileState State,
    string? ActualSha256 = null, string? ExpectedSha256 = null);
public enum Hd2IASBuildState { NotChecked, Missing, Read, Invalid }
public enum Hd2IASConfigKind { UserSettings, SystemCatalog, Unclassified }
public enum Hd2IASFileAction { SkipUnchanged, PreserveSettings, Create, ReplaceRecognized, RemoveRecognized, BlockUnknown }
public sealed record Hd2IASConfigFile(string Path, Hd2IASConfigKind Kind);
public sealed record Hd2IASInstalledBuild(string Path, Hd2IASBuildState State, string? Version,
    Hd2IASRoute? Route, string Contents, IReadOnlyList<Hd2IASConfigFile> ConfigurationFiles, string? Error = null);
public sealed record Hd2IASFilePlan(string Path, Hd2IASFileAction Action, string? ActualSha256, string? ExpectedSha256);
public sealed record Hd2IASPackage(string Version, Hd2IASRuntimeManifest Lua,
    Hd2IASRuntimeManifest Native, Hd2IASFoundationManifest Foundation,
    IReadOnlyList<Hd2IASFileCheck> FileChecks, string UpdateNotes, IReadOnlyList<string> ManualActions)
{
    public bool IsValid => FileChecks.Count > 0 && FileChecks.All(file => file.State == Hd2IASFileState.Match);
    public Hd2IASRuntimeManifest ForRoute(Hd2IASRoute route) => route == Hd2IASRoute.Lua ? Lua : Native;
}
public sealed record Hd2IASInspection(Hd2IASPackage Package, Hd2IASRoute SelectedRoute,
    Hd2IASInstallationState Installation, Hd2IASRoute? InstalledRoute, string? InstalledVersion,
    IReadOnlyList<Hd2IASFileCheck> GameChecks, IReadOnlyList<Hd2IASFileCheck> RuntimeChecks,
    IReadOnlyList<Hd2IASFileCheck> FoundationChecks)
{
    public Hd2IASInstalledBuild? InstalledBuild { get; init; }
    public IReadOnlyList<Hd2IASFilePlan> FilePlan { get; init; } = [];
    // 运行时匹配不代表 data 包有效，不能将此属性作为可部署/可切换的判据。
    public bool IsGameCompatible => GameChecks.Count > 0 && GameChecks.All(file => file.State == Hd2IASFileState.Match);
}

public interface IHd2IASPackageReader
{
    Task<Hd2IASPackage> ReadAsync(string zipPath, CancellationToken cancellationToken = default);
}

public interface IHd2IASRuntimeInspector
{
    Task<Hd2IASInstalledBuild> ReadInstalledAsync(string? gamePath, CancellationToken cancellationToken = default);
    Task<Hd2IASInspection> InspectAsync(Hd2IASPackage package, string? gamePath,
        Hd2IASRoute selectedRoute, CancellationToken cancellationToken = default);
}

public interface IHd2IASLuaCompiler
{
    Task ValidateAsync(string lua51Path, string expectedSha256, byte[] source, string module,
        CancellationToken cancellationToken = default);
}

public sealed record Hd2IASGeneratedPackage(string Path, string Version, Hd2IASRoute Route,
    string Module, string Identity, string SourceSha256)
{
    public Guid? ModId { get; init; }
}

public sealed record Hd2IASUpdateVersion(string Version);

public sealed record Hd2IASManagementState(Hd2IASInstalledBuild Build, bool HasManagedInstallation,
    bool HasDataMod, bool HasDataFiles, IReadOnlyList<string> MissingFiles)
{
    public bool NeedsInstallation => HasDataMod && MissingFiles.Count > 0;
    public bool NeedsDataGeneration => !HasDataFiles && (HasDataMod || HasManagedInstallation || Build.State == Hd2IASBuildState.Read);
}
public sealed record Hd2IASUninstallResult(IReadOnlyList<string> RemovedFiles, IReadOnlyList<string> PreservedFiles,
    IReadOnlyList<string> ModifiedFiles);

/// <summary>官方更新目录；下载流由调用方释放，释放时清理临时文件。</summary>
public interface IHd2IASUpdateSource
{
    Task<IReadOnlyList<Hd2IASUpdateVersion>> GetVersionsAsync(CancellationToken cancellationToken = default);
    Task<Stream> DownloadAsync(Hd2IASUpdateVersion version,
        IProgress<HD2Mimnt.Core.Entities.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
