using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Application.Cli;

internal static partial class CliHandlers
{
    /// <summary>mod 扩展域：标签、本地版本、缩略图、在线更新检查、来源链接、GPU 扫描与内容哈希。</summary>
    internal static class ModsExt
    {
        public static async Task<CliCommandResponse> TagAddAsync(CliCommandContext context, CancellationToken ct)
        {
            var (mod, tag) = await ResolveTagArgsAsync(context, ct);
            var updated = await context.Get<ModService>().AddUserTagAsync(mod.Id, tag, ct);
            return CliCommandResponse.Ok($"已为 {updated.Name} 添加标签 {tag}。", new { modId = mod.Id, tag, tags = updated.UserTags });
        }

        public static async Task<CliCommandResponse> TagRemoveAsync(CliCommandContext context, CancellationToken ct)
        {
            var (mod, tag) = await ResolveTagArgsAsync(context, ct);
            var updated = await context.Get<ModService>().RemoveUserTagAsync(mod.Id, tag, ct);
            return CliCommandResponse.Ok($"已移除标签 {tag}。", new { modId = mod.Id, tag, tags = updated.UserTags });
        }

        public static async Task<CliCommandResponse> TagColorsAsync(CliCommandContext context, CancellationToken ct)
        {
            var colors = await context.Get<ModService>().GetTagColorsAsync(ct);
            var data = colors.Select(entry => new { tag = entry.Key, color = entry.Value }).ToList();
            return CliCommandResponse.Ok(colors.Count == 0 ? "还没有标签颜色。" : $"共 {colors.Count} 个标签颜色。",
                data);
        }

        public static async Task<CliCommandResponse> TagColorSetAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var color = args.Option("--color");
            var positional = args.Positionals();
            if (positional.Count != 1 || string.IsNullOrWhiteSpace(positional[0]))
                return CliCommandResponse.Usage("用法: mod tag color <标签> [--color #RRGGBB]（缺省 --color 清除颜色）");
            await context.Get<ModService>().SetTagColorAsync(positional[0], color, ct);
            return CliCommandResponse.Ok(color == null
                ? $"已清除标签 {positional[0]} 的颜色。"
                : $"标签 {positional[0]} 颜色已设为 {color}。", new { tag = positional[0], color });
        }

        public static async Task<CliCommandResponse> VersionsListAsync(CliCommandContext context, CancellationToken ct)
        {
            var mod = await FindModAsync(context, ct);
            var versions = await context.Get<ModService>().GetLocalVersionsAsync(mod.Id, ct);
            var data = versions.Select(version => new
            {
                versionId = version.Id,
                savedAt = version.SavedAt,
                modName = version.Content.Name,
                version = version.Content.Version
            }).ToList();
            var message = versions.Count == 0
                ? $"{mod.Name} 没有本地版本记录。"
                : $"{mod.Name} 共 {versions.Count} 个本地版本：";
            return CliCommandResponse.Ok(message + (versions.Count > 0
                ? "\n  " + string.Join("\n  ", versions.Select(version => $"{version.SavedAt:yyyy-MM-dd HH:mm} {version.Id} {version.Content.Name} v{version.Content.Version}"))
                : ""), data);
        }

        public static async Task<CliCommandResponse> VersionSwitchAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2 || !Guid.TryParse(positional[1], out var versionId))
                return CliCommandResponse.Usage("用法: mod versions switch <ID|名称> <版本ID>（mod versions list 获取版本 ID）");
            var mod = await FindModAsync(context, positional[0], ct);
            var result = await context.Get<ModService>().SwitchLocalVersionAsync(mod.Id, versionId, ct);
            return CliCommandResponse.Ok(
                $"已切换到版本 {versionId}（{result.Mod.Name}，{(result.WasDeployed ? "已重新部署" : "未部署，需要手动 deploy")}）。",
                new { modId = result.Mod.Id, versionId, wasDeployed = result.WasDeployed, cleanupWarning = result.CleanupWarning });
        }

        public static async Task<CliCommandResponse> VersionDeleteAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2 || !Guid.TryParse(positional[1], out var versionId))
                return CliCommandResponse.Usage("用法: mod versions delete <ID|名称> <版本ID>");
            var mod = await FindModAsync(context, positional[0], ct);
            await context.Get<ModService>().DeleteLocalVersionAsync(mod.Id, versionId, ct);
            return CliCommandResponse.Ok($"已删除版本 {versionId}。", new { modId = mod.Id, versionId });
        }

        public static async Task<CliCommandResponse> ThumbnailAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var generate = args.Flag("--generate");
            var outputPath = args.Option("--out");
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: mod thumbnail <ID|名称> [--generate | --out 导出路径]");
            var mod = await FindModAsync(context, positional[0], ct);
            var mods = context.Get<ModService>();

            if (outputPath != null)
            {
                var bytes = await mods.ReadOriginalPreviewAsync(mod.Id, ct);
                if (bytes is not { Length: > 0 }) return CliCommandResponse.Fail($"{mod.Name} 没有可导出的预览图。");
                await File.WriteAllBytesAsync(outputPath, bytes, ct);
                return CliCommandResponse.Ok($"已导出预览图到 {outputPath}（{bytes.Length} 字节）。", new { modId = mod.Id, path = outputPath });
            }

            var path = generate
                ? await mods.GenerateThumbnailAsync(mod.Id, ct)
                : mods.GetThumbnailPath(mod.Id);
            var exists = path != null && File.Exists(path);
            return exists
                ? CliCommandResponse.Ok($"{mod.Name} 的缩略图：{path}", new { modId = mod.Id, path })
                : CliCommandResponse.Fail($"{mod.Name} 没有缩略图；可用 --generate 重新生成。", new { modId = mod.Id });
        }

        public static async Task<CliCommandResponse> UpdateCheckAsync(CliCommandContext context, CancellationToken ct)
        {
            var mod = await FindModAsync(context, ct);
            var info = await context.Get<ModOnlineService>().CheckAsync(mod.Id, ct);
            var installedVersion = mod.PlatformLink?.InstalledVersion ?? mod.Version;
            var platformVersions = info.Files
                .Select(file => file.Version)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Distinct().ToList();
            var data = new
            {
                modId = mod.Id,
                name = mod.Name,
                installedVersion,
                platformVersions,
                hasUpdate = platformVersions.Any(version => version != installedVersion),
                supportsUpdates = mod.PlatformLink?.SupportsUpdates == true
            };
            var message = platformVersions.Count == 0
                ? $"{mod.Name} 平台上没有版本信息。"
                : $"已装 {installedVersion}；平台版本：{string.Join("、", platformVersions)}。";
            return CliCommandResponse.Ok(message, data);
        }

        public static async Task<CliCommandResponse> LinkGetAsync(CliCommandContext context, CancellationToken ct)
        {
            var mod = await FindModAsync(context, ct);
            var link = mod.PlatformLink;
            return link == null
                ? CliCommandResponse.Fail($"{mod.Name} 还没有来源链接；可用 mod link set <ID|名称> <链接> 设置。")
                : CliCommandResponse.Ok($"{mod.Name} 来源：{link}", new { modId = mod.Id, link = link.ToString() });
        }

        public static async Task<CliCommandResponse> LinkSetAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: mod link set <ID|名称> <平台链接>");
            var mod = await FindModAsync(context, positional[0], ct);
            var links = context.Get<ModLinkService>();
            var link = links.Parse(positional[1]);
            var updated = await links.SaveAsync(mod.Id, link, ct);
            return CliCommandResponse.Ok($"已保存 {updated.Name} 的来源链接：{link}", new { modId = updated.Id, link = link.ToString() });
        }

        public static async Task<CliCommandResponse> LinkFetchAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: mod link fetch <平台链接>");
            var link = await context.Get<ModLinkService>().FetchAsync(positional[0], ct);
            return CliCommandResponse.Ok($"已解析平台链接：{link}", new { link = link.ToString() });
        }

        public static async Task<CliCommandResponse> GpuScanAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            var gpu = context.Get<ModGpuUsageService>();
            if (positional.Count == 1 && positional[0].Equals("invalidate", StringComparison.OrdinalIgnoreCase))
            {
                gpu.InvalidateScans();
                return CliCommandResponse.Ok("GPU 占用扫描缓存已失效。");
            }
            if (positional.Count == 1 && positional[0].Equals("profile", StringComparison.OrdinalIgnoreCase))
            {
                var profiles = context.Get<ProfileService>();
                var profile = await profiles.GetActiveProfileAsync(ct)
                    ?? throw new InvalidOperationException("没有活动配置。");
                var result = await gpu.ScanProfileAsync(profile.Id, ct);
                return CliCommandResponse.Ok($"配置 {profile.Name} 的 GPU 占用扫描完成。", result);
            }
            return CliCommandResponse.Usage("用法: mod gpu-scan profile | mod gpu-scan invalidate");
        }

        public static async Task<CliCommandResponse> HashAsync(CliCommandContext context, CancellationToken ct)
        {
            var mod = await FindModAsync(context, ct);
            var hash = await context.Get<ModService>().EnsureModHashAsync(mod.Id, ct);
            return CliCommandResponse.Ok($"{mod.Name} 内容哈希：{hash}", new { modId = mod.Id, hash });
        }

        private static async Task<(Mod Mod, string Tag)> ResolveTagArgsAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2) throw new ArgumentException("用法: mod tag add|remove <ID|名称> <标签>");
            var mod = await FindModAsync(context, positional[0], ct);
            return (mod, positional[1]);
        }

        private static async Task<Mod> FindModAsync(CliCommandContext context, CancellationToken ct)
        {
            var positional = context.Request.Arguments.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
            var reference = positional.Count > 0 ? positional[0] : "";
            return await CliResolve.FindModAsync(context.Get<ModService>(), reference, ct)
                ?? throw new KeyNotFoundException($"找不到模组“{reference}”。");
        }

        private static async Task<Mod> FindModAsync(CliCommandContext context, string reference, CancellationToken ct)
            => await CliResolve.FindModAsync(context.Get<ModService>(), reference, ct)
               ?? throw new KeyNotFoundException($"找不到模组“{reference}”。");
    }

    /// <summary>compat 扩展域：批量修复（选项参数化）、备份历史与回滚、资源名数据刷新。</summary>
    internal static class CompatExt
    {
        public static async Task<CliCommandResponse> RepairBatchAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var dryRun = args.Flag("--dry-run");
            var positional = args.Positionals();
            if (positional.Count == 0) return CliCommandResponse.Usage("用法: compat repair-batch <ID|名称|all> [--dry-run]");
            var compat = context.Get<ModCompatibilityService>();
            var ids = await ResolveIdsAsync(context, positional[0], ct);

            var session = context.Session;
            var progress = new Progress<CompatibilityBatchProgress>(value =>
                session.Progress.Report(new CliProgressEvent("批量修复",
                    value.Total > 0 ? value.Completed * 100.0 / value.Total : null,
                    value.Item?.Name, null)));

            var items = dryRun
                ? await compat.CreateBatchRepairPlanAsync(ids, cancellationToken: ct, progress: progress)
                : await compat.RepairBatchAsync(ids, cancellationToken: ct, progress: progress);
            var failed = items.Where(item => item.State is CompatibilityBatchState.Failed or CompatibilityBatchState.Blocked).ToList();
            var data = new
            {
                dryRun,
                total = items.Count,
                failed = failed.Count,
                items = items.Select(item => new
                {
                    modId = item.ModId,
                    name = item.Name,
                    state = item.State.ToString(),
                    error = item.Error
                })
            };
            var summary = $"{(dryRun ? "修复计划" : "批量修复")}完成：{items.Count} 个模组，失败/阻塞 {failed.Count}。";
            return failed.Count == 0
                ? CliCommandResponse.Ok(summary, data)
                : CliCommandResponse.Fail(summary + "\n" + string.Join("\n", failed.Select(item => $"  ✗ {item.Name}: {item.Error ?? item.State.ToString()}")), data);
        }

        public static async Task<CliCommandResponse> BackupsAsync(CliCommandContext context, CancellationToken ct)
        {
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(),
                context.Request.Arguments.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "", ct)
                ?? throw new KeyNotFoundException("找不到模组。");
            var backups = await context.Get<ModCompatibilityService>().GetBackupHistoryAsync(mod.Id, ct);
            var data = backups.Select(entry => new
            {
                backupPath = entry.BackupPath,
                createdAt = entry.CreatedAtUtc
            }).ToList();
            var message = backups.Count == 0
                ? $"{mod.Name} 没有兼容性修复备份。"
                : $"{mod.Name} 共 {backups.Count} 份备份。";
            return CliCommandResponse.Ok(message, data);
        }

        public static async Task<CliCommandResponse> RestoreBackupAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2)
                return CliCommandResponse.Usage("用法: compat restore-backup <ID|名称> <备份路径|备份时间yyyy-MM-ddTHH:mm:ss>");
            var mod = await CliResolve.FindModAsync(context.Get<ModService>(), positional[0], ct)
                ?? throw new KeyNotFoundException($"找不到模组“{positional[0]}”。");
            var compat = context.Get<ModCompatibilityService>();
            if (DateTime.TryParse(positional[1], out var targetUtc))
            {
                var result = await compat.RollbackToAsync(mod.Id, targetUtc, ct);
                return DescribeRepair(result, $"已回滚 {mod.Name} 到 {targetUtc:u}");
            }
            var restore = await compat.RestoreBackupAsync(mod.Id, positional[1], ct);
            return DescribeRepair(restore, $"已恢复 {mod.Name} 的备份 {positional[1]}");
        }

        public static async Task<CliCommandResponse> RefreshNamesAsync(CliCommandContext context, CancellationToken ct)
        {
            await context.Get<ModCompatibilityService>().RefreshResourceNamesAsync(ct);
            return CliCommandResponse.Ok("兼容性资源名数据已刷新。");
        }

        private static async Task<IReadOnlyList<Guid>> ResolveIdsAsync(CliCommandContext context, string reference, CancellationToken ct)
        {
            var mods = context.Get<ModService>();
            if (reference.Equals("all", StringComparison.OrdinalIgnoreCase))
                return (await mods.GetAllModsAsync(ct)).Select(mod => mod.Id).ToList();
            var mod = await CliResolve.FindModAsync(mods, reference, ct)
                ?? throw new KeyNotFoundException($"找不到模组“{reference}”。");
            return new[] { mod.Id };
        }

        private static CliCommandResponse DescribeRepair(CompatibilityRepairResult result, string action)
        {
            var data = new { success = result.Success, changed = result.Changed, appliedActions = result.AppliedActionCount, error = result.ErrorMessage };
            if (!result.Success)
                return CliCommandResponse.Fail($"{action}失败：{result.ErrorMessage}", data);
            return CliCommandResponse.Ok(
                $"{action}完成：应用 {result.AppliedActionCount} 项操作，备份 {result.BackupPaths.Count} 份。", data);
        }
    }
}
