using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;
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
        var mod = await _mods.GetModByIdAsync(modId, token)
                  ?? throw new InvalidOperationException("模组不存在：" + modId);
        var gameRoot = _settings.GetEffectiveGamePath();
        if (string.IsNullOrWhiteSpace(gameRoot))
            throw new InvalidOperationException("未检测到游戏安装目录，无法读取原生材质与骨架。");
        var dataRoot = Path.Combine(gameRoot, "data");
        var gameData = Directory.Exists(dataRoot) ? dataRoot : gameRoot;
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
        var work = Path.Combine(Path.GetTempPath(), "HD2Mimnt-HD2CL", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var output = Path.Combine(work, "package");
        try
        {
            var arguments = new Dictionary<string, string>
            {
                ["Seed"] = seed,
                ["Name"] = productName ?? mod.Name,
                ["Author"] = mod.Author ?? string.Empty,
                ["Description"] = mod.Description ?? string.Empty,
                ["ModPath"] = mod.Path,
                ["GameDataRoot"] = gameData,
                ["OutputDirectory"] = output
            };
            if (!string.IsNullOrWhiteSpace(mod.ThumbnailPath)) arguments["ThumbnailPath"] = mod.ThumbnailPath;
            if (!string.IsNullOrWhiteSpace(mod.Manifest?.IconPath))
                arguments["ManifestIconPath"] = Path.Combine(mod.Path, mod.Manifest.IconPath);
            if (componentFolders is { Count: > 0 })
                arguments["Components"] = string.Join('|', componentFolders);
            if (optionGroups is not null)
                arguments["OptionGroups"] = JsonSerializer.Serialize(optionGroups);

            var outcome = await _tasks.RunProcessTaskAsync(
                $"转换「{productName ?? mod.Name}」为 HD2CL 角色库",
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

            var artifact = JsonSerializer.Deserialize<Hd2ClArtifactResult>(outcome.Data)
                           ?? throw new InvalidOperationException("HD2CL 转换结果无效。");
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
                if (optionGroups is not null)
                    await _profiles.SetModOptionsAsync(currentProfile.Id, imported,
                        ProductOptionState(optionGroups), token);
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
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
        var directory = Path.Combine(modPath, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory)) return false;
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
