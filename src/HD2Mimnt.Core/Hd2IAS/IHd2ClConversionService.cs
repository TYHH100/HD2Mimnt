namespace HD2Mimnt.Core.Hd2IAS;

/// <summary>可转换为 HD2CL 角色库的 HD2IAS 模组。</summary>
public sealed record Hd2ClCandidate(Guid ModId, string Name, string Path, int CompanionComponents);

/// <summary>合集模组中的一个可转换版本；ComponentFolders 是相对模组根的组件目录。</summary>
public sealed record Hd2ClVariant(string Id, string Name, string? Description, IReadOnlyList<string> ComponentFolders);

/// <summary>角色库成品标记：模组库与配置页据此显示 HD2CL 徽标，与 HD2IAS 源模组区分。</summary>
public static class Hd2ClProductTag
{
    public const string Value = "HD2CL";
}

/// <summary>导入合集时会给组件名加「类型 · 」前缀；成品名只保留作者名部分。</summary>
public static class Hd2ClNames
{
    public static string StripComponentPrefix(string name)
    {
        var separator = name.IndexOf(" · ", StringComparison.Ordinal);
        if (separator <= 0) return name;
        if (!System.Enum.TryParse<Enums.ComponentType>(name[..separator], ignoreCase: false, out _)) return name;
        var stripped = name[(separator + 3)..].Trim();
        return stripped.Length == 0 ? name : stripped;
    }
}

/// <summary>HD2CL 转换结果：成品模组身份与当前配置中的源模组禁用状态。</summary>
public sealed record Hd2ClConversionResult(
    Guid ModId,
    string Name,
    Guid? ProfileId,
    bool SourceDisabled);

/// <summary>HD2CL 导出结果：成品名与写出的 7z 路径（未导入模组库）。</summary>
public sealed record Hd2ClExportResult(string Name, string ArchivePath);

public interface IHd2ClConversionService
{
    Task<IReadOnlyList<Hd2ClCandidate>> GetCandidatesAsync(CancellationToken token = default);

    /// <summary>识别合集模组里的多个可转换版本；普通模组返回单个版本。</summary>
    Task<IReadOnlyList<Hd2ClVariant>> GetVariantsAsync(Guid modId, CancellationToken token = default);

    /// <summary>把 HD2IAS 模组转换为 HD2CL 角色库模型：导入模组库、加入当前配置并禁用源 IAS 模组（保留文件）。</summary>
    Task<Hd2ClConversionResult> ConvertAsync(Guid modId, IReadOnlyList<string>? componentFolders = null,
        string? productName = null, IProgress<string>? progress = null, CancellationToken token = default);

    /// <summary>把成品打成 7z 写到 outputDirectory：不导入模组库、不修改当前配置。</summary>
    Task<Hd2ClExportResult> ExportAsync(Guid modId, string outputDirectory,
        IReadOnlyList<string>? componentFolders = null, string? productName = null,
        IProgress<string>? progress = null, CancellationToken token = default);
}
