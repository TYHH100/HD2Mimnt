using System.Text.Json;
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
            var profile = await _profiles.GetActiveProfileAsync(token);
            if (profile is not null)
            {
                profileId = profile.Id;
                await _profiles.AddModToProfileAsync(profile.Id, imported, token);
                await _profiles.SetModEnabledAsync(profile.Id, imported, true, token);
                await _profiles.SetModEnabledAsync(profile.Id, modId, false, token);
                sourceDisabled = true;
            }
            progress?.Report(profile is null
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
