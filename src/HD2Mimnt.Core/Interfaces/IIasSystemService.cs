using HD2Mimnt.Core.Hd2IAS;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 新版 IAS 系统（HD2IntegratedArmorSystem）的模型部署服务。
/// 独立于配置文件部署体系：把模组库中 IAS 模型组件的 native 文件部署到
/// &lt;安装位置&gt;\HD2IntegratedArmorSystem\Runtime\&lt;已有随机ID&gt;\armor\rig。
/// </summary>
public interface IIasSystemService
{
    /// <summary>读取已保存的 IAS 系统安装位置（未设置时为 null）。</summary>
    Task<string?> GetInstallLocationAsync(CancellationToken token = default);

    /// <summary>保存 IAS 系统安装位置。</summary>
    Task SaveInstallLocationAsync(string location, CancellationToken token = default);

    /// <summary>扫描安装位置下已存在的 Runtime\&lt;随机ID&gt; 目录（沿用上游安装器创建的目录）。</summary>
    Task<IReadOnlyList<string>> DiscoverRuntimeIdsAsync(string location, CancellationToken token = default);

    /// <summary>列出模组库中可部署的 IAS 模型组件及其文件清单。</summary>
    Task<IReadOnlyList<IasModelComponent>> GetModelComponentsAsync(CancellationToken token = default);

    /// <summary>把全部 IAS 模型组件部署到指定 Runtime 目录的 armor\rig 下。</summary>
    Task<IasDeployResult> DeployAsync(string location, string runtimeId,
        IProgress<string>? progress = null, CancellationToken token = default);

    /// <summary>按部署清单卸载已部署的文件（外部文件缺失时容错）。</summary>
    Task<IasUninstallResult> UninstallAsync(IProgress<string>? progress = null, CancellationToken token = default);

    /// <summary>是否存在本服务部署的记录。</summary>
    Task<bool> HasDeploymentAsync(CancellationToken token = default);

    /// <summary>
    /// 解析随配置文件部署使用的 rig 目标根目录（&lt;位置&gt;\HD2IntegratedArmorSystem\Runtime\&lt;ID&gt;\armor\rig）。
    /// 未配置位置、没有 Runtime 目录时返回 null（此类组件随配置文件部署时静默跳过）。
    /// </summary>
    Task<string?> GetRigTargetRootAsync(CancellationToken token = default);

    /// <summary>保存随配置文件部署优先使用的 Runtime ID。</summary>
    Task SaveActiveRuntimeIdAsync(string? runtimeId, CancellationToken token = default);

    /// <summary>读取随配置文件部署优先使用的 Runtime ID。</summary>
    Task<string?> GetActiveRuntimeIdAsync(CancellationToken token = default);
}
