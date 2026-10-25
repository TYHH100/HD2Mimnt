using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

internal sealed record PlannedResourceFile(
    string Suffix,
    string SourcePath,
    string SourceRelativePath,
    string SourceKey,
    DeploymentMethod Method,
    Guid? ExistingFileId = null,
    bool TrustExisting = false);

internal sealed record PlannedResource(string Prefix, IReadOnlyList<PlannedResourceFile> Files);

internal sealed record PlannedFixedFile(
    string TargetRelativePath,
    string SourcePath,
    string SourceRelativePath,
    string SourceKey,
    DeploymentMethod Method,
    Guid? ExistingFileId = null,
    bool TrustExisting = false);

internal sealed record PlannedMod(
    Mod Mod,
    Guid ProfileId,
    string? OptionsSnapshotJson,
    int OrderIndex,
    string? ModVersion,
    DeploymentMethod Method,
    string Workflow,
    List<PlannedResource> Patches,
    List<PlannedFixedFile> Fixed);

internal sealed record PlannedFile(
    Guid ModId,
    string SourcePath,
    string SourceRelativePath,
    string SourceKey,
    string TargetPath,
    DeploymentMethod Method,
    Guid? ExistingFileId,
    bool TrustExisting,
    bool IsPatchBase);

internal sealed class DeploymentPlan
{
    public required Guid SessionId { get; init; }
    public required Guid ProfileId { get; init; }
    public required string GamePath { get; init; }
    public required DeploymentMethod Method { get; init; }
    public required DeploymentOrder Order { get; init; }
    public required int TotalMods { get; set; }
    public Profile? Profile { get; init; }
    public string DataPath => Path.Combine(GamePath, "data");
    public List<PlannedMod> Mods { get; } = [];
    public List<PlannedFile> Files { get; } = [];
    public List<string> SkippedModNames { get; } = [];
}

/// <summary>
/// 生成“期望安装状态”：完整配置计划用于手动部署，当前状态+单个更新模组用于自动增量部署。
/// </summary>
internal sealed class DeploymentPlanBuilder
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] SuffixOrder = ["", ".gpu_resources", ".stream"];

    public async Task<DeploymentPlan> BuildProfilePlanAsync(
        Profile profile,
        IReadOnlyDictionary<Guid, Mod> modsById,
        DeploymentMethod method,
        DeploymentOrder order,
        string gamePath,
        bool lockLoaders,
        IReadOnlySet<string> trackedTargets,
        CancellationToken cancellationToken = default)
    {
        var normalizedGamePath = NormalizeGamePath(gamePath);
        var plan = new DeploymentPlan
        {
            SessionId = Guid.NewGuid(),
            ProfileId = profile.Id,
            GamePath = normalizedGamePath,
            Method = method,
            Order = order,
            TotalMods = 0,
            Profile = profile
        };

        var ordered = BslProfilePolicy.DeploymentSequence(profile, modsById, order, lockLoaders);
        plan.TotalMods = ordered.Count;
        var nativeTargets = new Dictionary<string, string>(PathComparer);

        foreach (var profileMod in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            modsById.TryGetValue(profileMod.ModId, out var mod);
            if (mod == null || !Directory.Exists(mod.Path))
            {
                plan.SkippedModNames.Add(mod?.Name ?? profileMod.ModId.ToString());
                continue;
            }

            var patches = BuildPatchResources(mod, profileMod.OptionsJson, method, cancellationToken);
            var native = mod.WorkflowType == Hd2IrIntegrationService.Workflow
                ? await Hd2IrIntegrationService.GetNativeFilesAsync(mod, profileMod.OptionsJson, cancellationToken)
                : [];
            var fixedFiles = BuildNativeFiles(mod, native, method, normalizedGamePath, trackedTargets,
                nativeTargets, cancellationToken);
            if (patches.Count == 0 && fixedFiles.Count == 0)
            {
                plan.SkippedModNames.Add(mod.Name);
                continue;
            }

            plan.Mods.Add(new PlannedMod(mod, plan.ProfileId, profileMod.OptionsJson,
                plan.Mods.Count, mod.Version, method, mod.WorkflowType ?? string.Empty, patches, fixedFiles));
        }

        ValidateLoaders(ordered, modsById, plan);
        if (plan.Mods.Count == 0 && plan.SkippedModNames.Count > 0)
            throw new InvalidOperationException(
                $"当前配置没有可部署的补丁，跳过 {plan.SkippedModNames.Count} 个模组：{string.Join("、", plan.SkippedModNames)}");

        AssignTargets(plan);
        return plan;
    }

    /// <summary>
    /// 自动增量计划：其他模组完全沿用当前 DeploymentFiles，仅重新规划被更新的模组；
    /// 不读取当前配置里尚未部署的启用/排序/选项。
    /// </summary>
    public async Task<DeploymentPlan> BuildTargetedPlanAsync(
        string gamePath,
        Guid changedModId,
        Profile deployedProfile,
        IReadOnlyList<DeploymentRecord> currentRecords,
        IReadOnlyDictionary<Guid, Mod> modsById,
        DeploymentMethod fallbackMethod,
        DeploymentOrder order,
        CancellationToken cancellationToken = default)
    {
        var normalizedGamePath = NormalizeGamePath(gamePath);
        var changed = currentRecords.FirstOrDefault(record => record.ModId == changedModId)
            ?? throw new InvalidOperationException("该模组当前没有部署记录，已跳过自动部署。");
        var changedMethod = ParseMethod(changed.DeploymentMethod) ?? fallbackMethod;
        var plan = new DeploymentPlan
        {
            SessionId = Guid.NewGuid(),
            ProfileId = changed.ProfileId,
            GamePath = normalizedGamePath,
            Method = changedMethod,
            Order = ParseOrder(changed.DeploymentOrder) ?? order,
            TotalMods = currentRecords.Count,
            Profile = deployedProfile
        };

        var trackedTargets = currentRecords.SelectMany(record => record.Files)
            .Select(file => NormalizePath(file.TargetPath))
            .OfType<string>()
            .ToHashSet(PathComparer);
        var nativeTargets = new Dictionary<string, string>(PathComparer);

        foreach (var record in currentRecords
                     .OrderBy(record => record.DeploymentOrderIndex ?? int.MaxValue)
                     .ThenBy(record => record.StartTime)
                     .ThenBy(record => record.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!modsById.TryGetValue(record.ModId, out var mod))
                throw new InvalidOperationException(
                    $"当前部署中的模组 {record.ModId} 已不在模组库，无法安全自动部署，请手动部署或清理部署。");

            if (record.ModId == changedModId)
            {
                var patches = BuildPatchResources(mod, record.OptionsSnapshotJson, changedMethod, cancellationToken);
                var native = mod.WorkflowType == Hd2IrIntegrationService.Workflow
                    ? await Hd2IrIntegrationService.GetNativeFilesAsync(mod, record.OptionsSnapshotJson, cancellationToken)
                    : [];
                var fixedFiles = BuildNativeFiles(mod, native, changedMethod, normalizedGamePath, trackedTargets,
                    nativeTargets, cancellationToken);
                plan.Mods.Add(new PlannedMod(mod, record.ProfileId, record.OptionsSnapshotJson,
                    record.DeploymentOrderIndex ?? plan.Mods.Count, mod.Version, changedMethod,
                    mod.WorkflowType ?? record.Workflow, patches, fixedFiles));
                continue;
            }

            var current = BuildCurrentMod(record, mod, normalizedGamePath, fallbackMethod);
            foreach (var fixedFile in current.Fixed)
                RegisterNativeTarget(fixedFile.TargetRelativePath, normalizedGamePath, mod.Name, trackedTargets, nativeTargets);
            plan.Mods.Add(current);
        }

        AssignTargets(plan);
        ValidateTargetedLoaderRole(plan, changedModId);
        return plan;
    }

    /// <summary>更新可能改变加载器身份；自动部署仍要阻止把已部署配置变成双加载器或缺加载器。</summary>
    private static void ValidateTargetedLoaderRole(DeploymentPlan plan, Guid changedModId)
    {
        var changed = plan.Mods.SingleOrDefault(mod => mod.Mod.Id == changedModId);
        if (changed == null) return;
        var role = BslProfilePolicy.Role(changed.Mod);
        var otherLoader = plan.Mods.Any(mod => mod.Mod.Id != changedModId
            && BslProfilePolicy.IsLoader(BslProfilePolicy.Role(mod.Mod)));
        if (BslProfilePolicy.IsLoader(role) && otherLoader)
            throw new InvalidOperationException("更新后该模组成为加载器，但当前配置已有另一个加载器（BSL / MDL / BML 三选一），已跳过自动部署");
        if (!BslProfilePolicy.IsLoader(role) && (role is BslModRole.Dependency or BslModRole.SmoothBoot)
            && !plan.Mods.Any(mod => BslProfilePolicy.IsLoader(BslProfilePolicy.Role(mod.Mod))))
            throw new InvalidOperationException("更新后该模组需要加载器，但当前部署中没有 BSL / MDL / BML，已跳过自动部署");
    }

    private static PlannedMod BuildCurrentMod(DeploymentRecord record, Mod mod, string gamePath,
        DeploymentMethod fallbackMethod)
    {
        var recordMethod = ParseMethod(record.DeploymentMethod) ?? fallbackMethod;
        var patchGroups = new Dictionary<string, SortedDictionary<int, List<PlannedResourceFile>>>(PathComparer);
        var fixedFiles = new List<PlannedFixedFile>();
        var dataPath = Path.Combine(gamePath, "data");

        foreach (var file in record.Files)
        {
            var method = ParseMethod(file.Method) ?? recordMethod;
            var sourceKey = string.IsNullOrWhiteSpace(file.SourceKey) ? null : file.SourceKey;
            if (IsUnder(file.TargetPath, dataPath)
                && PatchDeploymentPlanner.TryParseTarget(file.TargetPath, out var prefix, out var index, out var suffix))
            {
                sourceKey ??= PatchDeploymentPlanner.MakePatchSourceKey(
                    string.IsNullOrWhiteSpace(file.SourceRelativePath) ? Path.GetFileName(file.SourcePath) : file.SourceRelativePath);
                if (!patchGroups.TryGetValue(prefix, out var byIndex))
                    patchGroups[prefix] = byIndex = new SortedDictionary<int, List<PlannedResourceFile>>();
                if (!byIndex.TryGetValue(index, out var files))
                    byIndex[index] = files = [];
                files.Add(new PlannedResourceFile(suffix, file.SourcePath,
                    file.SourceRelativePath ?? Path.GetFileName(file.SourcePath) ?? string.Empty,
                    sourceKey, method, file.Id, true));
                continue;
            }

            if (IsUnder(file.TargetPath, dataPath))
                throw new InvalidDataException($"当前部署包含无法识别的补丁目标：{file.TargetPath}，请手动部署。");
            var relative = Path.GetRelativePath(gamePath, file.TargetPath);
            if (relative.StartsWith("..", StringComparison.Ordinal))
                throw new InvalidDataException($"部署目标不在当前游戏目录：{file.TargetPath}，请手动部署。");
            sourceKey ??= PatchDeploymentPlanner.MakeNativeSourceKey(relative);
            fixedFiles.Add(new PlannedFixedFile(relative.Replace('\\', '/'), file.SourcePath,
                file.SourceRelativePath ?? relative, sourceKey, method, file.Id, true));
        }

        var patches = patchGroups
            .SelectMany(pair => pair.Value.Select(item =>
                new PlannedResource(pair.Key, item.Value.OrderBy(file => SuffixRank(file.Suffix)).ToList())))
            .ToList();
        return new PlannedMod(mod, record.ProfileId, record.OptionsSnapshotJson,
            record.DeploymentOrderIndex ?? 0, record.ModVersion, recordMethod,
            record.Workflow ?? string.Empty, patches, fixedFiles);
    }

    private static List<PlannedResource> BuildPatchResources(Mod mod, string? optionsJson,
        DeploymentMethod method, CancellationToken cancellationToken)
    {
        var selected = PatchDeploymentPlanner.CollectSelectedFiles(mod, optionsJson, cancellationToken);
        var entries = PatchDeploymentPlanner.BuildFiles(selected, new Dictionary<string, int>(PathComparer),
            cancellationToken, mod.Path);
        // 主文件为空或缺失时必须在改动旧部署之前失败。
        foreach (var entry in entries.Where(entry => entry.Suffix.Length == 0))
        {
            var info = new FileInfo(entry.SourcePath!);
            if (!info.Exists || info.Length == 0)
                throw new InvalidDataException($"补丁主文件不存在或为空：{entry.SourcePath}");
        }

        return entries.GroupBy(entry => entry.ResourceOrdinal)
            .OrderBy(group => group.Key)
            .Select(group => new PlannedResource(
                group.First().Prefix,
                group.OrderBy(entry => SuffixRank(entry.Suffix))
                    .Select(entry => new PlannedResourceFile(
                        entry.Suffix,
                        entry.SourcePath!,
                        entry.SourceRelativePath ?? Path.GetFileName(entry.SourcePath) ?? string.Empty,
                        entry.SourceKey ?? PatchDeploymentPlanner.MakePatchSourceKey(
                            entry.SourceRelativePath ?? Path.GetFileName(entry.SourcePath) ?? string.Empty),
                        method))
                    .ToList()))
            .ToList();
    }

    private static List<PlannedFixedFile> BuildNativeFiles(Mod mod, IReadOnlyList<PatchDeploymentPlanner.FileEntry> files,
        DeploymentMethod method, string gamePath, IReadOnlySet<string> trackedTargets,
        IDictionary<string, string> nativeTargets, CancellationToken cancellationToken)
    {
        var result = new List<PlannedFixedFile>();
        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FileName.Replace('\\', '/');
            RegisterNativeTarget(relative, gamePath, mod.Name, trackedTargets, nativeTargets);
            result.Add(new PlannedFixedFile(relative, entry.SourcePath!,
                entry.SourceRelativePath ?? relative,
                entry.SourceKey ?? PatchDeploymentPlanner.MakeNativeSourceKey(relative),
                method));
        }
        return result;
    }

    private static void RegisterNativeTarget(string relativeTarget, string gamePath, string modName,
        IReadOnlySet<string> trackedTargets, IDictionary<string, string> nativeTargets)
    {
        var target = NormalizePath(Path.Combine(gamePath, relativeTarget));
        if (target == null) throw new InvalidDataException($"HD2IAS 目标无效：{relativeTarget}");
        if (nativeTargets.TryGetValue(target, out var other))
            throw new InvalidOperationException($"HD2IAS 组件目标重复：{relativeTarget}（{other} / {modName}），请在配置选项中只启用一份。");
        if (Path.Exists(target) && !trackedTargets.Contains(target))
            throw new IOException($"HD2IAS 目标已存在且不属于当前部署：{relativeTarget}，请先备份并处理原有文件。");
        nativeTargets[target] = modName;
    }

    private static void AssignTargets(DeploymentPlan plan)
    {
        var nextIndexes = new Dictionary<string, int>(PathComparer);
        var targets = new HashSet<string>(PathComparer);
        foreach (var mod in plan.Mods)
        {
            var keys = new HashSet<string>(PathComparer);
            foreach (var resource in mod.Patches)
            {
                var index = nextIndexes.GetValueOrDefault(resource.Prefix);
                foreach (var file in resource.Files)
                {
                    var target = NormalizePath(Path.Combine(plan.DataPath,
                        $"{resource.Prefix}.patch_{index}{file.Suffix}"))!;
                    AddPlannedFile(plan, targets, keys, new PlannedFile(mod.Mod.Id, file.SourcePath,
                        file.SourceRelativePath, file.SourceKey, target, file.Method, file.ExistingFileId,
                        file.TrustExisting, file.Suffix.Length == 0));
                }
                nextIndexes[resource.Prefix] = index + 1;
            }

            foreach (var file in mod.Fixed)
            {
                var target = NormalizePath(Path.Combine(plan.GamePath, file.TargetRelativePath))!;
                AddPlannedFile(plan, targets, keys, new PlannedFile(mod.Mod.Id, file.SourcePath,
                    file.SourceRelativePath, file.SourceKey, target, file.Method, file.ExistingFileId,
                    file.TrustExisting, false));
            }
        }
    }

    private static void AddPlannedFile(DeploymentPlan plan, HashSet<string> targets, HashSet<string> keys,
        PlannedFile file)
    {
        if (!keys.Add(file.SourceKey))
            throw new InvalidDataException($"模组 {file.ModId} 存在重复文件身份：{file.SourceKey}");
        if (!targets.Add(file.TargetPath))
            throw new InvalidOperationException($"部署目标重复：{file.TargetPath}");
        plan.Files.Add(file);
    }

    private static void ValidateLoaders(IReadOnlyList<ProfileMod> ordered,
        IReadOnlyDictionary<Guid, Mod> modsById, DeploymentPlan plan)
    {
        var enabledLoaders = ordered.Count(item => modsById.TryGetValue(item.ModId, out var mod)
            && BslProfilePolicy.IsLoader(BslProfilePolicy.Role(mod)));
        if (enabledLoaders > 1)
            throw new InvalidOperationException("BSL / MDL / Boot Mod Loader 互斥，同一配置只能启用一个加载器，请停用另一份或重复副本");
        var loaderCount = plan.Mods.Count(mod => BslProfilePolicy.IsLoader(BslProfilePolicy.Role(mod.Mod)));
        if (loaderCount > 1)
            throw new InvalidOperationException("同一配置只能部署一个 BSL / MDL / Boot Mod Loader 加载器，请停用重复副本");
        if (loaderCount == 0 && plan.Mods.Any(mod =>
                BslProfilePolicy.Role(mod.Mod) is BslModRole.Dependency or BslModRole.SmoothBoot))
            throw new InvalidOperationException("此配置包含依赖加载器模组或 SmoothBoot，请先添加并启用 Bingus Shared Loader、MDL 或 Boot Mod Loader 及其补丁选项");
    }

    private static int SuffixRank(string suffix)
    {
        var index = Array.FindIndex(SuffixOrder, item => item.Equals(suffix, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : SuffixOrder.Length;
    }

    internal static DeploymentMethod? ParseMethod(string? value)
        => Enum.TryParse<DeploymentMethod>(value, out var method) && Enum.IsDefined(method) ? method : null;

    internal static DeploymentOrder? ParseOrder(string? value)
        => Enum.TryParse<DeploymentOrder>(value, out var order) && Enum.IsDefined(order) ? order : null;

    internal static string NormalizeGamePath(string gamePath)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));

    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(root);
        if (normalizedPath == null || normalizedRoot == null) return false;
        if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
