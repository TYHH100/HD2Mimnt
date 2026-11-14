using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 按设置中的部署方式把库文件放进 HellForge 的 <c>mods\</c>：Copy 走流式拷贝，HardLink/SymbolicLink 调用
/// <see cref="IDeploymentMethod"/>；单个文件建链失败时回退为复制并只记录一次警告。
/// 只做文件级链接：HellForge 删除模组时校验路径上的每一级（LocalInstall.NoLinks），目录链接会被拒绝。
/// </summary>
internal sealed class HellForgeModFilePlacer
{
    private readonly IDeploymentMethod? _linkMethod;
    private readonly Action<string>? _warn;
    private bool _fallbackReported;

    /// <param name="linkMethod">null 或 Copy 实现表示文件复制。</param>
    /// <param name="warn">建链失败后的提示出口（只调用一次）。</param>
    public HellForgeModFilePlacer(IDeploymentMethod? linkMethod, Action<string>? warn = null)
    {
        _linkMethod = linkMethod;
        if (_linkMethod != null && Enum.TryParse<DeploymentMethod>(_linkMethod.Name, out var parsed)
            && parsed == DeploymentMethod.Copy)
            _linkMethod = null;
        _warn = warn;
    }

    /// <summary>实际写入方式的名字，用于同步结果文案。</summary>
    public string MethodName => _linkMethod?.Name ?? nameof(DeploymentMethod.Copy);

    /// <summary>是否出现过“本应建链但改为复制”的回退。</summary>
    public bool UsedFallback { get; private set; }

    public async Task PlaceAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (_linkMethod != null)
        {
            if (await _linkMethod.DeployFileAsync(sourcePath, destinationPath, null, cancellationToken)) return;
            UsedFallback = true;
            if (!_fallbackReported)
            {
                _fallbackReported = true;
                _warn?.Invoke($"{MethodName} 写入失败，已改为文件复制");
            }
        }
        await HellForgeModExporter.CopyFileAsync(sourcePath, destinationPath, cancellationToken);
    }

    /// <summary>
    /// 解析设置中的部署方式并在写入前做兼容性预检；不可用时回退为文件复制并给出警告。
    /// </summary>
    public static async Task<HellForgeModFilePlacer> ResolveAsync(
        SettingsService settings,
        IReadOnlyDictionary<DeploymentMethod, IDeploymentMethod> methods,
        string sourceDirectory,
        string targetDirectory,
        Action<string>? warn,
        CancellationToken cancellationToken)
    {
        var saved = await settings.GetSettingAsync(SettingKeys.DefaultDeploymentMethod, nameof(DeploymentMethod.Copy));
        if (!Enum.TryParse<DeploymentMethod>(saved, true, out var method) || !Enum.IsDefined(method)
            || method == DeploymentMethod.Copy || !methods.TryGetValue(method, out var implementation))
            return new HellForgeModFilePlacer(null, warn);

        var compatibility = await implementation.CheckCompatibilityAsync(sourceDirectory, targetDirectory, cancellationToken);
        if (compatibility.CanDeploy) return new HellForgeModFilePlacer(implementation, warn);

        warn?.Invoke($"{method} 部署不可用（{compatibility.ErrorMessage}），已改为文件复制");
        return new HellForgeModFilePlacer(null, warn);
    }
}
