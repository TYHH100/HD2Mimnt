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

/// <summary>
/// 一个被跳过的模组。<paramref name="Expected"/> 为真表示按设计排除（加载器/依赖、HD2IAS），不计为同步失败；
/// 为假表示真实问题（如模组文件缺失），整体状态仍报部分失败。
/// </summary>
public sealed record HellForgeSyncSkip(string Name, string Reason, bool Expected = false);

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

public sealed record HellForgeSyncStatus(bool HasSync, int ModCount, DateTime? SyncedAt, string? ProfileId,
    IReadOnlyDictionary<Guid, HellForgeModMarkInfo>? Marks = null);

/// <summary>
/// HellForge 模式下的模组同步：把配置中启用的普通模组按设置中的部署方式直接写进安装根 <c>mods\</c>（干净 V1 透传为
/// cosmetic pack，其余扁平化为 hellforge-mod.json + data/；Copy=只复制一次，HardLink/SymbolicLink=文件级链接）。
/// 启用状态、pack 选项与顺序写进 <c>mods\hellforge-mods.json</c>（上游 docs/external-launchers.md 明确允许外部启动器写，
/// 临时文件 + 改名），顺序仍用 <c>mod-order</c>；不调 <c>mod-add</c>（内部恒为 File.Copy，无法按设置建链接），
/// 也不逐个调 <c>mod-switch</c>/<c>mod-pack</c>（每条命令都要重扫整个 mods\，几十个外观包时单次约 5 秒）。
/// 是否已同步、选项/开关/顺序是否一致全部本地读文件判断，只有真的导入了才跑一次 <c>mods</c> 做权威校验。
/// 绝不写 game\data（暂存与清理由 HellForge 会话运行器负责）。会话运行中拒绝同步；Lua 加载器本体与 HD2IAS 模组
/// 跳过并记录原因（依赖加载器的 Lua 内容模组照常导出，Lua 随补丁包落地）。归属与指纹记录在 AppData 状态文件，未变化的模组跳过重新导出。
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
            var marks = new Dictionary<string, HellForgeModMarkRules.Entry>(IdComparer);
            void MarkSyncedMod(ProfileMod profileMod, HellForgeModMark mark, string detail = "")
                => marks[profileMod.ModId.ToString("N")] = new HellForgeModMarkRules.Entry { Mark = mark.ToString(), Detail = detail };
            var synced = new List<(ProfileMod ProfileMod, Mod Mod, HellForgeModKind Kind)>();
            // HD2IAS 模组的组件是「data 补丁 + bin\HD2IndependentRig 原生文件」混合体：data 部分照常进 mods\，
            // 原生文件 HellForge 没有承载机制，由本管理器直写它的游戏目录（见 SyncNativeFilesAsync）。
            var iasMods = new Dictionary<string, (ProfileMod ProfileMod, Mod Mod)>(IdComparer);
            foreach (var profileMod in sequence)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!modsById.TryGetValue(profileMod.ModId, out var mod) || !Directory.Exists(mod.Path))
                {
                    skipped.Add(new HellForgeSyncSkip(mod?.Name ?? profileMod.ModId.ToString("N"), "模组文件缺失"));
                    MarkSyncedMod(profileMod, HellForgeModMark.NotSynced, "模组文件缺失");
                    continue;
                }
                // 只排除加载器本体（含 SmoothBoot）：HellForge 把 BSL/HD2Runtime/MDL3 等当普通模组由模组页自己管理
                // （upstream docs/lua-bridge.md），再同步一份就会出现同一个加载器两份。声明依赖加载器的 Lua 内容模组照常导出：
                // HellForge 判断 Lua 模组是“补丁包内含 lua 资源”（Mods.cs HoldsLua），补丁文件按原字节复制，Lua 因此随包落地，
                // HellForge 会标成 LUA 并归入 CLIENT ONLY。
                var role = BslProfilePolicy.Role(mod);
                if (role != BslModRole.None && (BslProfilePolicy.IsLoader(role) || role == BslModRole.SmoothBoot))
                {
                    skipped.Add(new HellForgeSyncSkip(mod.Name, "Lua 加载器由 HellForge 模组页管理，不参与同步", Expected: true));
                    MarkSyncedMod(profileMod, HellForgeModMark.NotSynced, "加载器由 HellForge 模组页管理");
                    continue;
                }
                if (IsIasMod(mod))
                {
                    iasMods[mod.Id.ToString("N")] = (profileMod, mod);
                    // 只有原生文件的组件包（例如纯物理/头盔）没有 data 补丁可给 HellForge：不进 mods\，只落原生文件。
                    if (PatchDeploymentPlanner.CollectSelectedFiles(mod, profileMod.OptionsJson, cancellationToken).Count == 0)
                    {
                        MarkSyncedMod(profileMod, HellForgeModMark.Flattened, "HD2IAS：只有原生文件，已直写 HellForge 游戏目录");
                        continue;
                    }
                }
                // HD2IAS 一律扁平导出：它的组件目录里混着 rigbin/hd2phys 等原生文件，透传成外观包会把它们当补丁塞进 HF，
                // 而补丁只走 data\；原生文件由本管理器直写游戏目录（见 SyncNativeFilesAsync）。
                synced.Add((profileMod, mod, IsIasMod(mod)
                    ? HellForgeModKind.Flat
                    : HellForgeModExporter.ResolveKind(mod)));
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
            // HD2IAS 的原生文件（rigbin/hd2phys/hd2shared/ini）直写 HellForge 游戏目录，与 mods\ 的 data 补丁一起构成完整模组。
            await SyncNativeFilesAsync(root, iasMods, installation, placer, warnings, cancellationToken);
            // 标记按“最后一次落盘的补丁”统计：外观包整组先加载、扁平模组整组后加载，跨组共用资源包时覆盖关系会翻转。
            var cosmeticBundles = new Dictionary<Guid, IReadOnlyCollection<string>>();
            var gameplayBundles = new Dictionary<Guid, IReadOnlyCollection<string>>();
            foreach (var (_, mod, kind) in synced)
            {
                var entry = owned[mod.Id.ToString("N")];
                var folder = foldersById.GetValueOrDefault(entry.HellforgeId)
                    ?? (local.TryGetValue(entry.HellforgeId, out var scanned) ? scanned.Folder : null);
                if (folder == null) continue;
                var target = kind == HellForgeModKind.Pack ? cosmeticBundles : gameplayBundles;
                target[mod.Id] = CollectBundleKeys(folder, cancellationToken);
            }
            var conflicts = HellForgeModMarkRules.FindCrossGroupConflicts(cosmeticBundles, gameplayBundles);
            foreach (var (profileMod, mod, kind) in synced)
            {
                if (IsIasMod(mod))
                {
                    // HD2IAS 的数据补丁在 mods\，物理/骨架靠本管理器直写的原生文件与 HD2IAS 运行时：两者缺一即不完整。
                    MarkSyncedMod(profileMod, HellForgeModMark.Flattened,
                        "HD2IAS：data 补丁经 mods\\ 同步，原生 rig 文件已直写 HellForge 游戏目录，需要 HD2IAS 运行时");
                    continue;
                }
                var others = conflicts.TryGetValue(mod.Id, out var set)
                    ? string.Join("、", set.Take(3).Select(id => modsById.TryGetValue(id, out var other) ? other.Name : id.ToString("N")))
                    : string.Empty;
                var mark = HellForgeModMarkRules.ResolveSynced(
                    flattened: kind == HellForgeModKind.Flat,
                    needsRuntime: IsLuaMod(mod),
                    orderSensitive: others.Length > 0);
                MarkSyncedMod(profileMod, mark, others.Length > 0 ? $"与其他模组修改同一资源包：{others}" : string.Empty);
            }
            installation.Marks = marks;
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
                // 按设计排除（加载器/依赖、HD2IAS）不算失败：文案里已列明原因，用真实问题决定整体状态。
                Status = skipped.Any(skip => !skip.Expected) ? DeploymentStatus.PartiallyFailed : DeploymentStatus.Completed,
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
            if (!state.Installations.TryGetValue(root, out var installation)
                || installation.Mods.Count == 0 && installation.NativeFiles.Count == 0)
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
                installation.Marks.Remove(key);
            }
            modsFile.Remove(ids);
            modsFile.SaveIfChanged();
            // HD2IAS 的原生文件同样只清理本管理器写过的那批；运行时自身与用户放的文件不在归属内。
            var gameRoot = Path.Combine(root, "game");
            foreach (var relative in installation.NativeFiles.Values
                         .SelectMany(list => list).Distinct(StringComparer.OrdinalIgnoreCase))
                TryDeleteFile(Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            installation.NativeFiles.Clear();
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
        IReadOnlyDictionary<Guid, HellForgeModMarkInfo>? marks = null;
        if (installation.Marks.Count > 0)
        {
            var map = new Dictionary<Guid, HellForgeModMarkInfo>();
            foreach (var (key, entry) in installation.Marks)
                if (Guid.TryParseExact(key, "N", out var modId)
                    && Enum.TryParse<HellForgeModMark>(entry.Mark, out var mark))
                    map[modId] = new HellForgeModMarkInfo(modId, mark, entry.Detail);
            if (map.Count > 0) marks = map;
        }
        return Task.FromResult(new HellForgeSyncStatus(true, installation.Mods.Count, installation.SyncedAt,
            installation.ProfileId, marks));
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

    /// <summary>Lua 资源 Type ID（同 ResourceTypeCatalog）：补丁内含 Lua 时 HellForge 需要 Bingus/HD2Runtime 运行时。</summary>
    private const ulong LuaResourceTypeId = 0xa14e8dfa2cd117e2UL;

    private static bool IsLuaMod(Mod mod) =>
        mod.ContainsScripts
        || mod.ResourceTypeIds?.Contains(LuaResourceTypeId) == true
        || mod.LogicalMod?.Components.Any(component => component.Type == ComponentType.Lua) == true;

    private static bool IsIasMod(Mod mod) =>
        string.Equals(mod.WorkflowType, Hd2IrIntegrationService.Workflow, StringComparison.OrdinalIgnoreCase);

    /// <summary>本管理器在 HellForge 游戏目录里写的原生文件（HD2IAS 的 rig/物理/隔离 ini 等）相对于 game 的路径。</summary>
    internal static string NativeTargetPath(string relative) => relative.Replace('\\', '/');

    /// <summary>
    /// 把 HD2IAS 模组的原生文件写进 HellForge 游戏目录：上游只认识 <c>data\</c> 补丁与声明的 native 编辑（网络字段位宽、
    /// 组件记录 u32），没有“把任意文件放进 bin\”的能力，所以这部分由本管理器按部署方式直写并登记归属。
    /// 只增改缺失/变化的文件，只删本管理器上次写过、本次不再需要的文件；绝不碰 HD2IAS 运行时自己的文件与用户文件。
    /// </summary>
    private async Task SyncNativeFilesAsync(string root,
        IReadOnlyDictionary<string, (ProfileMod ProfileMod, Mod Mod)> iasMods,
        HellForgeInstallationSync installation,
        HellForgeModFilePlacer placer,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var gameRoot = Path.Combine(root, "game");
        var desired = new Dictionary<string, List<string>>(IdComparer);
        var targets = new Dictionary<string, (string Source, string Key)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, (profileMod, mod)) in iasMods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<PatchDeploymentPlanner.FileEntry> entries;
            try
            {
                entries = await Hd2IrIntegrationService.GetNativeFilesAsync(mod, profileMod.OptionsJson, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
            {
                warnings.Add($"“{mod.Name}”的原生文件无法读取：{ex.Message}");
                continue;
            }

            var relativePaths = new List<string>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(entry.SourcePath) || !File.Exists(entry.SourcePath)) continue;
                var relative = NativeTargetPath(entry.FileName);
                if (targets.TryGetValue(relative, out var owner))
                {
                    if (!string.Equals(owner.Key, key, StringComparison.Ordinal))
                        warnings.Add($"“{mod.Name}”与另一个模组写入同一原生文件 {relative}，保留先写入的那份。");
                    continue;
                }
                targets[relative] = (entry.SourcePath, key);
                relativePaths.Add(relative);
            }
            if (relativePaths.Count > 0) desired[key] = relativePaths;
        }

        foreach (var (relative, (source, _)) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var sourceInfo = new FileInfo(source);
                var targetInfo = new FileInfo(destination);
                // 与 mods\ 的指纹同口径（长度 + mtime）：没变就不重写，保持硬链接/时间戳稳定。
                if (targetInfo.Exists && targetInfo.Length == sourceInfo.Length
                    && targetInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (targetInfo.Exists) File.Delete(destination);
                await placer.PlaceAsync(source, destination, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"写入原生文件 {relative} 失败：{ex.Message}");
            }
        }

        // 模组被禁用/移出、或组件选项取消后，上次写的原生文件不再归谁：删掉，别把孤儿文件留在游戏目录里。
        foreach (var relative in installation.NativeFiles.Values
                     .SelectMany(list => list).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (targets.ContainsKey(relative)) continue;
            TryDeleteFile(Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        installation.NativeFiles = desired;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>模组落盘目录里补丁文件的 16 位 bundle key；只看文件名，不读内容。</summary>
    private static HashSet<string> CollectBundleKeys(string folder, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return keys;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (name.Length <= 17 || name[16] != '.' || !name.AsSpan(17).StartsWith("patch_", StringComparison.Ordinal))
                continue;
            keys.Add(name[..16].ToLowerInvariant());
        }
        return keys;
    }

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
        {
            // 同一原因的模组并到一组，避免几十个加载器时原因重复到无法阅读。
            var groups = result.SkippedMods
                .GroupBy(skip => skip.Reason)
                .Select(group => $"{string.Join("、", group.Select(skip => skip.Name))}（{group.Key}）");
            message += $"；跳过 {result.SkippedMods.Count} 个：{string.Join("；", groups)}";
        }
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
    /// <summary>
    /// HD2IAS 模组写在 HellForge 游戏目录里的原生文件：模组 key → 相对 game 根的 posix 路径。
    /// HellForge 只认识 <c>data\</c> 补丁，没有承载 <c>bin\HD2IndependentRig\</c> 这类文件的机制，由本管理器直写并登记归属。
    /// </summary>
    public Dictionary<string, List<string>> NativeFiles { get; set; } = new();
    /// <summary>上次同步时算出的 模组 key → HellForge 标记；库页只在 HellForge 模式读取。</summary>
    public Dictionary<string, HellForgeModMarkRules.Entry> Marks { get; set; } = new();
}

internal sealed class HellForgeSyncedMod
{
    public string HellforgeId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
}