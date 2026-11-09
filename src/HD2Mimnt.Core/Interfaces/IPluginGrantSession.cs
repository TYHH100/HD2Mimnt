namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 会话级插件授权：用户在安装对话框选择“仅本次允许”时，权限只写入内存，
/// 下次启动自动失效。与数据库中的持久授权合并生效。
/// </summary>
public interface IPluginGrantSession
{
    void Grant(string pluginId, IEnumerable<string> permissions);

    bool IsGranted(string pluginId, string permission);

    void Forget(string pluginId);
}

/// <summary>
/// 默认的内存实现；进程重启即清空，符合“仅本次允许”的语义。
/// </summary>
public sealed class PluginGrantSession : IPluginGrantSession
{
    private readonly Dictionary<string, HashSet<string>> _grants = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public void Grant(string pluginId, IEnumerable<string> permissions)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return;
        }

        lock (_sync)
        {
            if (!_grants.TryGetValue(pluginId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _grants[pluginId] = set;
            }

            foreach (var permission in permissions)
            {
                if (!string.IsNullOrWhiteSpace(permission))
                {
                    set.Add(permission);
                }
            }
        }
    }

    public bool IsGranted(string pluginId, string permission)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(permission))
        {
            return false;
        }

        lock (_sync)
        {
            return _grants.TryGetValue(pluginId, out var set) && set.Contains(permission);
        }
    }

    public void Forget(string pluginId)
    {
        lock (_sync)
        {
            _grants.Remove(pluginId);
        }
    }
}
