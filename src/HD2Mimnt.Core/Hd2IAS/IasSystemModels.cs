namespace HD2Mimnt.Core.Hd2IAS;

/// <summary>IAS 模型组件中的一个待部署文件：源为模组库内绝对路径，RelativePath 为 rig 目录下的相对路径。</summary>
public sealed record IasModelFile(string SourcePath, string RelativePath);

/// <summary>模组库中的一个 IAS 模型组件（workflow 为 HD2IAS 的模组）及其 native 文件清单。</summary>
public sealed record IasModelComponent(Guid ModId, string ModName, IReadOnlyList<IasModelFile> Files);

/// <summary>一次部署的实际方式与结果统计。</summary>
public sealed record IasDeployResult(
    string TargetRoot, string RequestedMethod, string UsedMethod,
    int DeployedCount, int SkippedCount)
{
    /// <summary>请求硬链接但目标与模组库跨盘（或不兼容），自动降级为文件复制。</summary>
    public bool UsedCopyFallback => RequestedMethod == "HardLink" && UsedMethod == "Copy";
}

/// <summary>卸载结果：成功清理数与缺失（早已不存在）的目标清单。</summary>
public sealed record IasUninstallResult(int RemovedCount, IReadOnlyList<string> MissingFiles);
