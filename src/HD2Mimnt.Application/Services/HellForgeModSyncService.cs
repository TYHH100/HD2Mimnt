using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IO;
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
    /// <summary>实际写入方式（Copy/HardLink/SymbolicLink），按设置中的部署方式解析后可能因不可用回退。</summary>
    public string? Method { get; set; }
    /// <summary>非致命提示（如部署方式不可用已回退文件复制）。</summary>
    public List<string> Warnings { get; set; } = new();
    public string? Message { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed record HellForgeSyncStatus(bool HasSync, int ModCount, DateTime? SyncedAt, string? ProfileId);

/// <summary>
/// HellForge 模式下的模组同步：把配置中启用的普通模组按设置中的部署方式直接写进安装根 <c>mods\</c>（干净 V1 透传为
/// cosmetic pack，其余扁平化为 hellforge-mod.json + data/；Copy=只复制一次，HardLink/SymbolicLink=文件级链接）。
/// 启用状态、pack 选项与顺序写进 <c>mods\hellforge-mods.json</c>（上游 docs/external-launchers.md 明确允许外部启动器写，
/// 临时文件 + 改名），顺序仍用 <c>mod-order</c>；不调 <c>mod-add</c>（内部恒为 File.Copy，无法按设置建链接），
/// 也不逐个调 <c>mod-switch</c>/<c>mod-pack</c>（每条命令都要重扫整个 mods\，几十个外观包时单次约 5 秒）。
/// 是否已同步、选项/开关/顺序是否一致全部本地读文件判断，只有真的导入了才跑一次 <c>mods</c> 做权威校验。
/// 绝不写 game\data（暂存与清理由 HellForge 会话运行器负责）。会话运行中拒绝同步；Lua 加载器/依赖与 HD2IAS 模组
/// 跳过并记录原因。归属与指纹记录在 AppData 状态文件，未变化的模组跳过重新导出。
/// 接口约定见 HellForge 仓库 docs/external-launchers.md（GPL-3.0，独立进程调用）。
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
    private readonly IReadOnlyDictionary<DeploymentMethod, IDeploymentMethod> _deploymentMethods;
    private readonly string _stateFilePath;
    private readonly string? _legacyExportDirectory;
    private readonly ILogger<HellForgeModSyncService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HellForgeModSyncService(
        HellForgeService hellForge,
        IProfileRepository profiles,
        IModRepository mods,
        SettingsService settings,
        IEnumerable<IDeploymentMethod> deploymentMethods,
        string stateFilePath,
        ILogger<HellForgeModSyncService>? logger = null,
        string? legacyExportDirectory = null)
    {
        _hellForge = hellForge;
        _profiles = profiles;
        _mods = mods;
        _settings = settings;
        _deploymentMethods = deploymentMethods
            .Where(method => Enum.TryParse<DeploymentMethod>(method.Name, out var parsed) && Enum.IsDefined(parsed))
            .GroupBy(method => Enum.Parse<DeploymentMethod>(method.Name))
            .ToDictionary(group => group.Key, group => group.First());
        _stateFilePath = stateFilePath;
        _logger = logger;
        _legacyExportDirectory = legacyExportDirectory;
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

            var warnings = new List<string>();
            // 两个启用模组算出同一个 HellForge id（同 Guid 的外观包）会被判重复、两个都不加载：
            // 后面那个改成扁平导出（唯一 id），内容不丢。
            var claimedIds = new HashSet<string>(IdComparer);
            var deduped = new List<(ProfileMod ProfileMod, Mod Mod, HellForgeModKind Kind)>(synced.Count);
            foreach (var item in synced)
            {
                var kind = item.Kind;
                var resolvedId = HellForgeModExporter.ResolveModId(item.Mod, kind);
                if (!claimedIds.Add(resolvedId) && kind == HellForgeModKind.Pack)
                {
                    warnings.Add($"“{item.Mod.Name}”与另一个启用模组共用外观包 id（{resolvedId}），已改为扁平导出，避免 HellForge 把两者都判为重复。");
                    kind = HellForgeModKind.Flat;
                    resolvedId = HellForgeModExporter.ResolveModId(item.Mod, kind);
                    claimedIds.Add(resolvedId);
                }
                deduped.Add((item.ProfileMod, item.Mod, kind));
            }
            synced = deduped;

            var state = LoadState();
            var installation = state.Installations.GetValueOrDefault(root) ?? new HellForgeInstallationSync();
            var owned = installation.Mods;
            var modsDirectory = Path.Combine(root, "mods");
            Directory.CreateDirectory(modsDirectory);
            // 上次同步中断留下的临时目录（点前缀，HellForge 不加载）：清掉，别越堆越多。
            foreach (var leftover in Directory.EnumerateDirectories(modsDirectory, StagingPrefix + "*"))
                TryDeleteDirectory(leftover);

            // 本地读 mods\ 和 hellforge-mods.json，不再为“谁已经在了 / 谁被关了 / 选项什么状态”去跑 CLI：
            // 一次 mods 调用要把整个 mods\ 扫一遍（几十个外观包约 5-6 秒），同步的固定开销主要就是它。
            var local = HellForgeLocalMods.Scan(modsDirectory);
            var modsFile = HellForgeModsFile.Load(modsDirectory);
            var foldersById = local.Values.ToDictionary(mod => mod.Id, mod => mod.Folder, IdComparer);
            var placer = await HellForgeModFilePlacer.ResolveAsync(_settings, _deploymentMethods,
                synced.Count > 0 ? synced[0].Mod.Path : modsDirectory, modsDirectory,
                warning => warnings.Add(warning), cancellationToken);

            var total = synced.Count;
            var done = 0;
            var updated = 0;
            var unchanged = 0;
            var importedFolders = new Dictionary<string, string>(IdComparer);
            var previousIds = new Dictionary<string, string>(IdComparer);
            foreach (var (profileMod, mod, kind) in synced)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = mod.Id.ToString("N");
                var fingerprint = HellForgeModExporter.ComputeFingerprint(mod, profileMod.OptionsJson, kind, cancellationToken);
                if (owned.TryGetValue(key, out var previous)
                    && previous.Fingerprint == fingerprint
                    && previous.HellforgeId.Length > 0
                    && local.ContainsKey(previous.HellforgeId))
                {
                    unchanged++;
                }
                else
                {
                    previousIds[key] = previous?.HellforgeId ?? string.Empty;
                    progress?.Report(new HellForgeSyncProgress
                    {
                        Stage = "Exporting", Message = $"正在导出 {mod.Name}...", TotalMods = total, CompletedMods = done
                    });
                    importedFolders[key] = await ImportModAsync(mod, kind, profileMod.OptionsJson, modsDirectory,
                        foldersById, placer, cancellationToken);
                    // id 先留空，写完后以 mods 的权威列表按目录校正（pack 的 id 由 HellForge 从清单推导）。
                    owned[key] = new HellForgeSyncedMod
                    {
                        Kind = kind.ToString().ToLowerInvariant(), Fingerprint = fingerprint
                    };
                    updated++;
                }
                done++;
                progress?.Report(new HellForgeSyncProgress
                {
                    Stage = "Syncing", Message = $"已同步 {done}/{total}", TotalMods = total, CompletedMods = done
                });
            }

            var syncedKeys = synced.Select(item => item.Mod.Id.ToString("N")).ToHashSet(IdComparer);
            var removed = 0;
            var droppedIds = new List<string>();
            foreach (var (key, entry) in owned.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (syncedKeys.Contains(key)) continue;
                // 目录是本管理器写的：直接删目录并清状态，不再为每个模组跑一次 mod-remove（几秒一次）。
                if (entry.HellforgeId.Length > 0 && local.TryGetValue(entry.HellforgeId, out var stale))
                {
                    DeleteOwnedFolder(stale.Folder, modsDirectory);
                    foldersById.Remove(entry.HellforgeId);
                    removed++;
                }
                if (entry.HellforgeId.Length > 0) droppedIds.Add(entry.HellforgeId);
                owned.Remove(key);
            }

            // 只有真的导入了才跑一次 mods：确认 HellForge 认出了新目录，并按目录校正 pack 的实际 id。
            // 没有导入时不做任何 CLI 列表调用——这是“再点一次同步”从十几秒降到一秒内的关键。
            var syncedByKey = synced.ToDictionary(item => item.Mod.Id.ToString("N"), item => item, IdComparer);
            if (importedFolders.Count > 0)
            {
                var listed = await _hellForge.GetModsAsync(cancellationToken);
                var listedIds = listed.Mods.Select(info => info.Id).ToHashSet(IdComparer);
                var byFolder = listed.Mods
                    .Where(info => !string.IsNullOrWhiteSpace(info.Folder))
                    .GroupBy(info => NormalizePath(info.Folder!), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
                foreach (var (key, folder) in importedFolders)
                {
                    var expectedId = HellForgeModExporter.ResolveModId(syncedByKey[key].Mod, syncedByKey[key].Kind);
                    owned[key].HellforgeId = byFolder.TryGetValue(NormalizePath(folder), out var id) ? id : expectedId;
                    // 模组 id 变了（如 pack 的 Guid 变更）：旧目录按旧 id 移除，避免 HellForge 里留两个同源模组。
                    var currentId = owned[key].HellforgeId;
                    if (previousIds.TryGetValue(key, out var oldId) && oldId.Length > 0
                        && !string.Equals(oldId, currentId, StringComparison.Ordinal)
                        && foldersById.Remove(oldId, out var staleFolder))
                        DeleteOwnedFolder(staleFolder, modsDirectory);
                }

                var unresolved = importedFolders.Keys
                    .Where(key => !listedIds.Contains(owned[key].HellforgeId))
                    .Select(key => $"{syncedByKey[key].Mod.Name}（{HellForgeModExporter.ResolveModId(syncedByKey[key].Mod, syncedByKey[key].Kind)}）")
                    .ToList();
                if (unresolved.Count > 0)
                    throw new InvalidOperationException(
                        $"HellForge 未识别导入的模组：{string.Join("、", unresolved)}。"
                        + (listed.Problems.Count > 0 ? string.Join("；", listed.Problems) : string.Empty));
            }

            var syncedIds = new List<string>();
            var seenIds = new HashSet<string>(IdComparer);
            // 同一 HellForge id 只列一次（同 id 的模组互相替换，mod-order 拒绝重复 id）。
            foreach (var (_, mod, _) in synced)
            {
                var id = owned[mod.Id.ToString("N")].HellforgeId;
                if (seenIds.Add(id)) syncedIds.Add(id);
            }

            installation.ProfileId = profileId.ToString("N");
            installation.SyncedAt = DateTime.UtcNow;
            installation.Mods = owned;
            state.Installations[root] = installation;
            SaveState(state);

            // 旧版本先导出到 AppData 缓存、再由 mod-add 复制一遍；现在直接写 mods\，遗留缓存一并清掉。
            if (!string.IsNullOrWhiteSpace(_legacyExportDirectory)) TryDeleteDirectory(_legacyExportDirectory);

            // 启用 + 外观包选项 + 清掉已删模组的条目：官方文档允许外部启动器直接写 hellforge-mods.json（临时文件 + 改名），
            // 一次写完，不再为每个选项跑 mod-pack（每条命令都会重新扫描整个 mods\，几十个包时单次约 5 秒）。
            modsFile.Enable(syncedIds);
            modsFile.Remove(droppedIds);
            foreach (var (profileMod, mod, kind) in synced)
            {
                if (!owned.TryGetValue(mod.Id.ToString("N"), out var entry) || entry.HellforgeId.Length == 0) continue;
                if (kind != HellForgeModKind.Pack || mod.Manifest?.Options == null)
                {
                    // 以前按外观包导出过、现在扁平化：清掉遗留的选项条目。
                    modsFile.ForgetPack(entry.HellforgeId);
                    continue;
                }
                var effective = ProfileModOptionResolver.ResolveEffective(mod.Manifest, profileMod.OptionsJson);
                // 导出清单里的选项名（重名已追加序号）——HellForge 用名字做开关/变体集合的键，必须与清单一致。
                var names = HellForgeModExporter.PackOptionNames(mod.Manifest.Options);
                var off = new List<string>();
                var subs = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < mod.Manifest.Options.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!effective.OptionToggles![i]) off.Add(names[i]);
                    var option = mod.Manifest.Options[i];
                    if (option.SubOptions is { Count: > 0 })
                        subs[names[i]] = HellForgeModExporter.PackSubOptionNames(option)[effective.OptionSelections![i]];
                }
                modsFile.SetPackChoice(entry.HellforgeId, off, subs);
            }
            modsFile.SaveIfChanged();

            // 顺序仍交给 mod-order：只写一个数组（很快），置顶规则由上游保证。
            var desiredOrder = syncedIds.AsEnumerable().Reverse().ToList();
            if (!modsFile.OrderMatches(desiredOrder))
                await _hellForge.OrderModsAsync(desiredOrder, cancellationToken);

            var result = new HellForgeSyncResult
            {
                Status = skipped.Count > 0 ? DeploymentStatus.PartiallyFailed : DeploymentStatus.Completed,
                SyncedCount = synced.Count, UpdatedCount = updated, UnchangedCount = unchanged,
                RemovedCount = removed, SkippedMods = skipped,
                // 出现“建链失败改为复制”时按实际写入方式报告，避免文案与实际占用不符。
                Method = placer.UsedFallback ? nameof(DeploymentMethod.Copy) : placer.MethodName,
                Warnings = warnings
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

            // 目录与 hellforge-mods.json 里的条目都是本管理器写的：直接删目录 + 清状态，
            // 不再逐个 mod-remove（每条命令都会重新扫描整个 mods\，几十个模组就是几分钟）。
            var modsDirectory = Path.Combine(root, "mods");
            var local = HellForgeLocalMods.Scan(modsDirectory);
            var modsFile = HellForgeModsFile.Load(modsDirectory);
            var removed = 0;
            var ids = new List<string>();
            foreach (var (key, entry) in installation.Mods.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.HellforgeId.Length > 0)
                {
                    ids.Add(entry.HellforgeId);
                    progress?.Report(new HellForgeSyncProgress { Stage = "Removing", Message = $"正在移除 {entry.HellforgeId}..." });
                    if (local.TryGetValue(entry.HellforgeId, out var owned))
                    {
                        DeleteOwnedFolder(owned.Folder, modsDirectory);
                        removed++;
                    }
                }
                installation.Mods.Remove(key);
            }
            modsFile.Remove(ids);
            modsFile.SaveIfChanged();
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

    /// <summary>
    /// 把一个模组按部署方式写进 mods\：先在点前缀临时目录导出（HellForge 不加载点目录），再整体改名成模组 id；
    /// 同 id 的旧目录（旧版本由 mod-add 落盘，目录名与 id 不同）与历史遗留副本先移除。返回最终目录。
    /// </summary>
    private static async Task<string> ImportModAsync(Mod mod, HellForgeModKind kind, string? optionsJson,
        string modsDirectory, Dictionary<string, string> foldersById, HellForgeModFilePlacer placer,
        CancellationToken cancellationToken)
    {
        var id = HellForgeModExporter.ResolveModId(mod, kind);
        var staging = Path.Combine(modsDirectory, StagingPrefix + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await HellForgeModExporter.ExportAsync(mod, optionsJson, staging, placer.PlaceAsync, kind, cancellationToken);
            await WriteMarkerAsync(staging, mod, kind, cancellationToken);
            if (foldersById.Remove(id, out var previousFolder))
                DeleteOwnedFolder(previousFolder, modsDirectory);
            DeleteLeftovers(modsDirectory, id, mod.Id.ToString("N"));
            var target = Path.Combine(modsDirectory, id);
            Directory.Move(staging, target);
            foldersById[id] = target;
            return target;
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>同步写入的目录名：等于 HellForge 会算出的模组 id（与上游 mod-add 一样同 id 替换）。</summary>
    private const string StagingPrefix = ".hd2mimnt-";

    /// <summary>本管理器的归属标记，用于识别“这个目录是我们写的”（也是清理历史副本的依据）。</summary>
    private const string MarkerFileName = ".hd2mimnt-mod.json";

    private static async Task WriteMarkerAsync(string folder, Mod mod, HellForgeModKind kind,
        CancellationToken cancellationToken)
    {
        var marker = new JsonObject
        {
            ["modId"] = mod.Id.ToString("N"),
            ["kind"] = kind.ToString().ToLowerInvariant(),
            ["name"] = mod.Name
        };
        await File.WriteAllTextAsync(Path.Combine(folder, MarkerFileName), marker.ToJsonString(),
            new UTF8Encoding(false), cancellationToken);
    }

    /// <summary>
    /// 清掉同 id 下历史遗留的目录：默认名 id 与 id-2/id-3…（旧版本因 HellForge 认不出该目录而每次同步都新开一个）。
    /// 只删带本管理器标记、或 HellForge 根本认不出的目录，别人的模组不动。
    /// </summary>
    private static void DeleteLeftovers(string modsDirectory, string id, string modKey)
    {
        foreach (var directory in Directory.EnumerateDirectories(modsDirectory))
            if (IsOwnFolderName(Path.GetFileName(directory), id) && IsOwnFolder(directory, modKey))
                TryDeleteDirectory(directory);
    }

    private static bool IsOwnFolderName(string name, string id)
    {
        if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length > id.Length + 1
               && name.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase)
               && name[(id.Length + 1)..].All(char.IsAsciiDigit);
    }

    private static bool IsOwnFolder(string directory, string modKey)
    {
        var marker = Path.Combine(directory, MarkerFileName);
        if (File.Exists(marker))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(marker));
                return !document.RootElement.TryGetProperty("modId", out var id)
                       || string.Equals(id.GetString(), modKey, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return true;
            }
        }
        // 没有标记：HellForge 认得出（有清单）的是别人的模组，不动；认不出的是本管理器留下的半成品。
        return !File.Exists(Path.Combine(directory, "hellforge-mod.json"))
               && !File.Exists(Path.Combine(directory, "bts-mod.json"))
               && !File.Exists(Path.Combine(directory, "manifest.json"));
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>只删除 mods\ 内本管理器登记过的模组目录；路径越界时保持原样。</summary>
    private static void DeleteOwnedFolder(string folder, string modsDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsDirectory));
        var full = Path.GetFullPath(folder);
        if (!PathGuard.IsInsideRoot(root, full)) return;
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        var methodName = result.Method switch
        {
            nameof(DeploymentMethod.HardLink) => "硬链接",
            nameof(DeploymentMethod.SymbolicLink) => "符号链接",
            _ => "文件复制"
        };
        var message = $"已同步 {result.SyncedCount} 个模组到 HellForge（写入方式：{methodName}）：更新 {result.UpdatedCount}、未变化 {result.UnchangedCount}、移除 {result.RemovedCount}。";
        if (result.SkippedMods.Count > 0)
            message += $"；跳过 {result.SkippedMods.Count} 个：{string.Join("、", result.SkippedMods.Select(skip => skip.Name))}";
        if (result.Warnings.Count > 0)
            message += $"；注意：{string.Join("；", result.Warnings)}";
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