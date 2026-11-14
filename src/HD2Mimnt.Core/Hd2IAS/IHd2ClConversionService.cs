namespace HD2Mimnt.Core.Hd2IAS;

/// <summary>可转换为 HD2CL 角色库的 HD2IAS 模组。</summary>
public sealed record Hd2ClCandidate(Guid ModId, string Name, string Path, int CompanionComponents);

/// <summary>HD2CL 转换结果：成品模组身份与当前配置中的源模组禁用状态。</summary>
public sealed record Hd2ClConversionResult(
    Guid ModId,
    string Name,
    Guid? ProfileId,
    bool SourceDisabled);

public interface IHd2ClConversionService
{
    Task<IReadOnlyList<Hd2ClCandidate>> GetCandidatesAsync(CancellationToken token = default);

    /// <summary>把 HD2IAS 模组转换为 HD2CL 角色库模型：导入模组库、加入当前配置并禁用源 IAS 模组（保留文件）。</summary>
    Task<Hd2ClConversionResult> ConvertAsync(Guid modId, IProgress<string>? progress = null,
        CancellationToken token = default);
}
