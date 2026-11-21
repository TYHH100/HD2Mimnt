namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 插件权限的统一判定：综合 FullAccess 模式、会话级授权（“仅本次允许”）与数据库授权。
/// PluginService 的权限检查与 PluginHostManager 下发给宿主的权限快照共用同一实现，
/// 保证“用户拒绝勾选的权限照常生效”不再出现。
/// </summary>
public interface IPluginPermissionChecker
{
    /// <summary>判定单个权限是否已授予。</summary>
    Task<bool> CheckAsync(string pluginId, string permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// 返回插件当前全部生效权限的集合（宿主启动快照用）；
    /// FullAccess 模式返回空集合并配合 <see cref="GetPermissionModeAsync"/> 判定。
    /// </summary>
    Task<IReadOnlyList<string>> GetGrantedAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>返回插件的权限模式（Sandbox/FullAccess）；插件不存在返回空串。</summary>
    Task<string> GetPermissionModeAsync(string pluginId, CancellationToken cancellationToken = default);
}
