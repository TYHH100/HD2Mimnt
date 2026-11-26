namespace HD2Mimnt.PluginSDK;

/// <summary>
/// 插件 Id 的字符集校验。Id 会拼进宿主进程的数据目录路径（<c>Plugins/&lt;id&gt;</c>），
/// 必须限制为安全的文件名片段：反向域名（com.example.xxx）与 GUID 均满足白名单，
/// 路径分隔符、盘符与遍历序列一律拒绝。
/// </summary>
public static class PluginId
{
    private const int MaxLength = 128;

    /// <summary>
    /// 判定 Id 是否合法：非空、不超过 <see cref="MaxLength"/>，
    /// 且仅含 ASCII 字母、数字、点、下划线、连字符（拒绝 <c>.</c> / <c>..</c>）。
    /// </summary>
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxLength)
        {
            return false;
        }

        if (id is "." or "..")
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
