using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 新版 IAS 系统（HD2IntegratedArmorSystem）的安装位置与 Runtime 发现服务。
/// 模型组件不再直接部署：armor 配套由 IasCompanionService 调用上游生成器生成；
/// 本服务只负责定位安装位置、发现 Runtime 目录和枚举模组库中的 IAS 组件（展示用）。
/// </summary>
public class IasSystemService : IIasSystemService
{
    private const string SystemFolder = "HD2IntegratedArmorSystem";
    private const string RuntimeFolder = "Runtime";
    private const string ExternalRuntimeFolder = "ExternalRuntime";
    private const string RigTargetPrefix = "bin/HD2IndependentRig/";

    private readonly ModService _mods;
    private readonly SettingsService _settings;

    public IasSystemService(ModService mods, SettingsService settings)
    {
        _mods = mods;
        _settings = settings;
    }

    public Task<string?> GetInstallLocationAsync(CancellationToken token = default)
        => _settings.GetSettingAsync<string?>(SettingKeys.IasInstallLocation, null);

    public Task SaveInstallLocationAsync(string location, CancellationToken token = default)
        => _settings.SetSettingsAsync(new Dictionary<string, object> { [SettingKeys.IasInstallLocation] = location });

    public Task SaveActiveRuntimeIdAsync(string? runtimeId, CancellationToken token = default)
        => _settings.SetSettingsAsync(new Dictionary<string, object> { [SettingKeys.IasActiveRuntimeId] = runtimeId ?? "" });

    public Task<string?> GetActiveRuntimeIdAsync(CancellationToken token = default)
        => _settings.GetSettingAsync<string?>(SettingKeys.IasActiveRuntimeId, null);

    public async Task<IReadOnlyList<string>> DiscoverRuntimeIdsAsync(string location, CancellationToken token = default)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        var runtimeRoot = Path.Combine(ResolveSystemRoot(location), SystemFolder, RuntimeFolder);
        if (!Directory.Exists(runtimeRoot)) return [];
        return Directory.EnumerateDirectories(runtimeRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<IasModelComponent>> GetModelComponentsAsync(CancellationToken token = default)
    {
        var components = new List<IasModelComponent>();
        foreach (var mod in await _mods.GetAllModsAsync())
        {
            token.ThrowIfCancellationRequested();
            if (mod.WorkflowType != Hd2IrIntegrationService.Workflow) continue;
            var files = new List<IasModelFile>();
            foreach (var entry in await Hd2IrIntegrationService.GetNativeFilesAsync(mod, optionsJson: null, token))
            {
                var relative = entry.FileName.Replace('\\', '/');
                if (!relative.StartsWith(RigTargetPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(new IasModelFile(entry.SourcePath!, relative[RigTargetPrefix.Length..]));
            }
            if (files.Count > 0) components.Add(new IasModelComponent(mod.Id, mod.Name, files));
        }
        return components;
    }

    public async Task<string?> GetRuntimeRootAsync(CancellationToken token = default)
    {
        var location = await GetInstallLocationAsync(token);
        if (string.IsNullOrWhiteSpace(location)) return null;
        var ids = await DiscoverRuntimeIdsAsync(location, token);
        if (ids.Count == 0) return null;
        var saved = await GetActiveRuntimeIdAsync(token);
        var runtimeId = saved != null && ids.Contains(saved, StringComparer.OrdinalIgnoreCase) ? saved : ids[0];
        return Path.Combine(ResolveSystemRoot(location), SystemFolder, RuntimeFolder, runtimeId);
    }

    public async Task<string?> GetExternalRuntimeRootAsync(CancellationToken token = default)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        var location = await GetInstallLocationAsync(token);
        if (string.IsNullOrWhiteSpace(location)) return null;
        return Path.Combine(ResolveSystemRoot(location), SystemFolder, ExternalRuntimeFolder);
    }

    private static string NormalizeLocation(string location)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Trim()));

    /// <summary>
    /// 解析系统根目录（HD2IntegratedArmorSystem 的父目录）。允许用户直接选中
    /// HD2IntegratedArmorSystem 目录本身：其下直接有 Runtime 时自动上溯一级。
    /// </summary>
    private static string ResolveSystemRoot(string location)
    {
        var root = NormalizeLocation(location);
        if (Directory.Exists(Path.Combine(root, SystemFolder, RuntimeFolder))) return root;
        if (Path.GetFileName(root).Equals(SystemFolder, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.Combine(root, RuntimeFolder)))
            return Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(root)!);
        return root;
    }
}
