using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

public sealed class HellForgeSyncProgress
{
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int TotalMods { get; set; }
    public int CompletedMods { get; set; }
}

public sealed record HellForgeSyncSkip(string Name, string Reason);

public sealed class HellForgeSyncResult
{
    public DeploymentStatus Status { get; set; }
    public int SyncedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int UnchangedCount { get; set; }
    public int RemovedCount { get; set; }
    public List<HellForgeSyncSkip> SkippedMods { get; set; } = new();
    public string? Message { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed record HellForgeSyncStatus(bool HasSync, int ModCount, DateTime? SyncedAt, string? ProfileId);

/// <summary>
/// HellForge 模式下的模组同步：把配置中启用的普通模组导入安装根 mods\（干净 V1 透传为 cosmetic pack，
/// 其余扁平化为 hellforge-mod.json + data/），启用→mod-switch、加载顺序取反→mod-order、pack 选项→mod-pack。
/// 只经官方 CLI 操作 mods\，绝不写 game\data（暂存与清理由 HellForge 会话运行器负责）。
/// 会话运行中拒绝同步；Lua 加载器/依赖与 HD2IAS 模组跳过并记录原因。归属与指纹记录在 AppData 状态文件，
/// 未变化的模组跳过重新导出。接口约定见 HellForge 仓库 docs/external-launchers.md（GPL-3.0，独立进程调用）。
/// </summary>
public class HellForgeModSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly StringComparer IdComparer = StringComparer.Ordinal;

    private readonly HellForgeService _hellForge;
    private readonly IProfileRepository _profiles;
    private readonly IModRepository _mods;
    private readonly SettingsService _settings;
    private readonly string _exportBaseDirectory;
    private readonly string _stateFilePath;
    private readonly ILogger<HellForgeModSyncService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HellForgeModSyncService(
        HellForgeService hellForge,
        IProfileRepository profiles,
        IModRepository mods,
        SettingsService settings,
        string exportBaseDirectory,
        string stateFilePath,
        ILogger<HellForgeModSyncService>? logger = null)
    {
        _hellForge = hellForge;
        _profiles = profiles;
        _mods = mods;
        _settings = settings;
        _exportBaseDirectory = exportBaseDirectory;
        _stateFilePath = stateFilePath;
        _logger = logger;
    }

    public async Task<HellForgeSyncResult> SyncProfileAsync(Guid profileId, DeploymentOrder order,
        IProgress<HellForgeSyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = RequireInstallationRoot();
            await EnsureNoSessionAsync(cancellationToken);

            var profile = await _profiles.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"配置文件 {profileId} 不存在。");
            var enabledIds = profile.Mods.Where(item => item.Enabled).Select(item => item.ModId).ToList();
            var modsById = (await _mods.GetByIdsAsync(enabledIds, cancellationToken)).ToDictionary(mod => mod.Id);
            foreach (var mod in modsById.Values)
                await BslProfilePolicy.EnsureDetectedAsync(mod, _mods, cancellationToken);

            var sequence = BslProfilePolicy.DeploymentSequence(profile, modsById, order, _settings.IsLoaderOrderLocked());
            var skipped = new List<HellForgeSyncSkip>();
            var synced = new List<(ProfileMod ProfileMod, Mod Mod, HellForgeModKind Kind)>();
            foreach (var profileMod in sequence)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!modsById.TryGetValue(profileMod.ModId, out var mod) || !Directory.Exists(mod.Path))
                {
                    skipped.Add(new HellForgeSyncSkip(mod?.Name ?? profileMod.ModId.ToString("N"), "模组文件缺失"));
                    continue;
                }
                if (BslProfilePolicy.Role(mod) != BslModRole.None)
                {
                    skipped.Add(new HellForgeSyncSkip(mod.Name, "Lua 加载器/依赖模组不参与 HellForge 同步"));
                    continue;
                }
                if (string.Equals(mod.WorkflowType, Hd2IrIntegrationService.Workflow, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(new HellForgeSyncSkip(mod.Name, "HD2IAS 模组不参与 HellForge 同步"));
                    continue;
                }
                synced.Add((profileMod, mod, HellForgeModExporter.ResolveKind(mod)));
            }

            var state = LoadState();
            var installation = state.Installations.GetValueOrDefault(root) ?? new HellForgeInstallationSync();
            var owned = installation.Mods;
            var current = (await _hellForge.GetModsAsync(cancellationToken)).Mods
                .ToDictionary(mod => mod.Id, IdComparer);

            var total = synced.Count;
            var done = 0;
            var updated = 0;
            var unchanged = 0;
            var syncedIds = new List<string>();
            var seenIds = new HashSet<string>(IdComparer);
            foreach (var (profileMod, mod, kind) in synced)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = mod.Id.ToString("N");
                var fingerprint = HellForgeModExporter.ComputeFingerprint(mod, profileMod.OptionsJson, kind, cancellationToken);
                string hellforgeId;
                if (owned.TryGetValue(key, out var previous)
                    && previous.Fingerprint == fingerprint
                    && current.ContainsKey(previous.HellforgeId))
                {
                    hellforgeId = previous.HellforgeId;
                    unchanged++;
                }
                else
                {
                    progress?.Report(new HellForgeSyncProgress
                    {
                        Stage = "Exporting", Message = $"正在导出 {mod.Name}...", TotalMods = total, CompletedMods = done
                    });
                    var export = await HellForgeModExporter.ExportAsync(mod, profileMod.OptionsJson,
                        Path.Combine(_exportBaseDirectory, key), cancellationToken);
                    var added = await _hellForge.AddModAsync(export.ManifestPath, cancellationToken);
                    hellforgeId = added.Id;
                    owned[key] = new HellForgeSyncedMod
                    {
                        HellforgeId = hellforgeId, Kind = kind.ToString().ToLowerInvariant(), Fingerprint = fingerprint
                    };
                    updated++;
                }
                // 同一 HellForge id 只列一次（重复 GUID 的模组经 mod-add 互相替换，mod-order 拒绝重复 id）。
                if (seenIds.Add(hellforgeId)) syncedIds.Add(hellforgeId);
                done++;
                progress?.Report(new HellForgeSyncProgress
                {
                    Stage = "Syncing", Message = $"已同步 {done}/{total}", TotalMods = total, CompletedMods = done
                });
            }

            var syncedKeys = synced.Select(item => item.Mod.Id.ToString("N")).ToHashSet(IdComparer);
            var removed = 0;
            foreach (var (key, entry) in owned.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (syncedKeys.Contains(key)) continue;
                if (current.TryGetValue(entry.HellforgeId, out var info) && info.Removable)
                {
                    await _hellForge.RemoveModAsync(entry.HellforgeId, cancellationToken);
                    removed++;
                }
                owned.Remove(key);
            }

            installation.ProfileId = profileId.ToString("N");
            installation.SyncedAt = DateTime.UtcNow;
            installation.Mods = owned;
            state.Installations[root] = installation;
            SaveState(state);

            await ApplyStateAsync(synced, syncedIds, owned, cancellationToken);

            var result = new HellForgeSyncResult
            {
                Status = skipped.Count > 0 ? DeploymentStatus.PartiallyFailed : DeploymentStatus.Completed,
                SyncedCount = synced.Count, UpdatedCount = updated, UnchangedCount = unchanged,
                RemovedCount = removed, SkippedMods = skipped
            };
            result.Message = BuildSyncMessage(result);
            progress?.Report(new HellForgeSyncProgress
            {
                Stage = "Completed", Message = result.Message, TotalMods = total, CompletedMods = total
            });
            return result;
        }
        catch (OperationCanceledException)
        {
            return new HellForgeSyncResult { Status = DeploymentStatus.Cancelled, ErrorMessage = "同步已取消" };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "HellForge 模组同步失败");
            return new HellForgeSyncResult { Status = DeploymentStatus.Failed, ErrorMessage = ex.Message };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清理：移除本管理器同步进当前安装 mods\ 的全部模组（不触碰 game\data 与他人模组）。</summary>
    public async Task<HellForgeSyncResult> RemoveSyncedAsync(
        IProgress<HellForgeSyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = RequireInstallationRoot();
            await EnsureNoSessionAsync(cancellationToken);
            var state = LoadState();
            if (!state.Installations.TryGetValue(root, out var installation) || installation.Mods.Count == 0)
                return new HellForgeSyncResult { Status = DeploymentStatus.Completed, Message = "没有需要同步清理的 HellForge 模组。" };

            var current = (await _hellForge.GetModsAsync(cancellationToken)).Mods.ToDictionary(mod => mod.Id, IdComparer);
            var removed = 0;
            foreach (var (key, entry) in installation.Mods.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (current.TryGetValue(entry.HellforgeId, out var info) && info.Removable)
                {
                    progress?.Report(new HellForgeSyncProgress { Stage = "Removing", Message = $"正在移除 {entry.HellforgeId}..." });
                    await _hellForge.RemoveModAsync(entry.HellforgeId, cancellationToken);
                    removed++;
                }
                installation.Mods.Remove(key);
            }
            installation.SyncedAt = DateTime.UtcNow;
            SaveState(state);
            var result = new HellForgeSyncResult
            {
                Status = DeploymentStatus.Completed, RemovedCount = removed,
                Message = $"已从 HellForge 移除 {removed} 个本管理器同步的模组。"
            };
            progress?.Report(new HellForgeSyncProgress { Stage = "Completed", Message = result.Message });
            return result;
        }
        catch (OperationCanceledException)
        {
            return new HellForgeSyncResult { Status = DeploymentStatus.Cancelled, ErrorMessage = "同步清理已取消" };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "HellForge 模组同步清理失败");
            return new HellForgeSyncResult { Status = DeploymentStatus.Failed, ErrorMessage = ex.Message };
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<HellForgeSyncStatus> GetStatusAsync()
    {
        if (!_hellForge.TryGetInstallationRoot(out var root))
            return Task.FromResult(new HellForgeSyncStatus(false, 0, null, null));
        var state = LoadState();
        if (!state.Installations.TryGetValue(root, out var installation) || installation.Mods.Count == 0)
            return Task.FromResult(new HellForgeSyncStatus(false, 0, null, null));
        return Task.FromResult(new HellForgeSyncStatus(true, installation.Mods.Count, installation.SyncedAt, installation.ProfileId));
    }

    /// <summary>启用、加载顺序（取反置顶）、pack 选项的最小 diff，全部经 CLI；先重查 mods 取最新状态。</summary>
    private async Task ApplyStateAsync(
        List<(ProfileMod ProfileMod, Mod Mod, HellForgeModKind Kind)> synced,
        List<string> syncedIds, Dictionary<string, HellForgeSyncedMod> owned, CancellationToken cancellationToken)
    {
        if (syncedIds.Count == 0) return;
        var fresh = (await _hellForge.GetModsAsync(cancellationToken)).Mods.ToDictionary(mod => mod.Id, IdComparer);

        foreach (var id in syncedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fresh.TryGetValue(id, out var info) && !info.Enabled)
                await _hellForge.SwitchModAsync(id, true, cancellationToken);
        }

        // HD2Mimnt 部署序列是加载顺序（先部署=底部）；HellForge 顶部最后加载，故取反置顶。
        await _hellForge.OrderModsAsync(syncedIds.AsEnumerable().Reverse().ToList(), cancellationToken);

        foreach (var (profileMod, mod, kind) in synced)
        {
            if (kind != HellForgeModKind.Pack || mod.Manifest?.Options == null) continue;
            if (!owned.TryGetValue(mod.Id.ToString("N"), out var entry)) continue;
            if (!fresh.TryGetValue(entry.HellforgeId, out var info) || info.Options == null) continue;
            var effective = ProfileModOptionResolver.ResolveEffective(mod.Manifest, profileMod.OptionsJson);
            var byName = info.Options.ToDictionary(option => option.Name, StringComparer.Ordinal);
            for (var i = 0; i < mod.Manifest.Options.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var option = mod.Manifest.Options[i];
                if (!byName.TryGetValue(option.Name, out var currentOption)) continue;
                var desiredOn = effective.OptionToggles![i];
                bool? on = currentOption.On != desiredOn ? desiredOn : null;
                string? variant = null;
                if (option.SubOptions is { Count: > 0 } subs)
                {
                    var desiredSub = subs[effective.OptionSelections![i]].Name;
                    var currentPicked = currentOption.Picked ?? subs[0].Name;
                    if (!string.Equals(desiredSub, currentPicked, StringComparison.Ordinal)) variant = desiredSub;
                }
                if (on.HasValue || variant != null)
                    await _hellForge.SetPackOptionAsync(entry.HellforgeId, option.Name, on, variant, cancellationToken);
            }
        }
    }

    private string RequireInstallationRoot()
    {
        if (!_hellForge.TryGetInstallationRoot(out var root))
            throw new InvalidOperationException("尚未配置有效的 HellForge 安装目录。");
        return root;
    }

    private async Task EnsureNoSessionAsync(CancellationToken cancellationToken)
    {
        var status = await _hellForge.GetStatusAsync(cancellationToken);
        if (status.Session.Running)
            throw new InvalidOperationException("HellForge 会话正在运行，请结束后再同步模组。");
    }

    private static string BuildSyncMessage(HellForgeSyncResult result)
    {
        var message = $"已同步 {result.SyncedCount} 个模组到 HellForge：更新 {result.UpdatedCount}、未变化 {result.UnchangedCount}、移除 {result.RemovedCount}。";
        if (result.SkippedMods.Count > 0)
            message += $"；跳过 {result.SkippedMods.Count} 个：{string.Join("、", result.SkippedMods.Select(skip => skip.Name))}";
        return message;
    }

    private HellForgeSyncState LoadState()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return new HellForgeSyncState();
            return JsonSerializer.Deserialize<HellForgeSyncState>(File.ReadAllText(_stateFilePath), JsonOptions)
                ?? new HellForgeSyncState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "HellForge 同步状态文件无法读取，按空状态处理");
            return new HellForgeSyncState();
        }
    }

    private void SaveState(HellForgeSyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
        var temporary = _stateFilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, _stateFilePath, overwrite: true);
    }
}

internal sealed class HellForgeSyncState
{
    public Dictionary<string, HellForgeInstallationSync> Installations { get; set; } = new();
}

internal sealed class HellForgeInstallationSync
{
    public string? ProfileId { get; set; }
    public DateTime SyncedAt { get; set; }
    public Dictionary<string, HellForgeSyncedMod> Mods { get; set; } = new();
}

internal sealed class HellForgeSyncedMod
{
    public string HellforgeId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
}