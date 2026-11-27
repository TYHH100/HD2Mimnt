using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IO;
using HD2Mimnt.Infrastructure.ModelConversion;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// HD2CL（角色库）原生适配：扫描与候选列表留在主程序，
/// 编译、打包等高 CPU/内存步骤交给 HD2Mimnt.Worker 独立进程，完成后由主程序入库并调整当前配置。
/// </summary>
public sealed class Hd2ClConversionService : IHd2ClConversionService
{
    private readonly ModService _mods;
    private readonly ProfileService _profiles;
    private readonly SettingsService _settings;
    private readonly TaskService _tasks;

    public Hd2ClConversionService(ModService mods, ProfileService profiles, SettingsService settings,
        TaskService tasks)
    {
        _mods = mods;
        _profiles = profiles;
        _settings = settings;
        _tasks = tasks;
    }

    public async Task<IReadOnlyList<Hd2ClCandidate>> GetCandidatesAsync(CancellationToken token = default)
    {
        var mods = (await _mods.GetAllModsAsync(token)).ToList();
        return await Task.Run(() =>
        {
            var result = new List<Hd2ClCandidate>();
            foreach (var mod in mods)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(mod.Path)) continue;
                var companions = 0;
                foreach (var folder in Hd2ClArtifactBuilder.ComponentFolders(mod.Path))
                {
                    if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                        .Any(file => Hd2ClArtifactBuilder.IsCompanionFile(Path.GetFileName(file))))
                        companions++;
                }
                if (companions > 0) result.Add(new Hd2ClCandidate(mod.Id, mod.Name, mod.Path, companions));
            }
            return (IReadOnlyList<Hd2ClCandidate>)result;
        }, token);
    }

    public async Task<IReadOnlyList<Hd2ClVariant>> GetVariantsAsync(Guid modId, CancellationToken token = default)
    {
        var mod = await _mods.GetModByIdAsync(modId, token)
                  ?? throw new InvalidOperationException("模组不存在：" + modId);
        if (!Directory.Exists(mod.Path)) throw new InvalidOperationException("模组目录不存在：" + mod.Path);
        var groups = mod.Manifest?.OptionGroups ?? [];
        return await Task.Run(() => (IReadOnlyList<Hd2ClVariant>)Hd2ClVariantPlanner.Plan(mod.Path)
            .Select(plan =>
            {
                var group = groups.FirstOrDefault(item =>
                    item.Id.Equals("component-" + Path.GetFileName(plan.Id), StringComparison.OrdinalIgnoreCase));
                var name = Hd2ClNames.StripComponentPrefix(
                    string.IsNullOrWhiteSpace(group?.Name) ? plan.Name : group.Name);
                var description = string.IsNullOrWhiteSpace(group?.Description) ? null : group.Description;
                return new Hd2ClVariant(plan.Id, name, description, plan.Folders);
            }).ToList(), token);
    }

    public async Task<Hd2ClConversionResult> ConvertAsync(Guid modId, IReadOnlyList<string>? componentFolders = null,
        string? productName = null, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var plan = await PrepareAsync(modId, componentFolders, token);
        var work = CreateWorkDirectory();
        var output = Path.Combine(work, "package");
        try
        {
            var artifact = await BuildArtifactAsync(plan, output, productName, progress, token);
            if (!Directory.Exists(artifact.OutputDirectory))
                throw new InvalidOperationException("HD2CL 转换没有生成成品目录。");

            progress?.Report("导入管理器库…");
            var imported = await _mods.ImportModAsync(artifact.OutputDirectory, token);
            await _mods.AddTagAsync(imported, Hd2ClProductTag.Value, token);
            Guid? profileId = null;
            var sourceDisabled = false;
            var currentProfile = await _profiles.GetActiveProfileAsync(token);
            if (currentProfile is not null)
            {
                profileId = currentProfile.Id;
                await _profiles.AddModToProfileAsync(currentProfile.Id, imported, token);
                if (plan.OptionGroups is not null)
                    await _profiles.SetModOptionsAsync(currentProfile.Id, imported,
                        ProductOptionState(plan.OptionGroups), token);
                await _profiles.SetModEnabledAsync(currentProfile.Id, imported, true, token);
                await _profiles.SetModEnabledAsync(currentProfile.Id, modId, false, token);
                sourceDisabled = true;
            }
            progress?.Report(currentProfile is null
                ? "已导入模组库（当前没有配置文件，未禁用源模组）"
                : "已导入、加入当前配置并禁用源 IAS 模组");
            return new Hd2ClConversionResult(imported, artifact.Name, profileId, sourceDisabled);
        }
        finally
        {
            DeleteWorkDirectory(work);
        }
    }

    /// <summary>只导出压缩包：转换产物打成 7z 写到指定目录，不导入模组库、不改当前配置。</summary>
    public async Task<Hd2ClExportResult> ExportAsync(Guid modId, string outputDirectory,
        IReadOnlyList<string>? componentFolders = null, string? productName = null,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("请选择导出目录。", nameof(outputDirectory));
        var plan = await PrepareAsync(modId, componentFolders, token);
        var work = CreateWorkDirectory();
        var output = Path.Combine(work, "package");
        try
        {
            var artifact = await BuildArtifactAsync(plan, output, productName, progress, token);
            var exportName = Hd2ClArchiveExport.WithHd2ClMarker(artifact.Name);
            var archive = await Hd2ClArchiveExport.WriteAsync(outputDirectory, exportName,
                artifact.OutputDirectory, progress, token);
            progress?.Report("已导出压缩包：" + archive);
            return new Hd2ClExportResult(exportName, archive);
        }
        finally
        {
            DeleteWorkDirectory(work);
        }
    }

    private sealed record ConversionPlan(Mod Mod, string Seed, string GameData,
        IReadOnlyList<string>? Components, IReadOnlyList<Hd2ClOptionGroup>? OptionGroups);

    private async Task<ConversionPlan> PrepareAsync(Guid modId, IReadOnlyList<string>? componentFolders,
        CancellationToken token)
    {
        var mod = await _mods.GetModByIdAsync(modId, token)
                  ?? throw new InvalidOperationException("模组不存在：" + modId);
        var gameRoot = _settings.GetEffectiveGamePath();
        if (string.IsNullOrWhiteSpace(gameRoot))
            throw new InvalidOperationException("未检测到游戏安装目录，无法读取原生材质与骨架。");
        var dataRoot = Path.Combine(gameRoot, "data");
        var seed = mod.Manifest is { Guid: var guid } && guid != Guid.Empty ? guid.ToString() : mod.Name;
        var profile = await _profiles.GetActiveProfileAsync(token);
        // 带选项的模组（颜色/部件切换）把分支原样带进成品，由成品选项决定部署哪一套；
        // 显式传入的版本选择（合集浮窗）优先于选项。
        var explicitSelection = componentFolders is not null;
        var scope = ResolveConversionScope(mod,
            profile?.Mods.FirstOrDefault(item => item.ModId == modId)?.OptionsJson);
        componentFolders ??= scope.Components;
        var optionGroups = explicitSelection ? null : scope.OptionGroups;
        if (componentFolders is { Count: 0 })
            throw new InvalidOperationException("当前模组选项没有选中任何文件，无法转换。");
        return new ConversionPlan(mod, seed, Directory.Exists(dataRoot) ? dataRoot : gameRoot,
            componentFolders, optionGroups);
    }

    private async Task<Hd2ClArtifactResult> BuildArtifactAsync(ConversionPlan plan, string output,
        string? productName, IProgress<string>? progress, CancellationToken token)
    {
        var arguments = new Dictionary<string, string>
        {
            ["Seed"] = plan.Seed,
            ["Name"] = productName ?? plan.Mod.Name,
            ["Author"] = plan.Mod.Author ?? string.Empty,
            ["Description"] = plan.Mod.Description ?? string.Empty,
            ["ModPath"] = plan.Mod.Path,
            ["GameDataRoot"] = plan.GameData,
            ["OutputDirectory"] = output
        };
        if (!string.IsNullOrWhiteSpace(plan.Mod.ThumbnailPath)) arguments["ThumbnailPath"] = plan.Mod.ThumbnailPath;
        if (!string.IsNullOrWhiteSpace(plan.Mod.Manifest?.IconPath))
            arguments["ManifestIconPath"] = Path.Combine(plan.Mod.Path, plan.Mod.Manifest.IconPath);
        if (plan.Components is { Count: > 0 }) arguments["Components"] = string.Join('|', plan.Components);
        if (plan.OptionGroups is not null) arguments["OptionGroups"] = JsonSerializer.Serialize(plan.OptionGroups);

        var outcome = await _tasks.RunProcessTaskAsync(
            $"转换「{productName ?? plan.Mod.Name}」为 HD2CL 角色库",
            "Hd2ClConversion",
            arguments,
            new ProgressBridge(progress),
            cancellationToken: token);
        if (outcome.Task?.Status is HD2Mimnt.Core.Entities.TaskStatus.Cancelled
            or HD2Mimnt.Core.Entities.TaskStatus.ForceTerminated)
            throw new OperationCanceledException(token);
        if (outcome.Task?.Status != HD2Mimnt.Core.Entities.TaskStatus.Completed || string.IsNullOrEmpty(outcome.Data))
            throw new InvalidOperationException(outcome.Task?.Errors.FirstOrDefault()
                ?? outcome.Task?.Message ?? "HD2CL 转换失败。");
        return JsonSerializer.Deserialize<Hd2ClArtifactResult>(outcome.Data)
               ?? throw new InvalidOperationException("HD2CL 转换结果无效。");
    }

    private static string CreateWorkDirectory()
    {
        var work = Path.Combine(Path.GetTempPath(), "HD2Mimnt-HD2CL", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    private static void DeleteWorkDirectory(string work)
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private const string MaterialArchive = "9ba626afa44a3aa3";

    internal sealed record ConversionScope(IReadOnlyList<string>? Components,
        IReadOnlyList<Hd2ClOptionGroup>? OptionGroups);

    /// <summary>
    /// 带选项的模组：颜色/部件分支是纯材质补丁，原样带进成品并保留互斥选项；
    /// 分支里混入 Unit 等资源时退回“只转换当前选中分支”，避免成品资源互相覆盖。
    /// </summary>
    internal static ConversionScope ResolveConversionScope(Mod mod, string? optionsJson)
    {
        if (!ProfileModOptionResolver.HasOptions(mod.Manifest)) return new(null, null);
        var state = ProfileModOptionResolver.ResolveEffective(mod.Manifest, optionsJson);
        var plan = ProfileModOptionResolver.ResolveDeployPlan(mod.Manifest, state);
        if (plan.Scope == OptionDeployScope.InvalidSelection)
            throw new InvalidOperationException("模组选项选择与清单不匹配，请重新选择模组选项后再转换。");
        var selected = plan.Scope == OptionDeployScope.DirectoryList ? plan.Directories : null;
        if (mod.Manifest is not { ManifestVersion: ManifestVersion.V1, Options: { Count: > 0 } options })
            return new(selected, null);

        var toggles = state.OptionToggles;
        var selections = state.OptionSelections;
        if (options.All(option => option.SubOptions is not { Count: > 0 })
            && ResolveIndependentMaterialOptions(mod.Path, options, toggles) is { } materialScope)
            return materialScope;

        var modelDirectories = new List<string>();
        var groups = new List<Hd2ClOptionGroup>();
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            var enabled = index < (toggles?.Count ?? 0) ? toggles![index] : option.DefaultEnabled ?? true;
            if (option.SubOptions is { Count: > 0 } subOptions)
            {
                var group = MaterialGroup(mod.Path, option, subOptions, enabled,
                    index < (selections?.Count ?? 0) ? selections![index] : 0);
                if (group is null) return new(selected, null);
                groups.Add(group);
                continue;
            }
            if (option.Include is not { Count: > 0 }) return new(selected, null);
            if (enabled) modelDirectories.AddRange(option.Include);
        }
        if (groups.Count == 0) return new(selected, null);
        return modelDirectories.Count > 0 ? new(modelDirectories, groups) : new(selected, null);
    }

    /// <summary>
    /// 成品清单的第一个开关是根目录映射（模型与自带材质），状态表必须同步占位，
    /// 否则后续配色开关会整体错位，导入后默认选中项与源模组不一致。
    /// </summary>
    internal static ProfileModOptionsState ProductOptionState(IReadOnlyList<Hd2ClOptionGroup> groups)
        => new()
        {
            OptionToggles = new[] { true }.Concat(groups.Select(group => group.Enabled)).ToList(),
            OptionSelections = new[] { 0 }.Concat(groups.Select(group => group.DefaultIndex)).ToList()
        };

    private static ConversionScope? ResolveIndependentMaterialOptions(string modPath,
        List<ModOption> options, List<bool>? toggles)
    {
        if (options.Count < 2) return null;
        var choices = new List<Hd2ClOptionChoice>();
        var colorOptions = new List<(int Index, ModOption Option, string Material)>();
        var materialDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelUseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var modelDirectories = new List<string>();
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            if (option.Include is not { Count: > 0 } include) return null;
            var materials = include.Where(path => IsMaterialDirectory(modPath, path)).ToList();
            if (materials.Count > 1) return null;
            var enabled = index < (toggles?.Count ?? 0) ? toggles![index] : option.DefaultEnabled ?? true;
            if (materials.Count == 0)
            {
                if (enabled) modelDirectories.AddRange(include);
                continue;
            }
            if (!materialDirectories.Add(materials[0])) return null;
            colorOptions.Add((index, option, materials[0]));
        }
        if (colorOptions.Count < 2) return null;

        var defaultIndex = -1;
        foreach (var (index, option, material) in colorOptions)
        {
            var models = option.Include!.Except([material], StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (models.Count == 0) return null;
            foreach (var model in models)
                modelUseCounts[model] = modelUseCounts.GetValueOrDefault(model) + 1;
            choices.Add(new Hd2ClOptionChoice(option.Name, option.Image, material));
            var enabled = index < (toggles?.Count ?? 0) ? toggles![index] : option.DefaultEnabled ?? true;
            if (!enabled) continue;
            defaultIndex = choices.Count - 1;
            modelDirectories.AddRange(models);
        }
        // 某个模型目录只属于一个配色时，它可能是模型版本差异，不能合并成配色组。
        if (defaultIndex < 0 || modelUseCounts.Any(pair => pair.Value < 2)) return null;
        return new(modelDirectories.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            [new Hd2ClOptionGroup("材质配色", null, null, true, defaultIndex, choices)]);
    }

    private static Hd2ClOptionGroup? MaterialGroup(string modPath, ModOption option,
        List<ModSubOption> subOptions, bool enabled, int defaultIndex)
    {
        if (option.Include is { Count: > 0 }) return null;
        var choices = new List<Hd2ClOptionChoice>(subOptions.Count);
        foreach (var sub in subOptions)
        {
            if (sub.SubOptions is { Count: > 0 } || sub.Include is not { Count: 1 }) return null;
            if (!IsMaterialDirectory(modPath, sub.Include[0])) return null;
            choices.Add(new Hd2ClOptionChoice(sub.Name, sub.Image, sub.Include[0]));
        }
        return new Hd2ClOptionGroup(option.Name, option.Description, option.Image, enabled,
            Math.Clamp(defaultIndex, 0, choices.Count - 1), choices);
    }

    /// <summary>分支目录只含材质/贴图补丁（可选图标），进入成品后才能安全复制成选项。</summary>
    private static bool IsMaterialDirectory(string modPath, string relative)
    {
        var directory = Path.GetFullPath(Path.Combine(modPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathGuard.IsInsideRoot(modPath, directory) || !Directory.Exists(directory)) return false;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (Hd2ClArtifactBuilder.IsCompanionFile(name)) return false;
            if (Hd2ClArtifactBuilder.IsPatchFile(name)
                && !name.StartsWith(MaterialArchive, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>把任务中心的 Worker 进度转成 UI 文本；观察者异常不得影响任务终态。</summary>
    private sealed class ProgressBridge(IProgress<string>? target) : IProgress<TaskProgress>
    {
        public void Report(TaskProgress value)
        {
            if (target is null || string.IsNullOrWhiteSpace(value.Stage)) return;
            try { target.Report(value.Stage); } catch { }
        }
    }
}
