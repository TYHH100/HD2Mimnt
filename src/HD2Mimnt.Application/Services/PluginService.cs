using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IO;
using System.Collections.Concurrent;
using System.Text.Json;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Service for managing plugin lifecycle and permissions
/// </summary>
public class PluginService : IPluginService
{
    private readonly IPluginRepository _pluginRepository;
    private readonly IPluginPermissionRepository _pluginPermissionRepository;
    private readonly IPluginExecutionRepository _pluginExecutionRepository;
    private readonly IPluginHostManager _pluginHostManager;
    private readonly IPluginGrantSession _grantSession;
    private readonly IPluginPermissionChecker _permissionChecker;
    private readonly string? _installedPluginsPath;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _commandGates = new(StringComparer.OrdinalIgnoreCase);

    public PluginService(
        IPluginRepository pluginRepository,
        IPluginPermissionRepository pluginPermissionRepository,
        IPluginExecutionRepository pluginExecutionRepository,
        IPluginHostManager pluginHostManager,
        IPluginGrantSession? grantSession = null,
        IPluginPermissionChecker? permissionChecker = null,
        string? installedPluginsPath = null)
    {
        _pluginRepository = pluginRepository;
        _pluginPermissionRepository = pluginPermissionRepository;
        _pluginExecutionRepository = pluginExecutionRepository;
        _pluginHostManager = pluginHostManager;
        _grantSession = grantSession ?? new PluginGrantSession();
        _installedPluginsPath = string.IsNullOrWhiteSpace(installedPluginsPath) ? null : Path.GetFullPath(installedPluginsPath);
        // 权限判定与宿主启动快照共用同一实现；未注入时按现有依赖就地构造
        _permissionChecker = permissionChecker ?? new PluginPermissionChecker(pluginRepository, pluginPermissionRepository, _grantSession);
    }

    public Task<PluginOperationResult> InstallPluginAsync(string manifestPath, CancellationToken cancellationToken = default)
        => InstallPluginAsync(manifestPath, null, cancellationToken);

    public async Task<PluginOperationResult> InstallPluginAsync(
        string manifestPath,
        PluginInstallConsent? consent,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return PluginOperationResult.ErrorResult("Manifest file not found", manifestPath);
            }

            var validationResult = await ValidateManifestAsync(manifestPath, cancellationToken);
            if (!validationResult.IsValid)
            {
                return PluginOperationResult.ErrorResult(
                    string.Join(", ", validationResult.Errors),
                    "Manifest validation failed");
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = PluginSDK.PluginManifestJson.Deserialize(manifestJson);
            if (manifest == null)
            {
                return PluginOperationResult.ErrorResult("Failed to parse manifest");
            }

            if (await _pluginRepository.ExistsAsync(manifest.Id, cancellationToken))
            {
                return PluginOperationResult.ErrorResult("Plugin already installed", manifest.Id);
            }

            var sourceManifestPath = Path.GetFullPath(manifestPath);
            var sourceDirectory = Path.GetDirectoryName(sourceManifestPath);
            if (string.IsNullOrEmpty(sourceDirectory))
            {
                return PluginOperationResult.ErrorResult("Invalid manifest path");
            }

            string? stagingDirectory = null;
            var installedDirectory = sourceDirectory;
            if (_installedPluginsPath is not null)
            {
                if (IsSameOrInside(sourceDirectory, _installedPluginsPath))
                {
                    return PluginOperationResult.ErrorResult("插件源目录不能包含已安装插件目录。", sourceDirectory);
                }

                installedDirectory = Path.Combine(_installedPluginsPath, manifest.Id);
                if (Directory.Exists(installedDirectory) || File.Exists(installedDirectory))
                {
                    return PluginOperationResult.ErrorResult("插件安装目录已存在，请先卸载旧插件。", installedDirectory);
                }

                Directory.CreateDirectory(_installedPluginsPath);
                stagingDirectory = Path.Combine(_installedPluginsPath, $".installing-{manifest.Id}-{Guid.NewGuid():N}");
                try
                {
                    await CopyPluginDirectoryAsync(sourceDirectory, stagingDirectory, cancellationToken);
                    Directory.Move(stagingDirectory, installedDirectory);
                    stagingDirectory = null;
                }
                catch
                {
                    if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
                    throw;
                }
            }

            manifestPath = Path.Combine(installedDirectory, Path.GetFileName(sourceManifestPath));

            // 入口始终从隔离副本解析，避免数据库继续引用安装源目录。
            string entryPath;
            if (manifest.IsWebPlugin)
            {
                entryPath = manifestPath;
            }
            else
            {
                try
                {
                    entryPath = PathGuard.ResolveInsideRoot(installedDirectory,
                        Path.Combine(installedDirectory, manifest.EntryFile ?? string.Empty),
                        "插件清单 EntryFile 越出插件目录，已拒绝安装。");
                }
                catch (InvalidDataException ex)
                {
                    if (_installedPluginsPath is not null) Directory.Delete(installedDirectory, recursive: true);
                    return PluginOperationResult.ErrorResult(ex.Message, installedDirectory);
                }
                if (!File.Exists(entryPath))
                {
                    if (_installedPluginsPath is not null) Directory.Delete(installedDirectory, recursive: true);
                    return PluginOperationResult.ErrorResult(
                        manifest.IsScriptPlugin ? "Plugin script not found" : "Plugin assembly not found",
                        entryPath);
                }
            }

            var recordAdded = false;
            try
            {
                var plugin = new Plugin
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Author = manifest.Author,
                    Description = manifest.Description,
                    ManifestPath = manifestPath,
                    // 对脚本型插件，这里记录脚本路径；对纯网页插件记录 manifest 路径。
                    AssemblyPath = entryPath,
                    // 完整性锚点：安装时固定入口文件、清单与混合 DLL 内容，启动前比对防替换。
                    // 命令权限门控运行时重读清单（RequiredPermission），清单不在锚点内则门控可被摘除。
                    AssemblySha256 = manifest.IsWebPlugin ? null : await ComputeFileSha256Async(entryPath, cancellationToken),
                    ManifestSha256 = await ComputeFileSha256Async(manifestPath, cancellationToken),
                    HybridAssemblySha256 = await ComputeHybridAssemblySha256Async(manifest, installedDirectory, cancellationToken),
                    InstalledAt = DateTime.UtcNow,
                    Enabled = true,
                    PermissionMode = manifest.PermissionMode.ToString()
                };

                await _pluginRepository.AddAsync(plugin, cancellationToken);
                recordAdded = true;

                var selected = consent?.GrantedPermissions is { } granted
                    ? new HashSet<string>(granted, StringComparer.OrdinalIgnoreCase)
                    : null;

                foreach (var permission in manifest.RequestedPermissions)
                {
                    var shouldGrant = selected is not null
                        // 用户在对话框中的勾选即最终结果：勾了才授予
                        ? selected.Contains(permission)
                        // 未走对话框（CLI/测试）时沿用默认策略：
                        // 敏感能力（AI 代理）必须由用户显式授权，任何权限模式下都不随安装自动放开
                        : manifest.PermissionMode == PluginSDK.PluginPermissionMode.Sandbox
                          && !IsSensitivePermission(permission);

                    var persisted = shouldGrant && (consent?.PersistGrants ?? true);

                    var pluginPermission = new PluginPermission
                    {
                        Id = Guid.NewGuid().ToString(),
                        PluginId = manifest.Id,
                        Permission = permission,
                        Granted = persisted,
                        GrantedAt = persisted ? DateTime.UtcNow : null
                    };

                    await _pluginPermissionRepository.AddAsync(pluginPermission, cancellationToken);
                }

                if (consent is { PersistGrants: false })
                {
                    // “仅本次允许”：勾选的权限只留在内存，进程重启后自动失效
                    _grantSession.Grant(manifest.Id, (IEnumerable<string>?)selected ?? Array.Empty<string>());
                }

                return PluginOperationResult.SuccessResult("Plugin installed successfully", plugin);
            }
            catch
            {
                if (recordAdded)
                {
                    await _pluginPermissionRepository.DeleteByPluginIdAsync(manifest.Id, CancellationToken.None);
                    await _pluginRepository.DeleteAsync(manifest.Id, CancellationToken.None);
                }
                if (_installedPluginsPath is not null)
                {
                    Directory.Delete(installedDirectory, recursive: true);
                }
                throw;
            }
            finally
            {
                if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            return PluginOperationResult.ErrorResult(ex.Message, "Installation failed");
        }
    }

    private static bool IsSameOrInside(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || PathGuard.IsInsideRoot(fullRoot, fullPath);
    }

    private static async Task CopyPluginDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };

        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(source, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(source, sourcePath);
            var destinationPath = PathGuard.ResolveInsideRoot(destination, Path.Combine(destination, relativePath));
            var attributes = File.GetAttributes(sourcePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"插件包含链接或重解析点，已拒绝安装：{relativePath}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    /// <summary>
    /// 启动插件宿主进程。启动前校验程序集 SHA-256 与安装记录一致，
    /// 不一致即拒绝（防止安装后被替换的程序集在完整权限下运行）；
    /// 旧数据无哈希时现场补录（首次信任当前文件）。
    /// </summary>
    public async Task<PluginOperationResult> StartPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
        if (plugin == null)
        {
            return PluginOperationResult.ErrorResult("Plugin not found", pluginId);
        }
        var manifest = await ReadManifestAsync(plugin.ManifestPath, cancellationToken);
        var isWebPlugin = manifest?.IsWebPlugin == true;
        if (!isWebPlugin)
        {
            if (!File.Exists(plugin.AssemblyPath))
            {
                return PluginOperationResult.ErrorResult("插件入口文件不存在", plugin.AssemblyPath);
            }

            var currentSha = await ComputeFileSha256Async(plugin.AssemblyPath, cancellationToken);
            if (plugin.AssemblySha256 is { } recorded)
            {
                if (!string.Equals(recorded, currentSha, StringComparison.OrdinalIgnoreCase))
                {
                    return PluginOperationResult.ErrorResult(
                        "插件入口文件与安装时不一致，已拒绝启动；请重新安装或更新插件。", pluginId);
                }
            }
            else
            {
                plugin.AssemblySha256 = currentSha;
                await _pluginRepository.UpdateAsync(plugin, cancellationToken);
            }
        }

        // 清单锚点：权限门控（RequiredPermission）、命令声明、脚本/DLL 路径全部来自清单内容，
        // 清单被改即拒绝启动（旧数据无哈希时首查信任并补录）
        if (!File.Exists(plugin.ManifestPath))
        {
            return PluginOperationResult.ErrorResult("插件清单文件不存在", plugin.ManifestPath);
        }
        var currentManifestSha = await ComputeFileSha256Async(plugin.ManifestPath, cancellationToken);
        if (plugin.ManifestSha256 is { } manifestRecorded)
        {
            if (!string.Equals(manifestRecorded, currentManifestSha, StringComparison.OrdinalIgnoreCase))
            {
                return PluginOperationResult.ErrorResult(
                    "插件清单与安装时不一致，已拒绝启动；请重新安装或更新插件。", pluginId);
            }
        }
        else
        {
            plugin.ManifestSha256 = currentManifestSha;
            await _pluginRepository.UpdateAsync(plugin, cancellationToken);
        }

        if (isWebPlugin)
        {
            return PluginOperationResult.SuccessResult("纯网页插件已启用，无需启动宿主进程。");
        }

        // 混合插件 DLL 锚点：路径取自已被锚定的清单，哈希与安装时不符即拒绝
        if (!await VerifyHybridAssemblyAsync(plugin, cancellationToken))
        {
            return PluginOperationResult.ErrorResult(
                "混合插件命令库与安装时不一致，已拒绝启动；请重新安装或更新插件。", pluginId);
        }

        return await _pluginHostManager.StartPluginAsync(plugin.Id, plugin.AssemblyPath, plugin.ManifestPath, cancellationToken);
    }

    /// <summary>
    /// 混合插件（kind=lua 且填 assembly）的命令库 DLL 锚点校验。
    /// 非混合插件恒通过；清单解析失败按不一致处理（清单锚点先行，此为防御深度）。
    /// </summary>
    private async Task<bool> VerifyHybridAssemblyAsync(Plugin plugin, CancellationToken cancellationToken)
    {
        string? currentSha;
        try
        {
            currentSha = await ReadHybridAssemblySha256Async(plugin, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return false;
        }

        if (currentSha is null) return true; // 非混合插件
        if (plugin.HybridAssemblySha256 is { } recorded)
        {
            return string.Equals(recorded, currentSha, StringComparison.OrdinalIgnoreCase);
        }

        // 旧数据无哈希：首查信任并补录
        plugin.HybridAssemblySha256 = currentSha;
        await _pluginRepository.UpdateAsync(plugin, cancellationToken);
        return true;
    }

    /// <summary>按清单 Assembly 字段解析命令库 DLL 并计算哈希；非混合插件返回 null。</summary>
    private async Task<string?> ReadHybridAssemblySha256Async(Plugin plugin, CancellationToken cancellationToken)
    {
        var manifestJson = await File.ReadAllTextAsync(plugin.ManifestPath, cancellationToken);
        var manifest = PluginSDK.PluginManifestJson.Deserialize(manifestJson);
        if (manifest?.IsHybrid != true || string.IsNullOrWhiteSpace(manifest.Assembly)) return null;

        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(plugin.ManifestPath))!;
        var assemblyPath = PathGuard.ResolveInsideRoot(manifestDirectory,
            Path.Combine(manifestDirectory, manifest.Assembly),
            "插件清单 Assembly 越出插件目录，已拒绝启动。");
        if (!File.Exists(assemblyPath)) return null;
        return await ComputeFileSha256Async(assemblyPath, cancellationToken);
    }

    /// <summary>安装时计算混合插件命令库 DLL 的锚点哈希；非混合或 DLL 缺失返回 null。</summary>
    private static async Task<string?> ComputeHybridAssemblySha256Async(
        PluginSDK.PluginManifest manifest, string manifestDirectory, CancellationToken cancellationToken)
    {
        if (!manifest.IsHybrid || string.IsNullOrWhiteSpace(manifest.Assembly)) return null;
        var assemblyPath = PathGuard.ResolveInsideRoot(manifestDirectory,
            Path.Combine(manifestDirectory, manifest.Assembly),
            "插件清单 Assembly 越出插件目录，已拒绝安装。");
        if (!File.Exists(assemblyPath)) return null;
        return await ComputeFileSha256Async(assemblyPath, cancellationToken);
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    public async Task<PluginOperationResult> UninstallPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        try
        {
            var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
            if (plugin == null)
            {
                return PluginOperationResult.ErrorResult("Plugin not found", pluginId);
            }

            await _pluginHostManager.StopPluginAsync(pluginId, cancellationToken);

            await _pluginPermissionRepository.DeleteByPluginIdAsync(pluginId, cancellationToken);

            await _pluginRepository.DeleteAsync(pluginId, cancellationToken);

            _grantSession.Forget(pluginId);

            if (_installedPluginsPath is not null && PluginSDK.PluginId.IsValid(pluginId))
            {
                var installedDirectory = Path.Combine(_installedPluginsPath, pluginId);
                var managedManifest = Path.Combine(installedDirectory, Path.GetFileName(plugin.ManifestPath));
                if (IsSameOrInside(installedDirectory, plugin.ManifestPath)
                    && plugin.ManifestPath.Equals(managedManifest, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(installedDirectory))
                {
                    Directory.Delete(installedDirectory, recursive: true);
                }
            }

            return PluginOperationResult.SuccessResult("Plugin uninstalled successfully");
        }
        catch (Exception ex)
        {
            return PluginOperationResult.ErrorResult(ex.Message, "Uninstallation failed");
        }
    }

    public async Task<PluginOperationResult> EnablePluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        try
        {
            var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
            if (plugin == null)
            {
                return PluginOperationResult.ErrorResult("Plugin not found", pluginId);
            }

            if (plugin.Enabled)
            {
                return PluginOperationResult.SuccessResult("Plugin already enabled");
            }

            plugin.Enabled = true;
            await _pluginRepository.UpdateAsync(plugin, cancellationToken);

            return PluginOperationResult.SuccessResult("Plugin enabled successfully");
        }
        catch (Exception ex)
        {
            return PluginOperationResult.ErrorResult(ex.Message, "Enable failed");
        }
    }

    public async Task<PluginOperationResult> DisablePluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        try
        {
            var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
            if (plugin == null)
            {
                return PluginOperationResult.ErrorResult("Plugin not found", pluginId);
            }

            if (!plugin.Enabled)
            {
                return PluginOperationResult.SuccessResult("Plugin already disabled");
            }

            await _pluginHostManager.StopPluginAsync(pluginId, cancellationToken);

            plugin.Enabled = false;
            await _pluginRepository.UpdateAsync(plugin, cancellationToken);

            // 停用后本次会话的临时授权一并失效，重新启用需要重新授权
            _grantSession.Forget(pluginId);

            return PluginOperationResult.SuccessResult("Plugin disabled successfully");
        }
        catch (Exception ex)
        {
            return PluginOperationResult.ErrorResult(ex.Message, "Disable failed");
        }
    }

    public async Task<Plugin?> GetPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        return await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
    }

    public async Task<List<Plugin>> GetAllPluginsAsync(CancellationToken cancellationToken = default)
    {
        return await _pluginRepository.GetAllAsync(cancellationToken);
    }

    public async Task<List<Plugin>> GetEnabledPluginsAsync(CancellationToken cancellationToken = default)
    {
        return await _pluginRepository.GetEnabledAsync(cancellationToken);
    }

    public async Task<PluginValidationResult> ValidateManifestAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var result = new PluginValidationResult();

        try
        {
            if (!File.Exists(manifestPath))
            {
                result.Errors.Add("Manifest file not found");
                return result;
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = PluginSDK.PluginManifestJson.Deserialize(manifestJson);

            if (manifest == null)
            {
                result.Errors.Add("Failed to parse manifest JSON");
                return result;
            }

            if (string.IsNullOrWhiteSpace(manifest.Id))
            {
                result.Errors.Add("Plugin Id is required");
            }
            else if (!PluginSDK.PluginId.IsValid(manifest.Id))
            {
                // Id 会拼进宿主数据目录路径，字符集不安全（含路径分隔符/遍历序列）直接拒绝安装
                result.Errors.Add($"Invalid plugin Id (allowed: ASCII letters, digits, '.', '_', '-'): {manifest.Id}");
            }

            if (string.IsNullOrWhiteSpace(manifest.Name))
            {
                result.Errors.Add("Plugin Name is required");
            }

            if (string.IsNullOrWhiteSpace(manifest.Version))
            {
                result.Errors.Add("Plugin Version is required");
            }
            else if (!IsValidVersion(manifest.Version))
            {
                result.Errors.Add($"Invalid version format: {manifest.Version}");
            }

            var isScript = manifest.IsScriptPlugin;

            if (manifest.IsWebPlugin)
            {
                if (manifest.Commands.Count > 0)
                {
                    result.Errors.Add("纯网页插件不能声明 commands；需要命令运行时请使用 lua 或 dotnet。");
                }
                if (!manifest.Ui.Pages.Any(page => PluginSDK.PluginUiPageKinds.IsWebView2(page.Kind)))
                {
                    result.Errors.Add("纯网页插件至少需要一个 kind 为 webView2 的页面。");
                }
            }
            else if (isScript)
            {
                if (string.IsNullOrWhiteSpace(manifest.Script))
                {
                    result.Errors.Add("Lua 插件必须声明 Script（入口脚本文件名，如 main.lua）");
                }
                else if (!manifest.Script.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"Lua 插件入口脚本必须是 .lua 文件：{manifest.Script}");
                }

                // 混合插件：DLL 可选，声明了就必须存在
                if (!string.IsNullOrWhiteSpace(manifest.Assembly)
                    && !File.Exists(Path.Combine(Path.GetDirectoryName(manifestPath) ?? ".", manifest.Assembly)))
                {
                    result.Errors.Add($"混合插件声明的程序集不存在：{manifest.Assembly}");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(manifest.Assembly))
                {
                    result.Errors.Add("Plugin Assembly is required");
                }

                if (string.IsNullOrWhiteSpace(manifest.EntryType))
                {
                    result.Errors.Add("Plugin EntryType is required");
                }
            }

            // 契约版本不兼容时直接拒绝安装：避免宿主加载到无法工作的插件后静默失败
            if (!PluginSDK.PluginSdkInfo.IsCompatible(manifest.ApiVersion))
            {
                result.Errors.Add(
                    $"插件要求 SDK 版本 {manifest.ApiVersion}，当前宿主为 {PluginSDK.PluginSdkInfo.Version}（仅兼容主版本 {PluginSDK.PluginSdkInfo.MajorVersion}）");
            }

            if (manifest.PermissionMode == PluginSDK.PluginPermissionMode.FullAccess)
            {
                result.Warnings.Add("Plugin requests Full Access mode - requires user approval");
            }

            if (manifest.RequestedPermissions.Count > 0)
            {
                result.Warnings.Add($"Plugin requests {manifest.RequestedPermissions.Count} permissions");
            }

            ValidateUiContributions(manifest.Ui, Path.GetDirectoryName(manifestPath) ?? ".", result);

            var manifestDirectory = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(manifestDirectory) && !string.IsNullOrWhiteSpace(manifest.EntryFile))
            {
                var entryPath = Path.Combine(manifestDirectory, manifest.EntryFile);
                if (!File.Exists(entryPath))
                {
                    result.Errors.Add(manifest.IsScriptPlugin
                        ? $"插件入口脚本不存在：{manifest.EntryFile}"
                        : $"Plugin assembly not found: {manifest.EntryFile}");
                }
            }

            result.IsValid = result.Errors.Count == 0;
            return result;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Validation error: {ex.Message}");
            return result;
        }
    }

    public async Task<PluginManifestPreview?> GetManifestPreviewAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<PluginSDK.PluginManifest>(
                stream,
                PluginSDK.PluginManifestJson.Options,
                cancellationToken);
            if (manifest == null) return null;
            return new PluginManifestPreview
            {
                Name = manifest.Name,
                Version = manifest.Version,
                Author = manifest.Author,
                Description = manifest.Description,
                PermissionMode = manifest.PermissionMode.ToString(),
                RequestedPermissions = manifest.RequestedPermissions.ToArray(),
                Kind = PluginSDK.PluginKinds.Normalize(manifest.Kind)
            };
        }
        catch
        {
            return null;
        }
    }

    public Task<bool> CheckPermissionAsync(string pluginId, string permission, CancellationToken cancellationToken = default)
        => _permissionChecker.CheckAsync(pluginId, permission, cancellationToken);

    public async Task<PluginOperationResult> GrantPermissionAsync(string pluginId, string permission, CancellationToken cancellationToken = default)
    {
        try
        {
            var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
            if (plugin == null)
            {
                return PluginOperationResult.ErrorResult("Plugin not found", pluginId);
            }

            var permissions = await _pluginPermissionRepository.GetByPluginIdAsync(pluginId, cancellationToken);
            var existingPermission = permissions.FirstOrDefault(p => p.Permission == permission);

            if (existingPermission != null)
            {
                if (existingPermission.Granted)
                {
                    return PluginOperationResult.SuccessResult("Permission already granted");
                }

                existingPermission.Granted = true;
                existingPermission.GrantedAt = DateTime.UtcNow;
                await _pluginPermissionRepository.UpdateAsync(existingPermission, cancellationToken);
            }
            else
            {
                var newPermission = new PluginPermission
                {
                    Id = Guid.NewGuid().ToString(),
                    PluginId = pluginId,
                    Permission = permission,
                    Granted = true,
                    GrantedAt = DateTime.UtcNow
                };

                await _pluginPermissionRepository.AddAsync(newPermission, cancellationToken);
            }

            // 权限快照在宿主启动时下发：授予后停宿主，下次命令以新快照重启
            await _pluginHostManager.StopPluginAsync(pluginId, cancellationToken);

            return PluginOperationResult.SuccessResult("Permission granted successfully");
        }
        catch (Exception ex)
        {
            return PluginOperationResult.ErrorResult(ex.Message, "Grant permission failed");
        }
    }

    public async Task<PluginOperationResult> RevokePermissionAsync(string pluginId, string permission, CancellationToken cancellationToken = default)
    {
        try
        {
            var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
            if (plugin == null)
            {
                return PluginOperationResult.ErrorResult("Plugin not found", pluginId);
            }

            var permissions = await _pluginPermissionRepository.GetByPluginIdAsync(pluginId, cancellationToken);
            var existingPermission = permissions.FirstOrDefault(p => p.Permission == permission);

            if (existingPermission == null)
            {
                return PluginOperationResult.SuccessResult("Permission not found");
            }

            if (!existingPermission.Granted)
            {
                return PluginOperationResult.SuccessResult("Permission already revoked");
            }

            existingPermission.Granted = false;
            existingPermission.GrantedAt = null;
            await _pluginPermissionRepository.UpdateAsync(existingPermission, cancellationToken);

            // 长驻宿主持有启动时的权限快照：撤销后停宿主，下次命令以新快照重启
            await _pluginHostManager.StopPluginAsync(pluginId, cancellationToken);

            return PluginOperationResult.SuccessResult("Permission revoked successfully");
        }
        catch (Exception ex)
        {
            return PluginOperationResult.ErrorResult(ex.Message, "Revoke permission failed");
        }
    }

    public async Task<List<PluginPermission>> GetPluginPermissionsAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        return await _pluginPermissionRepository.GetByPluginIdAsync(pluginId, cancellationToken);
    }

    public async Task<List<PluginCommandInfo>> GetPluginCommandsAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
        return await ReadDeclaredCommandsAsync(plugin?.ManifestPath, cancellationToken);
    }

    /// <summary>
    /// 读取 manifest 中声明的命令；文件缺失或格式异常时按“无命令”处理，
    /// 不让单个损坏的清单阻断插件列表展示。
    /// </summary>
    private static async Task<List<PluginCommandInfo>> ReadDeclaredCommandsAsync(
        string? manifestPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return new List<PluginCommandInfo>();
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<PluginSDK.PluginManifest>(
                stream,
                PluginSDK.PluginManifestJson.Options,
                cancellationToken);

            return manifest?.Commands
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new PluginCommandInfo
                {
                    Name = c.Name,
                    DisplayName = string.IsNullOrWhiteSpace(c.DisplayName) ? c.Name : c.DisplayName,
                    Description = c.Description,
                    RequiredPermission = c.RequiredPermission,
                    Placement = PluginSDK.PluginCommandPlacements.Normalize(c.Placement)
                })
                .ToList() ?? new List<PluginCommandInfo>();
        }
        catch
        {
            return new List<PluginCommandInfo>();
        }
    }

    public async Task<List<PluginCommandContribution>> GetContributedCommandsAsync(
        string placement,
        CancellationToken cancellationToken = default)
    {
        var target = PluginSDK.PluginCommandPlacements.Normalize(placement);
        var contributions = new List<PluginCommandContribution>();

        var plugins = await _pluginRepository.GetEnabledAsync(cancellationToken);
        foreach (var plugin in plugins)
        {
            var commands = await ReadDeclaredCommandsAsync(plugin.ManifestPath, cancellationToken);
            foreach (var command in commands.Where(c => string.Equals(c.Placement, target, StringComparison.Ordinal)))
            {
                var granted = string.IsNullOrWhiteSpace(command.RequiredPermission)
                    || await CheckPermissionAsync(plugin.Id, command.RequiredPermission, cancellationToken);

                contributions.Add(new PluginCommandContribution
                {
                    PluginId = plugin.Id,
                    PluginName = plugin.Name,
                    Name = command.Name,
                    DisplayName = string.IsNullOrWhiteSpace(command.DisplayName) ? command.Name : command.DisplayName,
                    Description = command.Description,
                    RequiredPermission = command.RequiredPermission,
                    IsPermissionGranted = granted
                });
            }
        }

        return contributions;
    }

    public async Task<List<PluginUiBundle>> GetUiContributionsAsync(CancellationToken cancellationToken = default)
    {
        var bundles = new List<PluginUiBundle>();

        foreach (var plugin in await _pluginRepository.GetEnabledAsync(cancellationToken))
        {
            var manifest = await ReadManifestAsync(plugin.ManifestPath, cancellationToken);
            var ui = manifest?.Ui;
            if (manifest is null || ui is null)
            {
                continue;
            }

            if (ui.Pages.Count == 0 && ui.Dialogs.Count == 0 && ui.Menus.Count == 0 && ui.Themes.Count == 0)
            {
                continue;
            }

            var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(plugin.PermissionMode, "FullAccess", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var permission in manifest.RequestedPermissions)
                {
                    granted.Add(permission);
                }
            }
            else
            {
                foreach (var permission in await _pluginPermissionRepository.GetByPluginIdAsync(plugin.Id, cancellationToken))
                {
                    if (permission.Granted) granted.Add(permission.Permission);
                }
            }

            // 会话级授权（“仅本次允许”）不在数据库里，需要单独合并
            foreach (var permission in manifest.RequestedPermissions)
            {
                if (_grantSession.IsGranted(plugin.Id, permission)) granted.Add(permission);
            }

            bundles.Add(new PluginUiBundle
            {
                PluginId = plugin.Id,
                PluginName = plugin.Name,
                Ui = ui,
                ResourceRoot = Path.GetDirectoryName(plugin.ManifestPath) ?? string.Empty,
                GrantedPermissions = granted
            });
        }

        return bundles;
    }

    /// <summary>
    /// 校验声明式界面贡献：id 唯一、菜单必须能落到命令或浮窗、引用的浮窗必须存在。
    /// 这些错误会让插件装上却点不动，因此在安装阶段就拒绝。
    /// </summary>
    private static void ValidateUiContributions(
        PluginSDK.PluginUiContributions ui,
        string manifestDirectory,
        PluginValidationResult result)
    {
        var pageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in ui.Pages)
        {
            if (string.IsNullOrWhiteSpace(page.Id))
            {
                result.Errors.Add("ui.pages[].id 不能为空");
                continue;
            }

            if (!pageIds.Add(page.Id))
            {
                result.Errors.Add($"页面 id 重复：{page.Id}");
            }

            if (string.IsNullOrWhiteSpace(page.Title))
            {
                result.Warnings.Add($"页面 {page.Id} 缺少标题，将显示插件名称");
            }

            if (PluginSDK.PluginUiPageKinds.IsWebView2(page.Kind))
            {
                if (string.IsNullOrWhiteSpace(page.Entry))
                {
                    result.Errors.Add($"WebView2 页面 {page.Id} 必须声明 entry（相对插件目录的 HTML 文件）");
                }
                else if (Path.IsPathRooted(page.Entry) || page.Entry.Contains(':'))
                {
                    result.Errors.Add($"WebView2 页面入口必须是插件目录内的相对路径：{page.Entry}");
                }
                else if (!page.Entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                         && !page.Entry.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"WebView2 页面入口必须是 .html 或 .htm 文件：{page.Entry}");
                }
                else
                {
                    try
                    {
                        var entryPath = PathGuard.ResolveInsideRoot(
                            manifestDirectory,
                            Path.Combine(manifestDirectory, page.Entry),
                            "WebView2 页面入口越出插件目录。");
                        if (!File.Exists(entryPath))
                        {
                            result.Errors.Add($"WebView2 页面入口不存在：{page.Entry}");
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
                    {
                        result.Errors.Add(ex.Message);
                    }
                }
            }
            else if (!string.Equals(page.Kind, PluginSDK.PluginUiPageKinds.Declarative, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add($"页面 {page.Id} 的 kind 不受支持：{page.Kind}");
            }
            else if (page.Blocks.Count == 0)
            {
                result.Warnings.Add($"页面 {page.Id} 没有任何区块");
            }
        }

        var dialogIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dialog in ui.Dialogs)
        {
            if (string.IsNullOrWhiteSpace(dialog.Id))
            {
                result.Errors.Add("ui.dialogs[].id 不能为空");
                continue;
            }

            if (!dialogIds.Add(dialog.Id))
            {
                result.Errors.Add($"浮窗 id 重复：{dialog.Id}");
            }

            if (dialog.Blocks.Count == 0)
            {
                result.Warnings.Add($"浮窗 {dialog.Id} 没有任何区块");
            }
        }

        var menuIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var menu in ui.Menus)
        {
            if (string.IsNullOrWhiteSpace(menu.Id))
            {
                result.Errors.Add("ui.menus[].id 不能为空");
            }
            else if (!menuIds.Add(menu.Id))
            {
                result.Errors.Add($"菜单项 id 重复：{menu.Id}");
            }

            if (string.IsNullOrWhiteSpace(menu.Command) && string.IsNullOrWhiteSpace(menu.Dialog))
            {
                result.Errors.Add($"菜单项 {menu.Id} 需要 command 或 dialog 之一");
            }

            if (!string.IsNullOrWhiteSpace(menu.Dialog) && !dialogIds.Contains(menu.Dialog))
            {
                result.Errors.Add($"菜单项 {menu.Id} 引用了不存在的浮窗：{menu.Dialog}");
            }
        }

        foreach (var theme in ui.Themes)
        {
            if (string.IsNullOrWhiteSpace(theme.Id) || string.IsNullOrWhiteSpace(theme.Name))
            {
                result.Errors.Add("ui.themes[] 需要 id 与 name");
            }

            if (theme.Tokens.Count == 0)
            {
                result.Warnings.Add($"主题 {theme.Id} 没有任何令牌");
            }
        }

        var total = ui.Pages.Count + ui.Dialogs.Count + ui.Menus.Count + ui.Themes.Count;
        if (total > 0)
        {
            result.Warnings.Add($"插件声明了 {total} 项界面贡献（页面/浮窗/菜单/主题）");
        }
    }

    /// <summary>读取 manifest；文件缺失或损坏时返回 null，不影响其它插件展示。</summary>
    private static async Task<PluginSDK.PluginManifest?> ReadManifestAsync(
        string? manifestPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            return await JsonSerializer.DeserializeAsync<PluginSDK.PluginManifest>(
                stream,
                PluginSDK.PluginManifestJson.Options,
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<PluginOperationResult> ExecutePluginCommandAsync(string pluginId, string command, string? arguments = null, CancellationToken cancellationToken = default)
    {
        var gate = _commandGates.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ExecutePluginCommandCoreAsync(pluginId, command, arguments, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    // 宿主每个插件进程只接受一个当前命令；在服务层串行化，避免网页或其它入口并发调用互相取消。
    private async Task<PluginOperationResult> ExecutePluginCommandCoreAsync(string pluginId, string command, string? arguments, CancellationToken cancellationToken)
    {
        try
        {
            var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
            if (plugin == null)
            {
                return PluginOperationResult.ErrorResult("Plugin not found", pluginId);
            }

            if (!plugin.Enabled)
            {
                return PluginOperationResult.ErrorResult("Plugin is disabled", pluginId);
            }

            var manifest = await ReadManifestAsync(plugin.ManifestPath, cancellationToken);
            if (manifest?.IsWebPlugin == true)
            {
                return PluginOperationResult.ErrorResult("纯网页插件没有命令运行时；需要执行命令请改用 lua 或 dotnet。");
            }

            var declared = await ReadDeclaredCommandsAsync(plugin.ManifestPath, cancellationToken);
            var descriptor = declared.FirstOrDefault(
                c => string.Equals(c.Name, command, StringComparison.OrdinalIgnoreCase));

            if (descriptor == null)
            {
                return PluginOperationResult.ErrorResult($"插件未声明命令 '{command}'", pluginId);
            }

            // 受控入口的最小权限落地：命令声明的权限未授予时不启动宿主
            if (!string.IsNullOrWhiteSpace(descriptor.RequiredPermission) &&
                !await CheckPermissionAsync(pluginId, descriptor.RequiredPermission, cancellationToken))
            {
                return PluginOperationResult.ErrorResult(
                    $"缺少权限 '{descriptor.RequiredPermission}'，已拒绝执行命令 '{command}'",
                    pluginId);
            }

            var history = new PluginExecutionHistory
            {
                Id = Guid.NewGuid().ToString(),
                PluginId = pluginId,
                Command = command,
                StartTime = DateTime.UtcNow,
                Status = "Running"
            };

            await _pluginExecutionRepository.AddAsync(history, cancellationToken);

            // 命令执行前按需拉起宿主；启动失败要落历史，避免留下永久 Running 记录
            var alreadyRunning = _pluginHostManager.GetRunningPlugins()
                ?.Any(p => string.Equals(p.PluginId, pluginId, StringComparison.Ordinal)) == true;

            if (!alreadyRunning)
            {
                var startResult = await StartPluginAsync(pluginId, cancellationToken);
                if (!startResult.Success)
                {
                    history.EndTime = DateTime.UtcNow;
                    history.Status = "Failed";
                    history.Error = startResult.Error ?? startResult.Message;
                    await _pluginExecutionRepository.UpdateAsync(history, cancellationToken);
                    return startResult;
                }
            }

            var result = await _pluginHostManager.ExecutePluginCommandAsync(pluginId, command, arguments, cancellationToken);

            history.EndTime = DateTime.UtcNow;
            history.Status = result.Success ? "Completed" : "Failed";
            history.Result = result.Message;
            history.Error = result.Error;

            await _pluginExecutionRepository.UpdateAsync(history, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            return PluginOperationResult.ErrorResult(ex.Message, "Execution failed");
        }
    }

    public async Task<List<PluginExecutionHistory>> GetExecutionHistoryAsync(string pluginId, int limit = 100, CancellationToken cancellationToken = default)
    {
        return await _pluginExecutionRepository.GetByPluginIdAsync(pluginId, limit, cancellationToken);
    }

    /// <summary>
    /// 敏感权限：即使 Sandbox 安装也不自动授予，必须在权限对话框中显式勾选。
    /// ai.use 走主程序代理，插件本身拿不到密钥，但仍需用户知情授权。
    /// </summary>
    private static bool IsSensitivePermission(string permission)
        => string.Equals(permission, PluginAiBroker.AiPermission, StringComparison.OrdinalIgnoreCase)
           || string.Equals(permission, nameof(PluginSDK.PluginPermission.UseAI), StringComparison.OrdinalIgnoreCase);

    private static bool IsValidVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var parts = version.Split('.');
        if (parts.Length < 2 || parts.Length > 4)
        {
            return false;
        }

        return parts.All(part => int.TryParse(part, out _));
    }
}
