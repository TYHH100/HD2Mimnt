using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Cli;

/// <summary>CLI 命令 handler 集合；只编排现有 Application 服务，不新增业务逻辑。</summary>
internal static class CliHandlers
{
    internal static class Status
    {
        public static async Task<CliCommandResponse> StatusAsync(CliCommandContext context, CancellationToken ct)
        {
            var mods = context.Get<ModService>();
            var profiles = context.Get<ProfileService>();
            var deployments = context.Get<DeploymentService>();
            var settings = context.Get<SettingsService>();
            var storage = context.GetOrDefault<StorageSettingsService>();

            var allMods = (await mods.GetAllModsAsync(ct)).ToList();
            var active = await profiles.GetActiveProfileAsync(ct);
            var records = (await deployments.GetLatestDeploymentsAsync(ct)).ToList();
            var statistics = await mods.GetStatisticsAsync(ct);

            var data = new
            {
                version = typeof(CliHandlers).Assembly.GetName().Version?.ToString(3),
                gamePath = settings.GetEffectiveGamePath(),
                activeProfile = active == null ? null : new { active.Id, active.Name, memberCount = active.Mods.Count, enabledCount = active.Mods.Count(item => item.Enabled) },
                modCounts = new
                {
                    total = statistics.TotalCount,
                    scriptMods = statistics.ScriptModCount,
                    conflict = statistics.ConflictCount,
                    totalSizeBytes = statistics.TotalSize
                },
                deployment = new
                {
                    recordCount = records.Count,
                    sessions = records.GroupBy(record => record.SessionId).Select(group => new
                    {
                        sessionId = group.Key,
                        status = group.First().Status,
                        method = group.First().DeploymentMethod,
                        modCount = group.Count(),
                        startedAt = group.Min(record => record.StartTime)
                    }).ToList()
                },
                storagePaths = storage == null ? null : new
                {
                    appData = storage.Current.AppDataPath,
                    database = storage.Current.DatabasePath,
                    mods = storage.Current.ModsPath
                }
            };

            var rows = new List<(string, string)>
            {
                ("版本", data.version ?? "未知"),
                ("游戏目录", data.gamePath ?? "(未设置)"),
                ("活动配置", active == null ? "(无)" : $"{active.Name}  成员 {active.Mods.Count} / 启用 {active.Mods.Count(item => item.Enabled)}"),
                ("模组统计", $"共 {allMods.Count} 个  含脚本 {statistics.ScriptModCount}  冲突 {statistics.ConflictCount}"),
                ("部署记录", $"{records.Count} 条（{data.deployment.sessions.Count} 个会话）")
            };
            return CliCommandResponse.Ok(CliResolve.Table(rows.ToArray()), data);
        }
    }

    internal static class Mods
    {
        public static async Task<CliCommandResponse> ListAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var search = args.Option("--search");
            if (args.UnknownFlags() is { Count: > 0 } unknown)
                return CliCommandResponse.Usage("未知选项: " + string.Join(' ', unknown));

            var mods = context.Get<ModService>();
            var active = await context.Get<ProfileService>().GetActiveProfileAsync(ct);
            var source = search == null
                ? await mods.GetAllModsAsync(ct)
                : await mods.SearchModsAsync(search, ct);
            var ordered = source.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (int.TryParse(args.Option("--limit"), out var limit) && limit > 0) ordered = ordered.Take(limit).ToList();

            var data = ordered.Select(mod => new
            {
                id = mod.Id,
                name = mod.Name,
                version = mod.Version,
                category = mod.Category.ToString(),
                status = mod.Status.ToString(),
                enabled = active?.Mods.FirstOrDefault(item => item.ModId == mod.Id) is { Enabled: true },
                inActiveProfile = active?.Mods.Any(item => item.ModId == mod.Id) == true,
                sizeBytes = mod.Size,
                tags = mod.Tags
            }).ToList();
            return CliCommandResponse.Ok($"共 {data.Count} 个模组。", data);
        }

        public static async Task<CliCommandResponse> ShowAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: mod show <ID|名称>");
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到模组“{positional[0]}”。");
            var active = await context.Get<ProfileService>().GetActiveProfileAsync(ct);
            var member = active?.Mods.FirstOrDefault(item => item.ModId == mod.Id);

            var data = new
            {
                id = mod.Id,
                name = mod.Name,
                version = mod.Version,
                author = mod.Author,
                description = mod.Description,
                category = mod.Category.ToString(),
                status = mod.Status.ToString(),
                source = mod.Source.ToString(),
                sizeBytes = mod.Size,
                hash = mod.Hash,
                path = mod.Path,
                tags = mod.Tags,
                containsScripts = mod.ContainsScripts,
                importedAt = mod.ImportedAt,
                inActiveProfile = member != null,
                enabled = member is { Enabled: true }
            };
            return CliCommandResponse.Ok(
                $"{mod.Name} v{mod.Version}\n" + CliResolve.Table(
                    ("ID", mod.Id.ToString()), ("分类", mod.Category.ToString()), ("状态", mod.Status.ToString()),
                    ("大小", $"{mod.Size / 1024.0 / 1024.0:F1} MB"), ("脚本", mod.ContainsScripts ? "是" : "否"),
                    ("活动配置", member == null ? "未加入" : member.Enabled ? "已启用" : "已禁用")), data);
        }

        public static async Task<CliCommandResponse> ImportAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var paths = args.Positionals();
            if (paths.Count == 0) return CliCommandResponse.Usage("用法: mod import <路径...>");
            if (args.UnknownFlags() is { Count: > 0 } unknown)
                return CliCommandResponse.Usage("未知选项: " + string.Join(' ', unknown));
            var missing = paths.Where(path => !File.Exists(path) && !Directory.Exists(path)).ToList();
            if (missing.Count > 0) return CliCommandResponse.Fail("路径不存在: " + string.Join(", ", missing));

            var mods = context.Get<ModService>();
            var results = new List<object>();
            var lines = new List<string>();
            var succeeded = 0;
            foreach (var path in paths)
            {
                try
                {
                    var modId = await mods.ImportModAsync(path, ct);
                    results.Add(new { path, ok = true, modId });
                    lines.Add($"  ✓ {path} → {modId}");
                    succeeded++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results.Add(new { path, ok = false, error = ex.Message });
                    lines.Add($"  ✗ {path}: {ex.Message}");
                }
            }
            var failed = paths.Count - succeeded;
            var message = $"导入完成：成功 {succeeded}，失败 {failed}。\n" + string.Join("\n", lines);
            var response = failed == 0 ? CliCommandResponse.Ok(message, results) : CliCommandResponse.Fail(message, results);
            return response;
        }

        public static async Task<CliCommandResponse> DeleteAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: mod delete <ID|名称>");
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到模组“{positional[0]}”。");
            var deleted = await context.Get<ModService>().DeleteModAsync(mod.Id, ct);
            return deleted
                ? CliCommandResponse.Ok($"已删除模组 {mod.Name}（{mod.Id}）。", new { modId = mod.Id, name = mod.Name })
                : CliCommandResponse.Fail($"删除模组 {mod.Name} 失败，详情见日志。");
        }

        public static CliCommandHandler SetEnabledAsync(bool enabled)
            => async (context, ct) =>
            {
                var args = new CliArgs(context.Request.Arguments);
                var positional = args.Positionals();
                if (positional.Count != 1) return CliCommandResponse.Usage($"用法: mod {(enabled ? "enable" : "disable")} <ID|名称>");
                var mod = await CliResolve.FindModAsync(context.Get<ModService>(), positional[0], ct)
                    ?? throw new KeyNotFoundException($"找不到模组“{positional[0]}”。");
                var profile = await context.Get<ProfileService>().GetActiveProfileAsync(ct)
                    ?? throw new InvalidOperationException("没有活动配置，请先用 profile activate 切换。");
                var profiles = context.Get<ProfileService>();
                if (!profile.Mods.Any(item => item.ModId == mod.Id))
                    await profiles.AddModToProfileAsync(profile.Id, mod.Id, ct);
                await profiles.SetModEnabledAsync(profile.Id, mod.Id, enabled, ct);
                return CliCommandResponse.Ok(
                    $"模组 {mod.Name} 已在配置 {profile.Name} 中{(enabled ? "启用" : "禁用")}。",
                    new { profileId = profile.Id, modId = mod.Id, enabled });
            };

        public static async Task<CliCommandResponse> RenameAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: mod rename <ID|名称> <新名称>");
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到模组“{positional[0]}”。");
            var updated = await context.Get<ModService>().UpdateModMetadataAsync(mod.Id, name: positional[1], cancellationToken: ct);
            return CliCommandResponse.Ok($"模组已重命名为 {updated.Name}。", new { modId = updated.Id, name = updated.Name });
        }
    }

    internal static class Profiles
    {
        public static async Task<CliCommandResponse> ListAsync(CliCommandContext context, CancellationToken ct)
        {
            var profiles = (await context.Get<ProfileService>().GetAllProfilesAsync(ct))
                .OrderBy(profile => profile.SortOrder).ToList();
            var data = profiles.Select(profile => new
            {
                id = profile.Id,
                name = profile.Name,
                isActive = profile.IsActive,
                isDefault = profile.IsDefault,
                modCount = profile.Mods.Count,
                enabledCount = profile.Mods.Count(item => item.Enabled)
            }).ToList();
            var message = string.Join("\n", profiles.Select(profile =>
                $"{(profile.IsActive ? "▸" : " ")} {profile.Name}  成员 {profile.Mods.Count} / 启用 {profile.Mods.Count(item => item.Enabled)}{(profile.IsDefault ? "  [默认]" : "")}  {profile.Id}"));
            return CliCommandResponse.Ok(string.IsNullOrEmpty(message) ? "还没有任何配置。" : message, data);
        }

        public static async Task<CliCommandResponse> ShowAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: profile show <ID|名称>");
            var profile = await CliResolve.FindProfileAsync(context.Get<ProfileService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到配置“{positional[0]}”。");
            var mods = await context.Get<ModService>().GetModsByIdsAsync(profile.Mods.Select(item => item.ModId), ct);
            var names = mods.ToDictionary(mod => mod.Id, mod => mod.Name);
            var data = new
            {
                id = profile.Id,
                name = profile.Name,
                description = profile.Description,
                isActive = profile.IsActive,
                isDefault = profile.IsDefault,
                members = profile.Mods.OrderBy(item => item.Order).Select(item => new
                {
                    modId = item.ModId,
                    name = names.GetValueOrDefault(item.ModId, "(模组已删除)"),
                    enabled = item.Enabled,
                    order = item.Order
                })
            };
            var message = $"{profile.Name}{(profile.IsActive ? "（活动）" : "")} 成员 {profile.Mods.Count} 个：\n" +
                string.Join("\n", profile.Mods.OrderBy(item => item.Order).Select(item =>
                    $"  {(item.Enabled ? "✓" : "○")} {names.GetValueOrDefault(item.ModId, "(模组已删除)")}  {item.ModId}"));
            return CliCommandResponse.Ok(message, data);
        }

        public static async Task<CliCommandResponse> CreateAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: profile create <名称>");
            var id = await context.Get<ProfileService>().CreateProfileAsync(positional[0],
                args.Option("--description") ?? "", ct);
            return CliCommandResponse.Ok($"已创建配置 {positional[0]}（{id}）。", new { id, name = positional[0] });
        }

        public static async Task<CliCommandResponse> ActivateAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: profile activate <ID|名称>");
            var profile = await CliResolve.FindProfileAsync(context.Get<ProfileService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到配置“{positional[0]}”。");
            await context.Get<ProfileService>().SetActiveProfileAsync(profile.Id, ct);
            return CliCommandResponse.Ok($"活动配置已切换为 {profile.Name}。注意：需要执行 deploy 才会改变游戏目录。",
                new { profileId = profile.Id, name = profile.Name });
        }

        public static async Task<CliCommandResponse> AddModAsync(CliCommandContext context, CancellationToken ct)
        {
            var positional = new CliArgs(context.Request.Arguments).Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: profile add-mod <配置> <模组>");
            var profile = await CliResolve.FindProfileAsync(context.Get<ProfileService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到配置“{positional[0]}”。");
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(), positional[1], ct)
                ?? throw new KeyNotFoundException($"找不到模组“{positional[1]}”。");
            await context.Get<ProfileService>().AddModToProfileAsync(profile.Id, mod.Id, ct);
            return CliCommandResponse.Ok($"已把 {mod.Name} 加入配置 {profile.Name}（默认启用）。",
                new { profileId = profile.Id, modId = mod.Id });
        }

        public static async Task<CliCommandResponse> RemoveModAsync(CliCommandContext context, CancellationToken ct)
        {
            var positional = new CliArgs(context.Request.Arguments).Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: profile remove-mod <配置> <模组>");
            var profile = await CliResolve.FindProfileAsync(context.Get<ProfileService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到配置“{positional[0]}”。");
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(), positional[1], ct)
                ?? throw new KeyNotFoundException($"找不到模组“{positional[1]}”。");
            await context.Get<ProfileService>().RemoveModFromProfileAsync(profile.Id, mod.Id, ct);
            return CliCommandResponse.Ok($"已把 {mod.Name} 移出配置 {profile.Name}。",
                new { profileId = profile.Id, modId = mod.Id });
        }
    }

    internal static class Deploy
    {
        public static async Task<CliCommandResponse> DeployAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var profileRef = args.Option("--profile");
            var methodValue = args.Option("--method");
            var orderValue = args.Option("--order");
            if (args.UnknownFlags() is { Count: > 0 } unknown)
                return CliCommandResponse.Usage("未知选项: " + string.Join(' ', unknown));

            var deployments = context.Get<DeploymentService>();
            var settings = context.Get<SettingsService>();
            Profile? profile;
            var profiles = context.Get<ProfileService>();
            if (profileRef == null)
            {
                profile = await profiles.GetActiveProfileAsync(ct)
                    ?? throw new InvalidOperationException("没有活动配置，请先用 profile activate 切换。");
            }
            else
            {
                profile = await CliResolve.FindProfileAsync(profiles, profileRef, ct)
                    ?? throw new KeyNotFoundException($"找不到配置“{profileRef}”。");
            }

            var method = CliResolve.ParseMethod(methodValue);
            if (!deployments.GetSupportedDeploymentMethods().Contains(method))
                return CliCommandResponse.Fail($"当前环境不支持部署方式 {method}，可用: {string.Join(", ", deployments.GetSupportedDeploymentMethods())}。");
            var order = CliResolve.ParseOrder(orderValue);
            var gamePath = settings.GetEffectiveGamePath();
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                return CliCommandResponse.Fail($"游戏目录无效：{gamePath ?? "(未设置)"}。请先用 settings set Game.Path <目录> 配置。");

            var result = await deployments.DeployProfileAsync(profile.Id, method, order, gamePath,
                new Progress<DeploymentProgress> { }, ct);
            var data = new
            {
                sessionId = result.SessionId,
                profileId = result.ProfileId,
                status = result.Status.ToString(),
                method = result.Method.ToString(),
                totalMods = result.TotalMods,
                completedMods = result.CompletedMods,
                failedMods = result.FailedMods,
                filesDeleted = result.FilesDeleted,
                addedFiles = result.AddedFiles,
                updatedFiles = result.UpdatedFiles,
                unchangedFiles = result.UnchangedFiles,
                message = result.Message,
                errorMessage = result.ErrorMessage
            };
            var summary = $"部署{result.Status}：模组 {result.CompletedMods}/{result.TotalMods}，失败 {result.FailedMods}。" +
                $"\n新增 {result.AddedFiles}，更新 {result.UpdatedFiles}，未变 {result.UnchangedFiles}，删除 {result.RemovedFiles} 个文件。";
            if (result.ErrorMessage != null) summary += "\n错误: " + result.ErrorMessage;
            return result.Status == HD2Mimnt.Core.Enums.DeploymentStatus.Completed
                ? CliCommandResponse.Ok(summary, data)
                : CliCommandResponse.Fail(summary, data);
        }

        public static async Task<CliCommandResponse> StatusAsync(CliCommandContext context, CancellationToken ct)
        {
            var deployments = context.Get<DeploymentService>();
            var records = (await deployments.GetLatestDeploymentsAsync(ct)).ToList();
            var settings = context.Get<SettingsService>();
            var profiles = context.Get<ProfileService>();
            var profileNames = (await profiles.GetAllProfilesAsync(ct)).ToDictionary(profile => profile.Id, profile => profile.Name);
            var data = new
            {
                gamePath = settings.GetEffectiveGamePath(),
                records = records.Select(record => new
                {
                    sessionId = record.SessionId,
                    profileId = record.ProfileId,
                    profileName = profileNames.GetValueOrDefault(record.ProfileId),
                    modId = record.ModId,
                    method = record.DeploymentMethod,
                    status = record.Status,
                    fileCount = record.Files.Count,
                    appliedAt = record.AppliedAt
                })
            };
            var message = records.Count == 0
                ? "当前游戏安装没有部署记录。"
                : $"共 {records.Count} 条部署记录（{data.records.GroupBy(record => record.sessionId).Count()} 个会话）。";
            return CliCommandResponse.Ok(message, data);
        }

        public static async Task<CliCommandResponse> UndeployAsync(CliCommandContext context, CancellationToken ct)
        {
            var (filesDeleted, recordsDeleted) = await context.Get<DeploymentService>().PreDeploymentCleanupAsync(cancellationToken: ct);
            return CliCommandResponse.Ok($"已清除 {filesDeleted} 个已部署文件、{recordsDeleted} 条记录。",
                new { filesDeleted, recordsDeleted });
        }
    }

    internal static class Compat
    {
        public static async Task<CliCommandResponse> CheckAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: compat check <ID|名称|all> [--reference 版本]");
            uint? reference = null;
            if (args.Option("--reference") is { } raw)
            {
                if (!uint.TryParse(raw, out var parsed))
                    return CliCommandResponse.Usage($"--reference 需要数字版本号，收到“{raw}”。");
                reference = parsed;
            }

            var mods = context.Get<ModService>();
            List<Guid> ids;
            if (positional[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                ids = (await mods.GetAllModsAsync(ct)).Select(mod => mod.Id).ToList();
            else
            {
                var mod = await CliResolve.FindModAsync(mods, positional[0], ct)
                    ?? throw new KeyNotFoundException($"找不到模组“{positional[0]}”。");
                ids = [mod.Id];
            }
            if (ids.Count == 0) return CliCommandResponse.Ok("模组库为空，没有可检查的模组。", Array.Empty<object>());

            var service = context.Get<ModCompatibilityService>();
            var reports = await service.CheckModsAsync(ids, reference, ct);
            var byId = (await mods.GetModsByIdsAsync(ids, ct)).ToDictionary(mod => mod.Id, mod => mod.Name);
            var data = reports.Select(report => new
            {
                modId = report.ModId,
                modName = byId.GetValueOrDefault(report.ModId ?? Guid.Empty, report.ModPath),
                status = report.Status.ToString(),
                reason = report.Reason.ToString(),
                unitCount = report.UnitCount,
                issues = report.Issues,
                hasBlockingIssues = report.HasBlockingStructureIssues
            }).ToList();
            var summary = data.GroupBy(item => item.status).Select(group => $"{group.Key} {group.Count()}");
            return CliCommandResponse.Ok($"兼容性检查完成：{string.Join("，", summary)}。", data);
        }

        public static async Task<CliCommandResponse> RepairAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: compat repair <ID|名称>");
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到模组“{positional[0]}”。");
            var result = await context.Get<ModCompatibilityService>().RepairAsync(mod.Id, ct);
            var data = new
            {
                modId = mod.Id,
                success = result.Success,
                changed = result.Changed,
                appliedActionCount = result.AppliedActionCount,
                backups = result.BackupPaths,
                errorMessage = result.ErrorMessage
            };
            return result.Success
                ? CliCommandResponse.Ok($"修复完成：{mod.Name}，应用 {result.AppliedActionCount} 项动作{(result.Changed ? "，已产生更改" : "，无需更改")}。", data)
                : CliCommandResponse.Fail($"修复失败：{result.ErrorMessage ?? "未知错误"}", data);
        }
    }

    internal static class Settings
    {
        /// <summary>CLI 可访问的设置白名单：键 → 值类型说明。不在白名单的键一律拒绝。</summary>
        internal static readonly IReadOnlyDictionary<string, string> Whitelist = new Dictionary<string, string>
        {
            ["Game.Path"] = "游戏安装目录（字符串，必须存在）",
            ["Game.AutoDetect"] = "自动检测游戏（true/false）",
            ["Game.Mode"] = "游戏版本（Official/HellForge）",
            ["Game.HellForgePath"] = "HellForge 安装目录（字符串）",
            ["IAS.InstallLocation"] = "IAS 安装位置（字符串）"
        };

        public static async Task<CliCommandResponse> ListAsync(CliCommandContext context, CancellationToken ct)
        {
            var settings = context.Get<SettingsService>();
            var values = Whitelist.Keys.Select(key => (key, ReadValue(settings, key))).ToList();
            var data = values.Select(item => new { key = item.key, value = item.Item2 }).ToList();
            var message = string.Join("\n", values.Select(item => $"  {item.key} = {item.Item2 ?? "(未设置)"}"));
            return CliCommandResponse.Ok("白名单内设置当前值：\n" + message, data);
        }

        public static Task<CliCommandResponse> GetAsync(CliCommandContext context, CancellationToken ct)
        {
            var positional = new CliArgs(context.Request.Arguments).Positionals();
            if (positional.Count != 1) return Task.FromResult(CliCommandResponse.Usage("用法: settings get <键>"));
            if (!Whitelist.ContainsKey(positional[0]))
                return Task.FromResult(CliCommandResponse.Fail($"“{positional[0]}”不在 CLI 白名单内。可用键：\n{WhitelistText()}"));
            var value = ReadValue(context.Get<SettingsService>(), positional[0]);
            return Task.FromResult(CliCommandResponse.Ok($"{positional[0]} = {value ?? "(未设置)"}", new { key = positional[0], value }));
        }

        public static async Task<CliCommandResponse> SetAsync(CliCommandContext context, CancellationToken ct)
        {
            var positional = new CliArgs(context.Request.Arguments).Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: settings set <键> <值>");
            var (key, value) = (positional[0], positional[1]);
            if (!Whitelist.ContainsKey(key))
                return CliCommandResponse.Fail($"“{key}”不在 CLI 白名单内。可用键：\n{WhitelistText()}");
            var settings = context.Get<SettingsService>();
            switch (key)
            {
                case SettingKeys.GamePath:
                    if (!Directory.Exists(value)) return CliCommandResponse.Fail($"目录不存在：{value}");
                    await settings.SetSettingAsync(key, value);
                    break;
                case SettingKeys.GameAutoDetect:
                    if (!bool.TryParse(value, out var flag)) return CliCommandResponse.Fail("Game.AutoDetect 需要 true/false。");
                    await settings.SetSettingAsync(key, flag);
                    break;
                case SettingKeys.GameMode:
                    if (!Enum.TryParse<GameInstallationMode>(value, true, out var mode))
                        return CliCommandResponse.Fail("Game.Mode 需要 Official 或 HellForge。");
                    await settings.SetSettingAsync(key, mode.ToString());
                    break;
                default:
                    await settings.SetSettingAsync(key, value);
                    break;
            }
            return CliCommandResponse.Ok($"已设置 {key} = {value}。", new { key, value });
        }

        public static Task<CliCommandResponse> LoaderListAsync(CliCommandContext context, CancellationToken ct)
        {
            var snapshot = HD2Mimnt.Infrastructure.Analysis.LoaderRegistry.Current;
            var data = new
            {
                ruleVersion = snapshot.RuleVersion,
                initializationError = HD2Mimnt.Infrastructure.Analysis.LoaderRegistry.InitializationError,
                loaders = snapshot.Loaders.Select(loader => new
                {
                    id = loader.Id,
                    displayName = loader.DisplayName,
                    role = loader.Role.ToString(),
                    priority = loader.Priority,
                    mutexGroup = loader.MutexGroup,
                    guestOf = loader.GuestOf
                })
            };
            var message = "加载器识别配置：\n" + string.Join("\n", snapshot.Loaders.Select(loader =>
                $"  {loader.DisplayName}  角色 {loader.Role}  优先级 {loader.Priority}"));
            return Task.FromResult(CliCommandResponse.Ok(message, data));
        }

        public static async Task<CliCommandResponse> IasStatusAsync(CliCommandContext context, CancellationToken ct)
        {
            var ias = context.Get<IIasSystemService>();
            var location = await ias.GetInstallLocationAsync(ct);
            var active = await ias.GetActiveRuntimeIdAsync(ct);
            IReadOnlyList<string> runtimes = location == null ? [] : await ias.DiscoverRuntimeIdsAsync(location, ct);
            var data = new { installLocation = location, activeRuntimeId = active, runtimes };
            var message = $"IAS 安装位置：{location ?? "(未设置)"}\n活动 Runtime：{active ?? "(未设置)"}\n发现 Runtime：{runtimes.Count} 个";
            return CliCommandResponse.Ok(message, data);
        }

        public static async Task<CliCommandResponse> IasComponentsAsync(CliCommandContext context, CancellationToken ct)
        {
            var components = await context.Get<IIasSystemService>().GetModelComponentsAsync(ct);
            var data = components.Select(component => new { modId = component.ModId, modName = component.ModName, fileCount = component.Files.Count });
            return CliCommandResponse.Ok($"共 {components.Count} 个 IAS 模型组件。", data);
        }

        private static string? ReadValue(SettingsService settings, string key) => key switch
        {
            SettingKeys.GameAutoDetect => settings.GetSetting(key, true).ToString(),
            _ => settings.GetSetting<string?>(key, null)
        };

        private static string WhitelistText() => string.Join("\n", Whitelist.Select(item => $"  {item.Key}：{item.Value}"));
    }
}
