using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 插件权限统一判定实现。权限逻辑从 PluginService.CheckPermissionAsync 抽出，
/// 供权限检查与宿主启动快照共用；判定顺序：插件存在 → FullAccess 直通 →
/// 会话级授权（“仅本次允许”）→ 数据库授权。
/// </summary>
public sealed class PluginPermissionChecker(
    IPluginRepository pluginRepository,
    IPluginPermissionRepository permissionRepository,
    IPluginGrantSession grantSession) : IPluginPermissionChecker
{
    private readonly IPluginRepository _pluginRepository = pluginRepository;
    private readonly IPluginPermissionRepository _permissionRepository = permissionRepository;
    private readonly IPluginGrantSession _grantSession = grantSession;

    public async Task<bool> CheckAsync(string pluginId, string permission, CancellationToken cancellationToken = default)
    {
        var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
        if (plugin == null)
        {
            return false;
        }

        if (plugin.PermissionMode == "FullAccess")
        {
            return true;
        }

        // “仅本次允许”的授权只存在于本进程，优先于数据库判断
        if (_grantSession.IsGranted(pluginId, permission))
        {
            return true;
        }

        var permissions = await _permissionRepository.GetByPluginIdAsync(pluginId, cancellationToken);
        var pluginPermission = permissions.FirstOrDefault(p => p.Permission == permission);

        return pluginPermission?.Granted ?? false;
    }

    public async Task<IReadOnlyList<string>> GetGrantedAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
        if (plugin == null || plugin.PermissionMode == "FullAccess")
        {
            return Array.Empty<string>();
        }

        var granted = new List<string>();
        foreach (var permission in await _permissionRepository.GetByPluginIdAsync(pluginId, cancellationToken))
        {
            if (permission.Granted || _grantSession.IsGranted(pluginId, permission.Permission))
            {
                granted.Add(permission.Permission);
            }
        }

        return granted;
    }

    public async Task<string> GetPermissionModeAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
        return plugin?.PermissionMode ?? string.Empty;
    }
}
