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

    public PluginService(
        IPluginRepository pluginRepository,
        IPluginPermissionRepository pluginPermissionRepository,
        IPluginExecutionRepository pluginExecutionRepository,
        IPluginHostManager pluginHostManager)
    {
        _pluginRepository = pluginRepository;
        _pluginPermissionRepository = pluginPermissionRepository;
        _pluginExecutionRepository = pluginExecutionRepository;
        _pluginHostManager = pluginHostManager;
    }

    public async Task<PluginOperationResult> InstallPluginAsync(string manifestPath, CancellationToken cancellationToken = default)
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
            var manifest = JsonSerializer.Deserialize<PluginSDK.PluginManifest>(manifestJson);
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

            foreach (var permission in manifest.RequestedPermissions)
            {
                var pluginPermission = new PluginPermission
                {
                    Id = Guid.NewGuid().ToString(),
                    PluginId = manifest.Id,
                    Permission = permission,
                    Granted = manifest.PermissionMode == PluginSDK.PluginPermissionMode.Sandbox,
                    GrantedAt = manifest.PermissionMode == PluginSDK.PluginPermissionMode.Sandbox ? DateTime.UtcNow : null
                };

                await _pluginPermissionRepository.AddAsync(pluginPermission, cancellationToken);
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
            var manifest = JsonSerializer.Deserialize<PluginSDK.PluginManifest>(manifestJson);

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

            if (manifest.PermissionMode == PluginSDK.PluginPermissionMode.FullAccess)
            {
                result.Warnings.Add("Plugin requests Full Access mode - requires user approval");
            }

            if (manifest.RequestedPermissions.Count > 0)
            {
                result.Warnings.Add($"Plugin requests {manifest.RequestedPermissions.Count} permissions");
            }

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
            var manifest = await JsonSerializer.DeserializeAsync<PluginSDK.PluginManifest>(stream, cancellationToken: cancellationToken);
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

            var history = new PluginExecutionHistory
            {
                Id = Guid.NewGuid().ToString(),
                PluginId = pluginId,
                Command = command,
                StartTime = DateTime.UtcNow,
                Status = "Running"
            };

            await _pluginExecutionRepository.AddAsync(history, cancellationToken);

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
