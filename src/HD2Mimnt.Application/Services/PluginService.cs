using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
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

    public PluginService(
        IPluginRepository pluginRepository,
        IPluginPermissionRepository pluginPermissionRepository,
        IPluginExecutionRepository pluginExecutionRepository,
        IPluginHostManager pluginHostManager,
        IPluginGrantSession? grantSession = null)
    {
        _pluginRepository = pluginRepository;
        _pluginPermissionRepository = pluginPermissionRepository;
        _pluginExecutionRepository = pluginExecutionRepository;
        _pluginHostManager = pluginHostManager;
        _grantSession = grantSession ?? new PluginGrantSession();
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

            var manifestDirectory = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrEmpty(manifestDirectory))
            {
                return PluginOperationResult.ErrorResult("Invalid manifest path");
            }

            var assemblyPath = Path.Combine(manifestDirectory, manifest.Assembly);
            if (!File.Exists(assemblyPath))
            {
                return PluginOperationResult.ErrorResult("Plugin assembly not found", assemblyPath);
            }

            var plugin = new Plugin
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Version = manifest.Version,
                Author = manifest.Author,
                Description = manifest.Description,
                ManifestPath = manifestPath,
                AssemblyPath = assemblyPath,
                // 完整性锚点：安装时固定程序集内容，启动前比对防替换
                AssemblySha256 = await ComputeFileSha256Async(assemblyPath, cancellationToken),
                InstalledAt = DateTime.UtcNow,
                Enabled = true,
                PermissionMode = manifest.PermissionMode.ToString()
            };

            await _pluginRepository.AddAsync(plugin, cancellationToken);

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
        catch (Exception ex)
        {
            return PluginOperationResult.ErrorResult(ex.Message, "Installation failed");
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
        if (!File.Exists(plugin.AssemblyPath))
        {
            return PluginOperationResult.ErrorResult("Plugin assembly not found", plugin.AssemblyPath);
        }

        var currentSha = await ComputeFileSha256Async(plugin.AssemblyPath, cancellationToken);
        if (plugin.AssemblySha256 is { } recorded)
        {
            if (!string.Equals(recorded, currentSha, StringComparison.OrdinalIgnoreCase))
            {
                return PluginOperationResult.ErrorResult(
                    "插件程序集与安装时不一致，已拒绝启动；请重新安装或更新插件。", pluginId);
            }
        }
        else
        {
            plugin.AssemblySha256 = currentSha;
            await _pluginRepository.UpdateAsync(plugin, cancellationToken);
        }

        return await _pluginHostManager.StartPluginAsync(plugin.Id, plugin.AssemblyPath, plugin.ManifestPath, cancellationToken);
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

            if (string.IsNullOrWhiteSpace(manifest.Assembly))
            {
                result.Errors.Add("Plugin Assembly is required");
            }

            if (string.IsNullOrWhiteSpace(manifest.EntryType))
            {
                result.Errors.Add("Plugin EntryType is required");
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

            ValidateUiContributions(manifest.Ui, result);

            var manifestDirectory = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(manifestDirectory))
            {
                var assemblyPath = Path.Combine(manifestDirectory, manifest.Assembly);
                if (!File.Exists(assemblyPath))
                {
                    result.Errors.Add($"Plugin assembly not found: {manifest.Assembly}");
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
                RequestedPermissions = manifest.RequestedPermissions.ToArray()
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> CheckPermissionAsync(string pluginId, string permission, CancellationToken cancellationToken = default)
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

        var permissions = await _pluginPermissionRepository.GetByPluginIdAsync(pluginId, cancellationToken);
        var pluginPermission = permissions.FirstOrDefault(p => p.Permission == permission);

        return pluginPermission?.Granted ?? false;
    }

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

            if (page.Blocks.Count == 0)
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
