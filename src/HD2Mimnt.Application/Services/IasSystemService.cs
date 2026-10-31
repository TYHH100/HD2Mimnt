using System.IO;
using System.Text.Json;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 新版 IAS 系统（HD2IntegratedArmorSystem）模型部署。
/// 模型组件从模组库部署到“&lt;安装位置&gt;\HD2IntegratedArmorSystem\Runtime\&lt;已有随机ID&gt;\armor\rig”，
/// 独立于配置文件部署体系；部署方式跟随默认部署设置，硬链接跨盘自动降级为文件复制。
/// </summary>
public class IasSystemService : IIasSystemService
{
    private const string SystemFolder = "HD2IntegratedArmorSystem";
    private const string RuntimeFolder = "Runtime";
    private const string LegacyTargetPrefix = "bin/HD2IndependentRig/";

    private readonly ModService _mods;
    private readonly SettingsService _settings;
    private readonly IReadOnlyDictionary<DeploymentMethod, IDeploymentMethod> _methods;
    private readonly string _storeRoot;
    private readonly string _modsStoragePath;

    public IasSystemService(ModService mods, SettingsService settings,
        IEnumerable<IDeploymentMethod> methods, string storeRoot, string modsStoragePath)
    {
        _mods = mods;
        _settings = settings;
        _methods = methods
            .Select(method => (Method: method, Parsed: Enum.TryParse<DeploymentMethod>(method.Name, true, out var parsed)
                    && Enum.IsDefined(parsed) ? parsed : (DeploymentMethod?)null))
            .Where(item => item.Parsed.HasValue)
            .GroupBy(item => item.Parsed!.Value)
            .ToDictionary(group => group.Key, group => group.First().Method);
        _storeRoot = storeRoot;
        _modsStoragePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsStoragePath));
    }

    public Task<string?> GetInstallLocationAsync(CancellationToken token = default)
        => _settings.GetSettingAsync<string?>(SettingKeys.IasInstallLocation, null);

    public Task SaveInstallLocationAsync(string location, CancellationToken token = default)
        => _settings.SetSettingsAsync(new Dictionary<string, object> { [SettingKeys.IasInstallLocation] = location });

    public Task<bool> HasDeploymentAsync(CancellationToken token = default) => Task.FromResult(File.Exists(ManifestPath));

    public async Task<string?> GetRigTargetRootAsync(CancellationToken token = default)
    {
        var location = await GetInstallLocationAsync(token);
        if (string.IsNullOrWhiteSpace(location)) return null;
        var ids = await DiscoverRuntimeIdsAsync(location, token);
        if (ids.Count == 0) return null;
        var saved = await GetActiveRuntimeIdAsync(token);
        var runtimeId = saved != null && ids.Contains(saved, StringComparer.OrdinalIgnoreCase) ? saved : ids[0];
        return Path.Combine(ResolveSystemRoot(location), SystemFolder, RuntimeFolder, runtimeId, "armor", "rig");
    }

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
                if (!relative.StartsWith(LegacyTargetPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(new IasModelFile(entry.SourcePath!, relative[LegacyTargetPrefix.Length..]));
            }
            if (files.Count > 0) components.Add(new IasModelComponent(mod.Id, mod.Name, files));
        }
        return components;
    }

    public async Task<IasDeployResult> DeployAsync(string location, string runtimeId,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(location)) throw new InvalidOperationException("请先填写 HD2IAS 系统安装位置。");
        if (string.IsNullOrWhiteSpace(runtimeId)) throw new InvalidOperationException("请先选择 Runtime 目录。");
        var root = Path.Combine(ResolveSystemRoot(location), SystemFolder, RuntimeFolder, runtimeId);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Runtime 目录不存在：{root}");
        var rigRoot = Path.Combine(root, "armor", "rig");
        var components = await GetModelComponentsAsync(token);
        if (components.Count == 0) throw new InvalidOperationException("模组库中没有可部署的 IAS 模型组件。");

        var requested = await _settings.GetSettingAsync(SettingKeys.DefaultDeploymentMethod,
            DeploymentMethod.Copy.ToString());
        var requestedMethod = Enum.TryParse<DeploymentMethod>(requested, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed) ? parsed : DeploymentMethod.Copy;
        var usedMethod = ResolveMethod(requestedMethod, _modsStoragePath, rigRoot);
        var method = _methods.GetValueOrDefault(usedMethod)
            ?? throw new InvalidOperationException($"部署方式未注册：{usedMethod}");
        // 硬链接还可能因权限、ReFS 等原因不可用，运行时兜底检查一次。
        if (usedMethod == DeploymentMethod.HardLink)
        {
            var (canDeploy, error) = await method.CheckCompatibilityAsync(_modsStoragePath, rigRoot, token);
            if (!canDeploy)
            {
                usedMethod = DeploymentMethod.Copy;
                method = _methods.GetValueOrDefault(DeploymentMethod.Copy)
                    ?? throw new InvalidOperationException("文件复制部署方式未注册。");
                progress?.Report($"硬链接不可用（{error}），改用文件复制。");
            }
        }

        var manifest = new DeploymentManifest(runtimeId, root, usedMethod.ToString(), []);
        var deployed = 0; var skipped = 0;
        foreach (var component in components)
        foreach (var file in component.Files)
        {
            token.ThrowIfCancellationRequested();
            var target = ResolveWithin(rigRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (await method.IsUpToDateAsync(file.SourcePath, target, token)) { skipped++; continue; }
            progress?.Report($"{component.ModName} · {file.RelativePath}");
            if (!await method.DeployFileAsync(file.SourcePath, target, progress, token))
                throw new IOException($"部署文件失败：{file.RelativePath}");
            manifest.Files.Add(new ManifestFile(file.SourcePath, target, file.RelativePath));
            deployed++;
        }

        Directory.CreateDirectory(_storeRoot);
        await WriteJsonAsync(ManifestPath, manifest, token);
        return new IasDeployResult(rigRoot, requestedMethod.ToString(), usedMethod.ToString(), deployed, skipped);
    }

    public async Task<IasUninstallResult> UninstallAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        var manifestPath = ManifestPath;
        if (!File.Exists(manifestPath)) return new IasUninstallResult(0, []);
        var manifest = JsonSerializer.Deserialize<DeploymentManifest>(await File.ReadAllTextAsync(manifestPath, token))
            ?? throw new InvalidDataException("IAS 系统部署记录无效，请手动清理 armor\\rig 目录。");
        var missing = new List<string>();
        var removed = 0;
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(file.TargetPath)) { missing.Add(file.RelativePath); continue; }
            progress?.Report(file.RelativePath);
            if (File.GetAttributes(file.TargetPath).HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file.TargetPath, FileAttributes.Normal);
            File.Delete(file.TargetPath);
            removed++;
        }
        RemoveEmptyDirectories(Path.Combine(manifest.RuntimeRoot, "armor", "rig"), manifest.RuntimeRoot);
        File.Delete(manifestPath);
        return new IasUninstallResult(removed, missing);
    }

    /// <summary>部署方式决策：硬链接要求目标与模组库同盘符，跨盘自动降级为文件复制。</summary>
    public static DeploymentMethod ResolveMethod(DeploymentMethod requested, string sourceRootPath, string targetRootPath)
    {
        if (requested != DeploymentMethod.HardLink) return requested;
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourceRootPath));
        var targetRoot = Path.GetPathRoot(Path.GetFullPath(targetRootPath));
        return string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase)
            ? DeploymentMethod.HardLink : DeploymentMethod.Copy;
    }

    private string ManifestPath => Path.Combine(_storeRoot, "deployment.json");

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

    private static string ResolveWithin(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("IAS 模型映射超出 rig 目录。");
        return fullPath;
    }

    private static void RemoveEmptyDirectories(string start, string stopAt)
    {
        var current = start;
        while (!string.Equals(Path.TrimEndingDirectorySeparator(current),
                   Path.TrimEndingDirectorySeparator(stopAt), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any()) return;
                Directory.Delete(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), token);
        token.ThrowIfCancellationRequested();
        File.Move(temporary, path, true);
    }

    private sealed record DeploymentManifest(string RuntimeId, string RuntimeRoot, string Method,
        List<ManifestFile> Files);

    private sealed record ManifestFile(string SourcePath, string TargetPath, string RelativePath);
}
