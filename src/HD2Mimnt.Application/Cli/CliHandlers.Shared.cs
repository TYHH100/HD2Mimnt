using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Application.Cli;

/// <summary>各命令 handler 共用的解析工具：id/名称解析、枚举转换、表格渲染。</summary>
internal static class CliResolve
{
    /// <summary>参数既可以是完整 GUID，也可以是模组名称（全名唯一优先，其次唯一子串）。</summary>
    public static async Task<Mod?> FindModAsync(ModService modService, string idOrName, CancellationToken ct)
    {
        if (Guid.TryParse(idOrName, out var id))
        {
            var byId = await modService.GetModByIdAsync(id, ct);
            if (byId != null) return byId;
        }
        var all = (await modService.GetAllModsAsync(ct)).ToList();
        var exact = all.Where(mod => string.Equals(mod.Name, idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw new InvalidOperationException($"名称“{idOrName}”对应 {exact.Count} 个模组，请改用完整 GUID。");
        var partial = all.Where(mod => mod.Name.Contains(idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];
        if (partial.Count > 1) throw new InvalidOperationException($"“{idOrName}”匹配到 {partial.Count} 个模组，请改用完整 GUID。");
        return null;
    }

    public static async Task<Profile?> FindProfileAsync(ProfileService profileService, string idOrName, CancellationToken ct)
    {
        if (Guid.TryParse(idOrName, out var id))
        {
            var byId = await profileService.GetProfileByIdAsync(id, ct);
            if (byId != null) return byId;
        }
        var all = (await profileService.GetAllProfilesAsync(ct)).ToList();
        var exact = all.Where(profile => string.Equals(profile.Name, idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw new InvalidOperationException($"名称“{idOrName}”对应 {exact.Count} 个配置，请改用完整 GUID。");
        return null;
    }

    public static DeploymentMethod ParseMethod(string? value)
        => value?.ToLowerInvariant() switch
        {
            null or "" or "copy" => DeploymentMethod.Copy,
            "symlink" or "symboliclink" => DeploymentMethod.SymbolicLink,
            "hardlink" => DeploymentMethod.HardLink,
            _ => throw new ArgumentException($"未知部署方式“{value}”，可选 copy|symlink|hardlink。")
        };

    public static DeploymentOrder ParseOrder(string? value)
        => value?.ToLowerInvariant() switch
        {
            null or "" or "top" or "tobottom" or "tobottomtop" => DeploymentOrder.TopToBottom,
            "bottom" or "bottomtotop" => DeploymentOrder.BottomToTop,
            _ => throw new ArgumentException($"未知部署方向“{value}”，可选 top|bottom。")
        };

    /// <summary>人类可读的简单两列表格；Data 已承载结构化结果，这里只做辅助排版。</summary>
    public static string Table(params (string Left, string Right)[] rows)
    {
        var width = rows.Length == 0 ? 0 : rows.Max(row => row.Left.Length);
        return string.Join("\n", rows.Select(row => $"  {row.Left.PadRight(width)}  {row.Right}"));
    }

    /// <summary>TaskService/Worker 的 TaskProgress 同步桥到 CLI 进度会话（同步回调避免线程池排队乱序）。</summary>
    public static IProgress<TaskProgress> TaskProgressBridge(CliCommandSession session)
        => new TaskProgressBridgeImpl(session);

    /// <summary>同步部署进度桥：DeploymentProgress 聚合为阶段 + 百分比 + 当前模组。</summary>
    public static IProgress<DeploymentProgress> DeploymentProgressBridge(CliCommandSession session)
        => new DeploymentProgressBridgeImpl(session);

    /// <summary>同步导入进度桥：ModImportProgress（百分比/步骤/明细）映射为 CLI 进度事件。</summary>
    public static IProgress<ModImportProgress> ImportProgressBridge(CliCommandSession session)
        => new ImportProgressBridgeImpl(session);

    /// <summary>长任务 --async 的宿主守卫：无 TaskService 的宿主（旧 headless）明确报错而不是静默退化。</summary>
    public static TaskService RequireTaskService(CliCommandContext context)
        => context.GetOrDefault<TaskService>()
           ?? throw new InvalidOperationException("--async 需要 Agent/UI 宿主提供任务中心；当前宿主不支持后台任务。");

    private sealed class TaskProgressBridgeImpl(CliCommandSession session) : IProgress<TaskProgress>
    {
        public void Report(TaskProgress value)
            => session.Progress.Report(new CliProgressEvent(
                value.Stage, value.Progress, value.CurrentItem, value.Message));
    }

    private sealed class DeploymentProgressBridgeImpl(CliCommandSession session) : IProgress<DeploymentProgress>
    {
        public void Report(DeploymentProgress value)
        {
            double? percent = value.TotalMods > 0 ? value.CompletedMods * 100.0 / value.TotalMods : null;
            session.Progress.Report(new CliProgressEvent(
                value.Stage, percent,
                value.CurrentMod is { } current ? $"{current} ({value.CurrentModFiles}/{value.CurrentModTotalFiles})" : null,
                value.Message));
        }
    }

    private sealed class ImportProgressBridgeImpl(CliCommandSession session) : IProgress<ModImportProgress>
    {
        public void Report(ModImportProgress value)
            => session.Progress.Report(new CliProgressEvent(value.Step, value.Percentage, value.Detail, value.Detail));
    }
}
