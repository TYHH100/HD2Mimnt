using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interop;

/// <summary>只供离线导出使用的数据库一致性快照，不属于公开 JSON 契约。</summary>
public sealed record OfflineInteropData(IReadOnlyList<Mod> Mods, IReadOnlyList<Profile> Profiles,
    IReadOnlyList<DeploymentRecord> Deployments);

public interface IOfflineInteropSource
{
    Task<OfflineInteropData> ReadAsync(CancellationToken cancellationToken = default);
}

// v1 的字段名称、类型和含义保持稳定。后续破坏性变化应新增适配器，不能改写旧格式。
public sealed record OfflineRuntimeV1(
    string Format, int SchemaVersion, int SchemaMinorVersion, string SnapshotId,
    DateTimeOffset GeneratedUtc, string ManagerVersion, OfflineStorageV1 Storage,
    Guid? ActiveProfileId, string DeploymentOrder, IReadOnlyList<OfflineModV1> Library,
    IReadOnlyList<OfflineProfileV1> Profiles, IReadOnlyList<OfflineDeploymentV1> Deployments);

public sealed record OfflineStorageV1(string AppDataPath, string ModsPath);

public sealed record OfflineModV1(
    Guid Id, Guid? ManifestGuid, string Name, string Version, string Author, string Description,
    string Path, string? ThumbnailPath, long Size, string Hash, string Role, string Category,
    string Workflow, string Status, IReadOnlyList<string> Tags,
    bool ContainsScripts, IReadOnlyList<string>? ResourceTypeIds, DateTimeOffset ModifiedUtc,
    IReadOnlyList<string> LegacyOptions, IReadOnlyList<OfflineOptionV1> Options,
    OfflineOptionStateV1 DefaultOptions, bool FilesComplete, string? FilesError,
    IReadOnlyList<OfflineLibraryFileV1> Files);

public sealed record OfflineOptionV1(string Name, string Description, bool DefaultEnabled,
    IReadOnlyList<string>? Include, string? Image, IReadOnlyList<OfflineSubOptionV1> SubOptions);
public sealed record OfflineSubOptionV1(string Name, string Description, IReadOnlyList<string>? Include, string? Image);
public sealed record OfflineOptionStateV1(int? LegacySelectedIndex, IReadOnlyList<bool>? OptionToggles,
    IReadOnlyList<int>? OptionSelections);
public sealed record OfflineLibraryFileV1(string RelativePath, long Size);

public sealed record OfflineProfileV1(Guid Id, string Name, bool IsActive,
    IReadOnlyList<OfflineProfileModV1> Mods, IReadOnlyList<Guid> DeploymentSequence);
public sealed record OfflineProfileModV1(Guid ModId, bool Enabled, int Order, OfflineOptionStateV1 Options);

public sealed record OfflineDeploymentV1(
    Guid Id, Guid SessionId, Guid ProfileId, Guid ModId, string Status, string Method,
    DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, int? Order,
    string? ModVersion, OfflineOptionStateV1? Options, bool IsComplete,
    IReadOnlyList<OfflineDeployedFileV1> Files, IReadOnlyList<string> MissingTargets);
public sealed record OfflineDeployedFileV1(string? SourcePath, string TargetPath, string Method, bool IsPlaceholder);

/// <summary>每个主版本独立投影；v1 始终注册，不能用未来版本的文件替代旧文件。</summary>
public interface IOfflineInteropAdapter
{
    int MajorVersion { get; }
    int MinorVersion { get; }
    object Project(OfflineRuntimeV1 snapshot);
}

public sealed class OfflineInteropV1Adapter : IOfflineInteropAdapter
{
    public int MajorVersion => 1;
    public int MinorVersion => 0;
    public object Project(OfflineRuntimeV1 snapshot) => snapshot;
}
