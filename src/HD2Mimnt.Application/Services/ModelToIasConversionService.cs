using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IO;
using HD2Mimnt.Infrastructure.ModelConversion;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

public sealed class ModelToIasConversionService(
    ModService mods,
    ProfileService profiles,
    SettingsService settings,
    TaskService tasks,
    StorageSettingsService storage,
    Microsoft.Extensions.Logging.ILogger<ModelToIasConversionService>? logger = null) : IModelToIasConversionService
{
    public async Task<IReadOnlyList<ModelToIasCandidate>> GetCandidatesAsync(CancellationToken token = default)
    {
        var all = await mods.GetAllModsAsync(token);
        return await Task.Run(() =>
        {
            var result = new List<ModelToIasCandidate>();
            foreach (var mod in all)
            {
                token.ThrowIfCancellationRequested();
                if (mod.WorkflowType == Hd2IrIntegrationService.Workflow
                    || mod.WorkflowType == "HD2CL"
                    || mod.Tags.Contains(ModelToIasProductTag.Value, StringComparer.OrdinalIgnoreCase)
                    || !Directory.Exists(mod.Path)) continue;
                var branches = BuildBranches(mod);
                var count = IasModelToIasConverter.CountRecognizedUnits(mod.Path, branches);
                if (count > 0) result.Add(new ModelToIasCandidate(mod.Id, mod.Name, mod.Path, count));
            }
            return (IReadOnlyList<ModelToIasCandidate>)result;
        }, token);
    }

    public async Task<ModelToIasConversionResult> ConvertAsync(Guid modId, string? productName = null,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var mod = await mods.GetModByIdAsync(modId, token)
                  ?? throw new InvalidOperationException("模组不存在：" + modId);
        if (!Directory.Exists(mod.Path)) throw new InvalidOperationException("模组目录不存在：" + mod.Path);

        var gameRoot = settings.GetEffectiveGamePath();
        if (string.IsNullOrWhiteSpace(gameRoot))
            throw new InvalidOperationException("未检测到游戏安装目录，无法读取原版 Avatar 骨架。");
        var gameData = Path.Combine(gameRoot, "data");
        if (!Directory.Exists(gameData)) throw new InvalidOperationException("游戏 data 目录不存在：" + gameData);
        var branches = BuildBranches(mod);
        if (IasModelToIasConverter.CountRecognizedUnits(mod.Path, branches) == 0)
            throw new InvalidOperationException("该模组没有找到可转换的护甲/头盔 Unit。");
        var name = string.IsNullOrWhiteSpace(productName) ? mod.Name + " · IAS" : productName.Trim();
        var seed = mod.Manifest is { Guid: var guid } && guid != Guid.Empty ? guid.ToString() : mod.Path;
        var work = Path.Combine(storage.Current.TempPath, "HD2Mimnt-ModelToIAS", Guid.NewGuid().ToString("N"));
        var output = Path.Combine(work, "package");
        Directory.CreateDirectory(work);
        try
        {
            var arguments = new Dictionary<string, string>
            {
                ["Seed"] = seed,
                ["Name"] = name,
                ["Author"] = mod.Author ?? string.Empty,
                ["Description"] = mod.Description ?? string.Empty,
                ["ModPath"] = mod.Path,
                ["GameDataRoot"] = gameData,
                ["OutputDirectory"] = output,
                ["Branches"] = JsonSerializer.Serialize(branches)
            };
            if (!string.IsNullOrWhiteSpace(mod.ThumbnailPath)) arguments["ThumbnailPath"] = mod.ThumbnailPath;
            if (!string.IsNullOrWhiteSpace(mod.Manifest?.IconPath))
            {
                var icon = Path.GetFullPath(Path.Combine(mod.Path, mod.Manifest.IconPath));
                arguments["ManifestIconPath"] = PathGuard.ResolveInsideRoot(mod.Path, icon,
                    "模组封面路径超出模组目录。");
            }
            var outcome = await tasks.RunProcessTaskAsync(
                $"转换「{mod.Name}」为 IAS 模型", "ModelToIasConversion", arguments,
                new ProgressBridge(progress), cancellationToken: token);
            if (outcome.Task?.Status is Core.Entities.TaskStatus.Cancelled or Core.Entities.TaskStatus.ForceTerminated)
                throw new OperationCanceledException(token);
            if (outcome.Task?.Status != Core.Entities.TaskStatus.Completed || string.IsNullOrEmpty(outcome.Data))
                throw new InvalidOperationException(outcome.Task?.Errors.FirstOrDefault()
                    ?? outcome.Task?.Message ?? "普通模型转 IAS 失败。");
            var artifact = JsonSerializer.Deserialize<IasModelToIasResult>(outcome.Data)
                           ?? throw new InvalidOperationException("普通模型转 IAS 结果无效。");
            if (!Directory.Exists(artifact.OutputDirectory))
                throw new InvalidOperationException("普通模型转 IAS 没有生成成品目录。");

            progress?.Report("导入管理器库…");
            var imported = await mods.ImportModAsync(artifact.OutputDirectory, token);
            await mods.AddTagAsync(imported, ModelToIasProductTag.Value, token);
            Guid? profileId = null;
            var sourceDisabled = false;
            var currentProfile = await profiles.GetActiveProfileAsync(token);
            if (currentProfile is not null)
            {
                profileId = currentProfile.Id;
                await profiles.ReplaceModWithAsync(currentProfile.Id, modId, imported, token);
                sourceDisabled = true;
            }
            progress?.Report(sourceDisabled
                ? "已导入、加入当前配置并禁用源普通模型；请重新部署并重启游戏。"
                : "已导入模组库；当前没有配置文件，源模组未改动。");
            return new ModelToIasConversionResult(imported, artifact.Name, profileId, sourceDisabled);
        }
        finally
        {
            DeleteWorkDirectory(work);
        }
    }

    internal static IReadOnlyList<ModelToIasBranch> BuildBranches(Mod mod)
    {
        var result = new List<ModelToIasBranch>();
        void Add(string name, IEnumerable<string>? directories)
        {
            var values = directories?.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (values is not { Count: > 0 }) return;
            if (result.Any(branch => branch.Name == name && branch.Directories.SequenceEqual(values, StringComparer.OrdinalIgnoreCase))) return;
            result.Add(new ModelToIasBranch(name, values));
        }

        if (mod.Manifest is not { ManifestVersion: ManifestVersion.V1, Options: { Count: > 0 } options })
        {
            if (mod.Manifest is { ManifestVersion: ManifestVersion.Legacy, LegacyOptions: { Count: > 0 } legacy })
            {
                foreach (var name in legacy) Add(name, [name]);
            }
            else
            {
                Add(mod.Name, ["."]);
                if (Directory.Exists(mod.Path))
                    foreach (var directory in Directory.EnumerateDirectories(mod.Path, "*", SearchOption.TopDirectoryOnly)
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                        Add(Path.GetFileName(directory), [Path.GetFileName(directory)]);
            }
            return result;
        }
        void Walk(string name, IEnumerable<string>? include, IReadOnlyList<ModSubOption>? subOptions)
        {
            Add(name, include);
            if (subOptions is null) return;
            foreach (var sub in subOptions)
                Walk(name + "/" + sub.Name, sub.Include, sub.SubOptions);
        }
        foreach (var option in options)
            Walk(option.Name, option.Include, option.SubOptions);
        return result;
    }

    private void DeleteWorkDirectory(string work)
    {
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "普通模型转 IAS 临时目录清理失败：{WorkDirectory}", work);
        }
    }

    private sealed class ProgressBridge(IProgress<string>? target) : IProgress<TaskProgress>
    {
        public void Report(TaskProgress value)
        {
            if (target is null || string.IsNullOrWhiteSpace(value.Stage)) return;
            try { target.Report(value.Stage); } catch { }
        }
    }
}
