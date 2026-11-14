namespace HD2Mimnt.Core.Hd2IAS;

/// <summary>可转换为 HD2CL 角色库的 HD2IAS 模组。</summary>
public sealed record Hd2ClCandidate(Guid ModId, string Name, string Path, int CompanionComponents);

/// <summary>合集模组中的一个可转换版本；ComponentFolders 是相对模组根的组件目录。</summary>
public sealed record Hd2ClVariant(string Id, string Name, string? Description, IReadOnlyList<string> ComponentFolders);

/// <summary>HD2CL 转换结果：成品模组身份与当前配置中的源模组禁用状态。</summary>
public sealed record Hd2ClConversionResult(
    Guid ModId,
    string Name,
    Guid? ProfileId,
    bool SourceDisabled);

public interface IHd2ClConversionService
{
    Task<IReadOnlyList<Hd2ClCandidate>> GetCandidatesAsync(CancellationToken token = default);

    /// <summary>识别合集模组里的多个可转换版本；普通模组返回单个版本。</summary>
    Task<IReadOnlyList<Hd2ClVariant>> GetVariantsAsync(Guid modId, CancellationToken token = default);

    /// <summary>把 HD2IAS 模组转换为 HD2CL 角色库模型：导入模组库、加入当前配置并禁用源 IAS 模组（保留文件）。</summary>
    Task<Hd2ClConversionResult> ConvertAsync(Guid modId, IReadOnlyList<string>? componentFolders = null,
        string? productName = null, IProgress<string>? progress = null, CancellationToken token = default);
}
