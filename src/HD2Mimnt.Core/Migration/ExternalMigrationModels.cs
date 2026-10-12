using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Migration;

public enum ExternalManagerKind { Helldivers2ModManager, Echelon, HD2Arsenal }

/// <summary>路径由调用方选择；InstallationPath 仅用于读取 HD2MM 的 settings.json。</summary>
public sealed record ExternalMigrationSource(ExternalManagerKind Manager, string DataPath,
    string? InstallationPath = null, string? ModsPath = null);

public sealed class ExternalMigrationPlan
{
    public required ExternalMigrationSource Source { get; init; }
    public required string SourceIdentity { get; init; }
    public List<ExternalMigrationMod> Mods { get; } = [];
    public List<ExternalMigrationProfile> Profiles { get; } = [];
    public List<ExternalMigrationIssue> Issues { get; } = [];
}

public sealed class ExternalMigrationMod
{
    public required string SourceId { get; init; }
    public required string SourcePath { get; init; }
    public required ModManifest Manifest { get; init; }
    public List<string> Tags { get; init; } = [];
    public string? ImagePath { get; init; }
    /// <summary>Echelon 的精确资源集合重新布局到独立目录，避免 Include 误选同目录其它集合。</summary>
    public List<ExternalMigrationFile> FileLayout { get; init; } = [];
}

public sealed record ExternalMigrationFile(string SourceRelativePath, string TargetRelativePath);

public sealed class ExternalMigrationProfile
{
    public required string SourceId { get; init; }
    public required string Name { get; init; }
    public bool WasActive { get; init; }
    public DeploymentOrder? DeploymentOrder { get; init; }
    public List<ExternalMigrationProfileMod> Mods { get; } = [];
    public List<ExternalMigrationSeparator> Separators { get; } = [];
    public bool IsLuaGroupCollapsed { get; set; }
    public string LuaSeparatorColor { get; set; } = string.Empty;
}

public sealed record ExternalMigrationProfileMod(string SourceModId, bool Enabled, ProfileModOptionsState Options);

/// <summary>Order/MemberCount 使用源配置的模组位置；执行时映射到普通模组区域。</summary>
public sealed record ExternalMigrationSeparator(string Name, int Order, bool IsCollapsed = false,
    string Color = "", int? MemberCount = null);

public sealed record ExternalMigrationIssue(string Item, string Message);
public sealed record ExternalMigrationProgress(int Completed, int Total, string Stage, string Item);

public sealed class ExternalMigrationResult
{
    /// <summary>源软件的活动配置对应的目标 ID；迁移不会自动切换目标的活动配置。</summary>
    public Guid? SourceActiveProfileId { get; set; }
    public Dictionary<string, Guid> ModIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Guid> ProfileIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ExternalMigrationIssue> Issues { get; } = [];
    public int ImportedMods { get; set; }
    public int ReusedMods { get; set; }
    public int ImportedProfiles { get; set; }
    public int ReusedProfiles { get; set; }
    public bool IsComplete => Issues.Count == 0;
}
