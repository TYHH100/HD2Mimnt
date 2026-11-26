using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Cli;

internal static partial class CliHandlers
{
    /// <summary>hellforge 域：状态/档案/会话/模组/设置/日志/同步。</summary>
    internal static class HellForge
    {
        public static async Task<CliCommandResponse> StatusAsync(CliCommandContext context, CancellationToken ct)
        {
            var forge = context.Get<HellForgeService>();
            var status = await forge.GetStatusAsync(ct);
            var data = new
            {
                installed = forge.TryGetInstallationRoot(out _),
                status = status.ToString()
            };
            return CliCommandResponse.Ok($"HellForge：{status}", data);
        }

        public static async Task<CliCommandResponse> ProfilesAsync(CliCommandContext context, CancellationToken ct)
        {
            var profiles = await context.Get<HellForgeService>().GetProfilesAsync(ct);
            var message = profiles.ToString();
            return CliCommandResponse.Ok(string.IsNullOrEmpty(message) ? "没有档案。" : message, new { detail = message });
        }

        public static async Task<CliCommandResponse> CreateProfileAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count < 1 || positional.Count > 2)
                return CliCommandResponse.Usage("用法: hellforge profile create <名称> [模板]");
            var name = positional[0];
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9-]{1,32}$"))
                return CliCommandResponse.Fail("档案名只能包含小写字母、数字与连字符（1-32 位）。");
            var result = await context.Get<HellForgeService>()
                .CreateProfileAsync(name, positional.Count == 2 ? positional[1] : "default", ct);
            return CliCommandResponse.Ok($"档案 {name} 已创建。{result}", new { name });
        }

        public static async Task<CliCommandResponse> SelectProfileAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: hellforge profile select <名称>");
            await context.Get<HellForgeService>().SelectProfileAsync(positional[0], ct);
            return CliCommandResponse.Ok($"已切换到档案 {positional[0]}。", new { name = positional[0] });
        }

        public static async Task<CliCommandResponse> SessionAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            var session = context.Get<HellForgeSessionService>();
            if (positional.Count == 1 && positional[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                await session.StopAsync(ct);
                return CliCommandResponse.Ok("HellForge 会话已停止。");
            }
            if (positional.Count == 1 && positional[0].Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                await session.StartAsync();
                return CliCommandResponse.Ok("HellForge 会话已启动。");
            }
            await session.RefreshAsync(ct);
            return CliCommandResponse.Ok("HellForge 会话状态已刷新。");
        }

        public static async Task<CliCommandResponse> ModsAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            var forge = context.Get<HellForgeService>();
            if (positional.Count == 0) return CliCommandResponse.Usage(
                "用法: hellforge mods list | add <文件> | remove <id> | switch <id> on|off | order <id...>");
            var verb = positional[0].ToLowerInvariant();
            return verb switch
            {
                "list" => await ListModsAsync(forge, ct),
                "add" => await AddModAsync(forge, positional, ct),
                "remove" => await RemoveModAsync(forge, positional, ct),
                "switch" => await SwitchModAsync(forge, positional, ct),
                "order" => await OrderModsAsync(forge, positional, ct),
                _ => CliCommandResponse.Usage($"未知子命令 hellforge mods {verb}。")
            };
        }

        public static async Task<CliCommandResponse> SettingsAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            var forge = context.Get<HellForgeService>();
            if (positional.Count == 0 || positional[0].Equals("get", StringComparison.OrdinalIgnoreCase))
            {
                var current = await forge.GetSettingsAsync(ct);
                return CliCommandResponse.Ok(current?.ToString() ?? "无设置。", new { detail = current?.ToString() });
            }
            if (positional[0].Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                var changes = new Dictionary<string, string>();
                foreach (var token in args.UnknownFlags())
                {
                    var value = args.Option(token);
                    changes[token.TrimStart('-')] = value ?? "";
                }
                if (changes.Count == 0) return CliCommandResponse.Usage("用法: hellforge settings set --键 值 ...");
                var result = await forge.UpdateSettingsAsync(changes, ct);
                return CliCommandResponse.Ok($"设置已更新。{result}", new { changes });
            }
            return CliCommandResponse.Usage("用法: hellforge settings get | set --键 值 ...");
        }

        public static async Task<CliCommandResponse> LogsAsync(CliCommandContext context, CancellationToken ct)
        {
            var logs = await context.Get<HellForgeService>().GetLogsAsync(cancellationToken: ct);
            return CliCommandResponse.Ok(logs?.ToString() ?? "没有日志。", new { detail = logs?.ToString() });
        }

        public static async Task<CliCommandResponse> SyncAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var order = args.Option("--order");
            var remove = args.Flag("--remove");
            var positional = args.Positionals();
            if (positional.Count < 1) return CliCommandResponse.Usage("用法: hellforge sync <配置ID|名称> [--order top|bottom] [--remove]");
            var profiles = context.Get<ProfileService>();
            var profile = await CliResolve.FindProfileAsync(profiles, positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到配置“{positional[0]}”。");
            var sync = context.Get<HellForgeModSyncService>();
            var session = context.Session;
            var progress = new Progress<HellForgeSyncProgress>(value =>
            {
                double? percent = value.TotalMods > 0 ? value.CompletedMods * 100.0 / value.TotalMods : null;
                session.Progress.Report(new CliProgressEvent(value.Stage, percent, null, value.Message));
            });
            if (remove)
            {
                var removed = await sync.RemoveSyncedAsync(progress, ct);
                return CliCommandResponse.Ok($"已从 HellForge 移除 {removed.RemovedCount} 个同步模组。", new { removed = removed.RemovedCount });
            }
            var result = await sync.SyncProfileAsync(profile.Id, CliResolve.ParseOrder(order), progress, ct);
            return CliCommandResponse.Ok(
                $"配置 {profile.Name} 已同步到 HellForge：同步 {result.SyncedCount}、更新 {result.UpdatedCount}、未变 {result.UnchangedCount}。",
                new
                {
                    profileId = profile.Id,
                    status = result.Status.ToString(),
                    synced = result.SyncedCount,
                    updated = result.UpdatedCount,
                    unchanged = result.UnchangedCount,
                    warnings = result.Warnings
                });
        }

        private static async Task<CliCommandResponse> ListModsAsync(HellForgeService forge, CancellationToken ct)
        {
            var list = await forge.GetModsAsync(ct);
            return CliCommandResponse.Ok(list?.ToString() ?? "模组列表为空。", new { detail = list?.ToString() });
        }

        private static async Task<CliCommandResponse> AddModAsync(HellForgeService forge, IReadOnlyList<string> positional, CancellationToken ct)
        {
            if (positional.Count != 2 || !File.Exists(positional[1]))
                return CliCommandResponse.Fail("用法: hellforge mods add <模组文件路径>（文件需已存在）。");
            var result = await forge.AddModAsync(positional[1], ct);
            return CliCommandResponse.Ok($"已添加 {positional[1]}：{result}", new { file = positional[1] });
        }

        private static async Task<CliCommandResponse> RemoveModAsync(HellForgeService forge, IReadOnlyList<string> positional, CancellationToken ct)
        {
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: hellforge mods remove <id>");
            var result = await forge.RemoveModAsync(positional[1], ct);
            return CliCommandResponse.Ok($"已移除 {positional[1]}：{result}", new { id = positional[1] });
        }

        private static async Task<CliCommandResponse> SwitchModAsync(HellForgeService forge, IReadOnlyList<string> positional, CancellationToken ct)
        {
            if (positional.Count != 3 || (positional[2] != "on" && positional[2] != "off"))
                return CliCommandResponse.Usage("用法: hellforge mods switch <id> on|off");
            var result = await forge.SwitchModAsync(positional[1], positional[2] == "on", ct);
            return CliCommandResponse.Ok($"{positional[1]} 已 {(positional[2] == "on" ? "启用" : "禁用")}：{result}", new { id = positional[1] });
        }

        private static async Task<CliCommandResponse> OrderModsAsync(HellForgeService forge, IReadOnlyList<string> positional, CancellationToken ct)
        {
            if (positional.Count < 2) return CliCommandResponse.Usage("用法: hellforge mods order <id...>（按给定顺序）");
            var result = await forge.OrderModsAsync(positional.Skip(1).ToList(), ct);
            return CliCommandResponse.Ok($"模组顺序已更新：{result}", new { ids = positional.Skip(1).ToList() });
        }
    }

    /// <summary>hd2cl / mmd 域：角色库转换与 MMD 适配（Worker 进程编译）。</summary>
    internal static class Hd2Cl
    {
        public static async Task<CliCommandResponse> CandidatesAsync(CliCommandContext context, CancellationToken ct)
        {
            var candidates = await context.Get<IHd2ClConversionService>().GetCandidatesAsync(ct);
            var data = candidates.Select(candidate => new
            {
                modId = candidate.ModId,
                name = candidate.Name,
                companionComponents = candidate.CompanionComponents
            }).ToList();
            var message = candidates.Count == 0 ? "没有可转换的 IAS 模组。" : $"共 {candidates.Count} 个可转换模组。";
            return CliCommandResponse.Ok(message, data);
        }

        public static async Task<CliCommandResponse> VariantsAsync(CliCommandContext context, CancellationToken ct)
        {
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(),
                context.Request.Arguments.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "", ct)
                ?? throw new KeyNotFoundException("找不到模组。");
            var variants = await context.Get<IHd2ClConversionService>().GetVariantsAsync(mod.Id, ct);
            var data = variants.Select(variant => new
            {
                id = variant.Id,
                name = variant.Name,
                description = variant.Description,
                componentFolders = variant.ComponentFolders
            }).ToList();
            var message = variants.Count == 0
                ? $"{mod.Name} 没有可用变体。"
                : $"{mod.Name} 的变体：\n  " + string.Join("\n  ", variants.Select(variant =>
                    $"{variant.Name}（{string.Join(", ", variant.ComponentFolders ?? System.Array.Empty<string>())}）"));
            return CliCommandResponse.Ok(message, data);
        }

        public static async Task<CliCommandResponse> ConvertAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var variantName = args.Option("--variant");
            var rawFolders = args.Option("--folders");
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: hd2cl convert <ID|名称> [--variant 变体名] [--folders 文件夹1,文件夹2]");
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到模组“{positional[0]}”。");
            var conversion = context.Get<IHd2ClConversionService>();
            IReadOnlyList<string>? folders = null;
            if (rawFolders != null)
                folders = rawFolders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (variantName != null && folders == null)
            {
                var variants = await conversion.GetVariantsAsync(mod.Id, ct);
                var match = variants.FirstOrDefault(variant =>
                    string.Equals(variant.Name, variantName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    return CliCommandResponse.Fail(
                        $"变体“{variantName}”不存在。可用变体：\n  " +
                        string.Join("\n  ", variants.Select(variant => $"{variant.Name}（{string.Join(", ", variant.ComponentFolders ?? System.Array.Empty<string>())}）")));
                folders = match.ComponentFolders;
            }
            if (folders == null)
                return CliCommandResponse.Fail("必须用 --variant 变体名 或 --folders 文件夹列表 指定转换目标（CLI 无弹窗选择）。");
            var session = context.Session;
            var result = await conversion.ConvertAsync(mod.Id, folders, variantName, ToStringProgress(session, "HD2CL 转换"), ct);
            return CliCommandResponse.Ok(
                $"转换完成：{result.Name}（源模组{(result.SourceDisabled ? "已禁用" : "未禁用")}）。",
                new { modId = result.ModId, name = result.Name, profileId = result.ProfileId, sourceDisabled = result.SourceDisabled });
        }

        public static async Task<CliCommandResponse> ExportAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: hd2cl export <ID|名称> <输出目录>");
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到模组“{positional[0]}”。");
            var result = await context.Get<IHd2ClConversionService>()
                .ExportAsync(mod.Id, positional[1], progress: ToStringProgress(context.Session, "HD2CL 导出"), token: ct);
            return CliCommandResponse.Ok($"已导出 {result.Name} → {result.ArchivePath}", new { archivePath = result.ArchivePath });
        }

        private static async Task<HD2Mimnt.Core.Entities.Mod> FindModAsync(CliCommandContext context, CancellationToken ct)
        {
            var positional = context.Request.Arguments.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
            var reference = positional.Count > 0 ? positional[0] : "";
            return await CliResolve.FindModAsync(context.Get<ModService>(), reference, ct)
                ?? throw new KeyNotFoundException($"找不到模组“{reference}”。");
        }
    }

    /// <summary>mmd 域：MMD 资源包检视、适配与导出。</summary>
    internal static class Mmd
    {
        public static async Task<CliCommandResponse> InspectAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var sourceOption = args.Option("--source");
            var positional = args.Positionals();
            if (positional.Count < 1) return CliCommandResponse.Usage("用法: mmd inspect <资源包路径> [--source 选项]");
            var inspection = await context.Get<IMmdAdaptationService>()
                .InspectAsync(positional[0], sourceOption, null, null, ToStringProgress(context.Session, "MMD 检视"), ct);
            return CliCommandResponse.Ok("检视完成（结果见 --json 的 data）。", inspection);
        }

        public static async Task<CliCommandResponse> ConvertAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var sourceOption = args.Option("--source");
            var positional = args.Positionals();
            if (positional.Count < 1) return CliCommandResponse.Usage("用法: mmd convert <资源包路径> [--source 选项]");
            var result = await context.Get<IMmdAdaptationService>()
                .ConvertAsync(positional[0], sourceOption, null, null,
                    ToStringProgress(context.Session, "MMD 适配"), ct);
            return CliCommandResponse.Ok("适配完成（结果见 --json 的 data）。", result);
        }

        public static async Task<CliCommandResponse> ExportAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var sourceOption = args.Option("--source");
            var positional = args.Positionals();
            if (positional.Count < 2) return CliCommandResponse.Usage("用法: mmd export <资源包路径> <输出目录> [--source 选项]");
            var result = await context.Get<IMmdAdaptationService>()
                .ExportAsync(positional[0], positional[1], sourceOption, null, null,
                    ToStringProgress(context.Session, "MMD 导出"), ct);
            return CliCommandResponse.Ok("导出完成（结果见 --json 的 data）。", result);
        }
    }

    /// <summary>ias 扩展域：HD2IAS 包管理（安装/卸载/生成）与 IAS 系统运行时切换。</summary>
    internal static class IasExt
    {
        public static async Task<CliCommandResponse> ManagementStateAsync(CliCommandContext context, CancellationToken ct)
        {
            var gamePath = context.Get<SettingsService>().GetEffectiveGamePath();
            var state = await context.Get<Hd2IASIntegrationService>().GetManagementStateAsync(gamePath, ct);
            return CliCommandResponse.Ok(
                $"HD2IAS：已托管安装={state.HasManagedInstallation}，数据模组={state.HasDataMod}，数据文件={state.HasDataFiles}，缺失文件={state.MissingFiles.Count}。",
                state);
        }

        public static async Task<CliCommandResponse> InstallAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var route = ParseRoute(args.Option("--route"));
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: ias install <HD2IAS 包路径> [--route lua|native]");
            var gamePath = context.Get<SettingsService>().GetEffectiveGamePath();
            var package = await context.Get<Hd2IASIntegrationService>()
                .InstallAsync(positional[0], gamePath, route, ToStringProgress(context.Session, "HD2IAS 安装"), ct);
            return CliCommandResponse.Ok(
                $"HD2IAS 已安装：{package.Version}（{package.Module}，{package.Route}）→ {package.Path}",
                new { path = package.Path, version = package.Version, route = package.Route.ToString(), sourceSha256 = package.SourceSha256 });
        }

        public static async Task<CliCommandResponse> UninstallAsync(CliCommandContext context, CancellationToken ct)
        {
            var gamePath = context.Get<SettingsService>().GetEffectiveGamePath();
            var result = await context.Get<Hd2IASIntegrationService>()
                .UninstallAsync(gamePath, ToStringProgress(context.Session, "HD2IAS 卸载"), ct);
            return CliCommandResponse.Ok(
                $"HD2IAS 已卸载：移除 {result.RemovedFiles.Count} 个文件，保留 {result.PreservedFiles.Count}，修改 {result.ModifiedFiles.Count}。",
                new { removed = result.RemovedFiles.Count, preserved = result.PreservedFiles.Count, modified = result.ModifiedFiles.Count });
        }

        public static async Task<CliCommandResponse> GenerateAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var route = ParseRoute(args.Option("--route"));
            var positional = args.Positionals();
            if (positional.Count != 2)
                return CliCommandResponse.Usage("用法: ias generate <HD2IAS 包路径> <输出路径> [--route lua|native]");
            var gamePath = context.Get<SettingsService>().GetEffectiveGamePath();
            var package = await context.Get<Hd2IASIntegrationService>()
                .GenerateAsync(positional[0], gamePath, route, positional[1], ct);
            return CliCommandResponse.Ok(
                $"生成完成：{package.Version}（{package.Route}）→ {package.Path}", new { path = package.Path, version = package.Version });
        }

        public static async Task<CliCommandResponse> CompanionRegenerateAsync(CliCommandContext context, CancellationToken ct)
        {
            var gamePath = context.Get<SettingsService>().GetEffectiveGamePath();
            var result = await context.Get<IIasCompanionService>()
                .RegenerateAsync(gamePath, ToStringProgress(context.Session, "armor 配套重建"), ct);
            return CliCommandResponse.Ok($"armor 配套已重建（详见 --json 的 data）。", result);
        }

        public static async Task<CliCommandResponse> RuntimeListAsync(CliCommandContext context, CancellationToken ct)
        {
            var system = context.Get<IIasSystemService>();
            var location = await system.GetInstallLocationAsync(ct);
            if (string.IsNullOrWhiteSpace(location))
                return CliCommandResponse.Fail(
                    "IAS 安装位置未设置。先用 GUI 设置页或 settings set IAS.InstallLocation <目录> 配置后重试。");
            var runtimes = await system.DiscoverRuntimeIdsAsync(location, ct);
            var active = await system.GetActiveRuntimeIdAsync(ct);
            var data = runtimes.Select(id => new { runtimeId = id, isActive = id == active }).ToList();
            return CliCommandResponse.Ok(
                $"IAS 位置 {location}：{runtimes.Count} 个运行时（当前 {active ?? "(未设置)"}）。\n  " +
                string.Join("\n  ", runtimes), data);
        }

        public static async Task<CliCommandResponse> RuntimeSetAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: ias runtime set <运行时ID>（ias runtime list 获取）");
            await context.Get<IIasSystemService>().SaveActiveRuntimeIdAsync(positional[0], ct);
            return CliCommandResponse.Ok($"活动运行时已切换为 {positional[0]}。", new { runtimeId = positional[0] });
        }

        private static Hd2IASRoute ParseRoute(string? value) => value?.ToLowerInvariant() switch
        {
            null or "" or "lua" => Hd2IASRoute.Lua,
            "native" => Hd2IASRoute.Native,
            _ => throw new ArgumentException("route 可选 lua|native。")
        };
    }

    /// <summary>plugin 域：插件安装/启停/卸载、权限、命令执行与历史。</summary>
    internal static class Plugins
    {
        public static async Task<CliCommandResponse> ListAsync(CliCommandContext context, CancellationToken ct)
        {
            var plugins = await context.Get<IPluginService>().GetAllPluginsAsync(ct);
            var data = plugins.Select(plugin => new
            {
                id = plugin.Id,
                name = plugin.Name,
                version = plugin.Version,
                enabled = plugin.Enabled,
                permissionMode = plugin.PermissionMode
            }).ToList();
            var message = plugins.Count == 0 ? "没有已安装的插件。" : $"共 {plugins.Count} 个插件：";
            return CliCommandResponse.Ok(message + (plugins.Count > 0
                ? "\n  " + string.Join("\n  ", plugins.Select(plugin =>
                    $"{(plugin.Enabled ? "●" : "○")} {plugin.Name} {plugin.Version} [{plugin.Id}]"))
                : ""), data);
        }

        public static async Task<CliCommandResponse> InstallAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1 || !File.Exists(positional[0]))
                return CliCommandResponse.Fail("用法: plugin install <插件包路径|manifest 路径>（文件需存在）。");
            var result = await context.Get<IPluginService>().InstallPluginAsync(positional[0], ct);
            return ToOperationResponse("安装", result);
        }

        public static async Task<CliCommandResponse> EnableAsync(CliCommandContext context, CancellationToken ct)
        {
            var pluginId = RequirePluginId(context);
            var result = await context.Get<IPluginService>().EnablePluginAsync(pluginId, ct);
            return ToOperationResponse("启用", result);
        }

        public static async Task<CliCommandResponse> DisableAsync(CliCommandContext context, CancellationToken ct)
        {
            var pluginId = RequirePluginId(context);
            var result = await context.Get<IPluginService>().DisablePluginAsync(pluginId, ct);
            return ToOperationResponse("停用", result);
        }

        public static async Task<CliCommandResponse> UninstallAsync(CliCommandContext context, CancellationToken ct)
        {
            var pluginId = RequirePluginId(context);
            var result = await context.Get<IPluginService>().UninstallPluginAsync(pluginId, ct);
            return ToOperationResponse("卸载", result);
        }

        public static async Task<CliCommandResponse> PermissionsAsync(CliCommandContext context, CancellationToken ct)
        {
            var pluginId = RequirePluginId(context);
            var permissions = await context.Get<IPluginService>().GetPluginPermissionsAsync(pluginId, ct);
            var data = permissions.Select(permission => new
            {
                permission = permission.Permission,
                granted = permission.Granted
            }).ToList();
            return CliCommandResponse.Ok(permissions.Count == 0
                ? $"{pluginId} 还没有权限记录。"
                : $"{pluginId} 的权限：\n  " + string.Join("\n  ", permissions.Select(permission =>
                    $"{(permission.Granted ? "✓" : "✗")} {permission.Permission}")), data);
        }

        public static async Task<CliCommandResponse> GrantAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: plugin grant <插件ID> <权限名>");
            var result = await context.Get<IPluginService>().GrantPermissionAsync(positional[0], positional[1], ct);
            return ToOperationResponse("授权", result);
        }

        public static async Task<CliCommandResponse> RevokeAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: plugin revoke <插件ID> <权限名>");
            var result = await context.Get<IPluginService>().RevokePermissionAsync(positional[0], positional[1], ct);
            return ToOperationResponse("撤销授权", result);
        }

        public static async Task<CliCommandResponse> CommandsAsync(CliCommandContext context, CancellationToken ct)
        {
            var pluginId = RequirePluginId(context);
            var commands = await context.Get<IPluginService>().GetPluginCommandsAsync(pluginId, ct);
            var data = commands.Select(command => new
            {
                command = command.Name,
                description = command.Description
            }).ToList();
            return CliCommandResponse.Ok(commands.Count == 0
                ? $"{pluginId} 没有贡献命令。"
                : $"{pluginId} 的命令：\n  " + string.Join("\n  ", commands.Select(command =>
                    $"{command.Name} — {command.Description}")), data);
        }

        public static async Task<CliCommandResponse> ExecAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var argumentsJson = args.Option("--args");
            var positional = args.Positionals();
            if (positional.Count < 2)
                return CliCommandResponse.Usage("用法: plugin exec <插件ID> <命令名> [--args JSON]");
            if (argumentsJson != null)
            {
                try { using var _ = JsonDocument.Parse(argumentsJson); }
                catch (JsonException ex) { return CliCommandResponse.Fail($"--args 不是合法 JSON：{ex.Message}"); }
            }
            var result = await context.Get<IPluginService>()
                .ExecutePluginCommandAsync(positional[0], positional[1], argumentsJson, ct);
            return ToOperationResponse("执行", result);
        }

        public static async Task<CliCommandResponse> HistoryAsync(CliCommandContext context, CancellationToken ct)
        {
            var pluginId = RequirePluginId(context);
            var history = await context.Get<IPluginService>().GetExecutionHistoryAsync(pluginId, cancellationToken: ct);
            var data = history.Select(entry => new
            {
                command = entry.Command,
                startTime = entry.StartTime,
                endTime = entry.EndTime,
                status = entry.Status,
                result = entry.Result,
                error = entry.Error
            }).ToList();
            return CliCommandResponse.Ok(history.Count == 0
                ? $"{pluginId} 没有执行历史。"
                : $"最近 {history.Count} 条执行记录。", data);
        }

        private static string RequirePluginId(CliCommandContext context)
        {
            var positional = context.Request.Arguments.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (positional.Count == 0) throw new ArgumentException("缺少插件 ID。");
            return positional[0];
        }

        private static CliCommandResponse ToOperationResponse(string verb, PluginOperationResult result)
        {
            var data = new { success = result.Success, message = result.Message };
            return result.Success
                ? CliCommandResponse.Ok($"{verb}成功：{result.Message}", data)
                : CliCommandResponse.Fail($"{verb}失败：{result.Message}", data);
        }
    }

    /// <summary>字符串进度（Hd2CL/MMD/IAS 的 IProgress&lt;string&gt;）桥到 CLI 进度会话。</summary>
    private static IProgress<string> ToStringProgress(CliCommandSession session, string stage)
        => new StringProgressBridge(session, stage);

    private sealed class StringProgressBridge(CliCommandSession session, string stage) : IProgress<string>
    {
        public void Report(string value)
            => session.Progress.Report(new CliProgressEvent(stage, null, value, value));
    }
}
