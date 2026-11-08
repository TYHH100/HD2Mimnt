namespace HD2Mimnt.PluginSDK;

/// <summary>
/// 插件契约版本。
///
/// 版本策略：SDK 是一个对外契约，而不是一个独立发布的产品分支。
/// 主程序按“主版本兼容”规则校验 manifest 的 ApiVersion：
/// 同一主版本内保持向后兼容，新增能力只提升次版本；
/// 破坏性变更提升主版本，旧插件会被明确拒绝而不是静默出错。
/// </summary>
public static class PluginSdkInfo
{
    /// <summary>当前契约版本。</summary>
    public const string Version = "1.3.0";

    /// <summary>当前契约主版本，用于 manifest.ApiVersion 兼容性判断。</summary>
    public const int MajorVersion = 1;

    /// <summary>
    /// 判断 manifest 声明的 ApiVersion 是否可被当前宿主加载。
    /// 未声明时按兼容处理（老插件沿用 1.0 契约）。
    /// </summary>
    public static bool IsCompatible(string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            return true;
        }

        var majorPart = apiVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(majorPart, out var major) && major <= MajorVersion;
    }
}
