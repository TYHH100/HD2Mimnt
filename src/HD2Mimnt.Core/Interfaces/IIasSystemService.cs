using HD2Mimnt.Core.Hd2IAS;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 新版 IAS 系统（HD2IntegratedArmorSystem）的安装位置与 Runtime 发现服务。
/// 模型组件不再由本服务直接部署：armor 配套由 <see cref="IIasCompanionService"/> 调用
/// 上游生成器生成；本服务只负责定位安装位置与生效的 Runtime 目录。
/// </summary>
public interface IIasSystemService
{
    /// <summary>读取已保存的 IAS 系统安装位置（未设置时为 null）。</summary>
    Task<string?> GetInstallLocationAsync(CancellationToken token = default);

    /// <summary>保存 IAS 系统安装位置。</summary>
    Task SaveInstallLocationAsync(string location, CancellationToken token = default);

    /// <summary>扫描安装位置下已存在的 Runtime\&lt;随机ID&gt; 目录（沿用上游安装器创建的目录）。</summary>
    Task<IReadOnlyList<string>> DiscoverRuntimeIdsAsync(string location, CancellationToken token = default);

    /// <summary>列出模组库中的 IAS 模型组件及其 native 文件清单（展示用）。</summary>
    Task<IReadOnlyList<IasModelComponent>> GetModelComponentsAsync(CancellationToken token = default);

    /// <summary>
    /// 解析当前生效的 Runtime 根目录（&lt;位置&gt;\HD2IntegratedArmorSystem\Runtime\&lt;ID&gt;）。
    /// 未配置位置、没有 Runtime 目录时返回 null。
    /// </summary>
    Task<string?> GetRuntimeRootAsync(CancellationToken token = default);

    /// <summary>
    /// 解析上游外部运行资源目录（&lt;位置&gt;\HD2IntegratedArmorSystem\ExternalRuntime，
    /// 与 Runtime 同级，内含 ArmorAssets.zip / Generator.zip）。未配置安装位置时返回 null。
    /// </summary>
    Task<string?> GetExternalRuntimeRootAsync(CancellationToken token = default);

    /// <summary>保存优先使用的 Runtime ID。</summary>
    Task SaveActiveRuntimeIdAsync(string? runtimeId, CancellationToken token = default);

    /// <summary>读取优先使用的 Runtime ID。</summary>
    Task<string?> GetActiveRuntimeIdAsync(CancellationToken token = default);
}
