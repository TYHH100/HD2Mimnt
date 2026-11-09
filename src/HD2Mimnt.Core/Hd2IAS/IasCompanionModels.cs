namespace HD2Mimnt.Core.Hd2IAS;

/// <summary>IAS 配套环境的当前状态（供 UI 展示与部署收口判断）。</summary>
public sealed record IasCompanionStatus(
    /// <summary>是否配置了 IAS 系统安装位置并找到了 Runtime 目录。</summary>
    bool RuntimeResolved,
    /// <summary>上游 armor 组件是否已部署（deployment.json 含 armor 组件）。</summary>
    bool SystemInstalled,
    /// <summary>当前生效的 Runtime 目录根（Runtime\&lt;随机ID&gt;）。</summary>
    string? RuntimeRoot,
    /// <summary>当前生效的 Runtime ID。</summary>
    string? RuntimeId);

/// <summary>一次配套生成的事务结果统计。</summary>
public sealed record IasCompanionResult(
    int FilesWritten,
    int FilesDeleted,
    int FilesPreserved,
    string TransactionPath);
