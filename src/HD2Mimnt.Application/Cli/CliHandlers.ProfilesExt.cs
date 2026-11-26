using HD2Mimnt.Application.Search;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Application.Cli;

internal static partial class CliHandlers
{
    /// <summary>profile 扩展域：删除/重命名/复制、分隔符分组、成员移动排序、模组选项、对比与标准化。</summary>
    internal static class ProfilesExt
    {
        public static async Task<CliCommandResponse> DeleteAsync(CliCommandContext context, CancellationToken ct)
        {
            var profile = await ResolveAsync(context, ct);
            await context.Get<ProfileService>().DeleteProfileAsync(profile.Id, ct);
            return CliCommandResponse.Ok($"已删除配置 {profile.Name}（{profile.Id}）。", new { profileId = profile.Id, name = profile.Name });
        }

        public static async Task<CliCommandResponse> RenameAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: profile rename <ID|名称> <新名称>");
            var profile = await FindAsync(context, positional[0], ct);
            await context.Get<ProfileService>().RenameProfileAsync(profile.Id, positional[1], ct);
            return CliCommandResponse.Ok($"配置已重命名为 {positional[1]}。", new { profileId = profile.Id, name = positional[1] });
        }

        public static async Task<CliCommandResponse> DuplicateAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var newName = args.Option("--name");
            var positional = args.Positionals();
            if (positional.Count != 1 || string.IsNullOrWhiteSpace(newName))
                return CliCommandResponse.Usage("用法: profile duplicate <ID|名称> --name 新名称");
            var profile = await FindAsync(context, positional[0], ct);
            var copy = await context.Get<ProfileService>().DuplicateProfileAsync(profile.Id, newName, ct);
            return CliCommandResponse.Ok($"已复制为配置 {copy.Name}（{copy.Id}）。", new { profileId = copy.Id, name = copy.Name });
        }

        public static async Task<CliCommandResponse> SeparatorAddAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var color = args.Option("--color") ?? "#808080";
            var atValue = args.Option("--at");
            var positional = args.Positionals();
            if (positional.Count != 2)
                return CliCommandResponse.Usage("用法: profile separator add <配置> <名称> [--color #RRGGBB] [--at 位置序号]");
            var profile = await FindAsync(context, positional[0], ct);
            var order = int.TryParse(atValue, out var parsedOrder) ? parsedOrder : profile.Mods.Count + 1;
            var separator = await context.Get<ProfileService>()
                .AddSeparatorAsync(profile.Id, positional[1], order, color, ct);
            return CliCommandResponse.Ok($"已在配置 {profile.Name} 的位置 {order} 添加分隔符 {separator.Name}。",
                new { profileId = profile.Id, separatorId = separator.Id, name = separator.Name, order });
        }

        public static async Task<CliCommandResponse> SeparatorUpdateAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var color = args.Option("--color");
            var name = args.Option("--name");
            var positional = args.Positionals();
            if (positional.Count != 2 || (name == null && color == null))
                return CliCommandResponse.Usage("用法: profile separator update <配置> <分隔符ID|名称> [--name 新名] [--color #RRGGBB]");
            var profile = await FindAsync(context, positional[0], ct);
            var separator = FindSeparatorAsync(profile, positional[1]);
            await context.Get<ProfileService>()
                .UpdateSeparatorAsync(profile.Id, separator.Id, name ?? separator.Name, color, ct);
            return CliCommandResponse.Ok($"分隔符已更新。", new { profileId = profile.Id, separatorId = separator.Id });
        }

        public static async Task<CliCommandResponse> SeparatorRemoveAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: profile separator remove <配置> <分隔符ID|名称>");
            var profile = await FindAsync(context, positional[0], ct);
            var separator = FindSeparatorAsync(profile, positional[1]);
            await context.Get<ProfileService>().RemoveSeparatorAsync(profile.Id, separator.Id, ct);
            return CliCommandResponse.Ok($"已移除分隔符 {separator.Name}。", new { profileId = profile.Id, separatorId = separator.Id });
        }

        public static async Task<CliCommandResponse> SeparatorCollapseAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var collapsed = !args.Flag("--open");
            var positional = args.Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: profile separator collapse <配置> <分隔符ID|名称> [--open]");
            var profile = await FindAsync(context, positional[0], ct);
            var separator = FindSeparatorAsync(profile, positional[1]);
            await context.Get<ProfileService>().SetSeparatorCollapsedAsync(profile.Id, separator.Id, collapsed, ct);
            return CliCommandResponse.Ok($"分隔符 {separator.Name} 已{(collapsed ? "折叠" : "展开")}。",
                new { profileId = profile.Id, separatorId = separator.Id, collapsed });
        }

        public static async Task<CliCommandResponse> MoveModsAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count < 2)
                return CliCommandResponse.Usage("用法: profile move-mod <配置> <分隔符ID|名称> <模组...>（模组移入分隔符分组）");
            var profile = await FindAsync(context, positional[0], ct);
            var separator = FindSeparatorAsync(profile, positional[1]);
            var modIds = new List<Guid>();
            foreach (var modRef in positional.Skip(2))
            {
                var mod = await CliResolve.FindModAsync(context.Get<ModService>(), modRef, ct)
                    ?? throw new KeyNotFoundException($"找不到模组“{modRef}”。");
                modIds.Add(mod.Id);
            }
            var moved = await context.Get<ProfileService>().MoveModsToGroupAsync(profile.Id, separator.Id, modIds, ct);
            return CliCommandResponse.Ok($"已把 {moved} 个模组移入分组 {separator.Name}。",
                new { profileId = profile.Id, separatorId = separator.Id, moved, modIds });
        }

        public static async Task<CliCommandResponse> ReorderAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count < 2)
                return CliCommandResponse.Usage("用法: profile reorder <配置> <模组...>（按给定顺序重排成员）");
            var profile = await FindAsync(context, positional[0], ct);
            var modIds = new List<Guid>();
            foreach (var modRef in positional.Skip(1))
            {
                var mod = await CliResolve.FindModAsync(context.Get<ModService>(), modRef, ct)
                    ?? throw new KeyNotFoundException($"找不到模组“{modRef}”。");
                modIds.Add(mod.Id);
            }
            await context.Get<ProfileService>().ReorderModsAsync(profile.Id, modIds, ct);
            return CliCommandResponse.Ok($"配置 {profile.Name} 的成员顺序已更新（共 {modIds.Count} 项）。", new { profileId = profile.Id, modIds });
        }

        public static async Task<CliCommandResponse> NormalizeAsync(CliCommandContext context, CancellationToken ct)
        {
            var profile = await ResolveAsync(context, ct);
            var normalized = await context.Get<ProfileService>().NormalizeProfileAsync(profile.Id, ct)
                ?? throw new InvalidOperationException($"配置 {profile.Name} 已不存在。");
            return CliCommandResponse.Ok($"配置 {normalized.Name} 已标准化（成员 {normalized.Mods.Count} 项）。",
                new { profileId = normalized.Id, modCount = normalized.Mods.Count });
        }

        public static async Task<CliCommandResponse> CompareAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2)
                return CliCommandResponse.Usage("用法: profile compare <配置A> <配置B>");
            var left = await FindAsync(context, positional[0], ct);
            var right = await FindAsync(context, positional[1], ct);
            var leftIds = left.Mods.Select(item => (item.ModId, item.Enabled)).ToList();
            var rightIds = right.Mods.Select(item => (item.ModId, item.Enabled)).ToList();
            var onlyInLeft = leftIds.Where(item => !rightIds.Any(other => other.ModId == item.ModId)).ToList();
            var onlyInRight = rightIds.Where(item => !leftIds.Any(other => other.ModId == item.ModId)).ToList();
            var stateChanged = leftIds.Where(item => rightIds.Any(other => other.ModId == item.ModId && other.Enabled != item.Enabled)).ToList();
            var mods = context.Get<ModService>();
            var allIds = onlyInLeft.Concat(onlyInRight).Select(item => item.ModId).Concat(stateChanged.Select(item => item.ModId)).Distinct();
            var names = (await mods.GetModsByIdsAsync(allIds, ct)).ToDictionary(mod => mod.Id, mod => mod.Name);
            var data = new
            {
                left = new { id = left.Id, name = left.Name, modCount = left.Mods.Count },
                right = new { id = right.Id, name = right.Name, modCount = right.Mods.Count },
                onlyInLeft = onlyInLeft.Select(item => new { modId = item.ModId, name = names.GetValueOrDefault(item.ModId), enabled = item.Enabled }),
                onlyInRight = onlyInRight.Select(item => new { modId = item.ModId, name = names.GetValueOrDefault(item.ModId), enabled = item.Enabled }),
                stateChanged = stateChanged.Select(item => new { modId = item.ModId, name = names.GetValueOrDefault(item.ModId), enabledInLeft = item.Enabled })
            };
            var message = $"对比 {left.Name} ↔ {right.Name}：仅 A 有 {onlyInLeft.Count}，仅 B 有 {onlyInRight.Count}，启停差异 {stateChanged.Count}。";
            return CliCommandResponse.Ok(message, data);
        }

        private static async Task<Profile> ResolveAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return await FindAsync(context, "(未指定)", ct);
            return await FindAsync(context, positional[0], ct);
        }

        private static async Task<Profile> FindAsync(CliCommandContext context, string reference, CancellationToken ct)
            => await CliResolve.FindProfileAsync(context.Get<ProfileService>(), reference, ct)
               ?? throw new KeyNotFoundException($"找不到配置“{reference}”。");

        private static ProfileSeparator FindSeparatorAsync(Profile profile, string idOrName)
        {
            var separators = profile.Separators;
            if (Guid.TryParse(idOrName, out var id))
            {
                var byId = separators.FirstOrDefault(item => item.Id == id);
                if (byId != null) return byId;
            }
            var byName = separators.Where(item => string.Equals(item.Name, idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            var partial = separators.Where(item => item.Name.Contains(idOrName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (partial.Count == 1) return partial[0];
            throw new KeyNotFoundException($"配置 {profile.Name} 中找不到分隔符“{idOrName}”。");
        }
    }

    /// <summary>conflicts 域：当前配置补丁资源覆盖扫描（只读）。</summary>
    internal static class Conflicts
    {
        public static async Task<CliCommandResponse> ScanAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            var profiles = context.Get<ProfileService>();
            var profile = positional.Count == 0
                ? await profiles.GetActiveProfileAsync(ct) ?? throw new InvalidOperationException("没有活动配置。请用 profile activate 切换，或指定配置。")
                : await CliResolve.FindProfileAsync(profiles, positional[0], ct) ?? throw new KeyNotFoundException($"找不到配置“{positional[0]}”。");

            var scan = await context.Get<ConflictDetectionService>().ScanProfileAsync(profile.Id, ct);
            var data = new
            {
                profileId = scan.ProfileId,
                profileName = scan.ProfileName,
                scannedAt = scan.ScannedAt,
                scannedMods = scan.ScannedModCount,
                skippedMods = scan.SkippedModCount,
                scannedPatches = scan.ScannedPatchCount,
                scannedResources = scan.ScannedResourceCount,
                definiteConflicts = scan.DefiniteConflictCount,
                groups = scan.Groups.Select(group => new
                {
                    resource = group.RelativePath,
                    typeName = group.ResourceTypeName,
                    modCount = group.ModCount,
                    definite = group.IsDefiniteConflict,
                    owners = group.Owners.Select(owner => new
                    {
                        modId = owner.ModId,
                        modName = owner.ModName,
                        order = owner.ModOrder,
                        path = owner.FullPath,
                        size = owner.Size,
                        streamSize = owner.StreamSize,
                        gpuSize = owner.GpuSize
                    })
                }),
                warnings = scan.Warnings.Select(warning => warning.Message).ToList()
            };
            var message = scan.Groups.Count == 0
                ? $"配置 {scan.ProfileName} 无资源覆盖冲突（扫描 {scan.ScannedModCount} 个模组 / {scan.ScannedPatchCount} 个补丁）。"
                : $"配置 {scan.ProfileName}：{scan.Groups.Count} 组资源覆盖（其中确定冲突 {scan.DefiniteConflictCount} 组）。";
            return CliCommandResponse.Ok(message, data);
        }
    }

    /// <summary>search 域：精确/模糊/拼音搜索（索引构建在首次查询时按需执行）。</summary>
    internal static class Search
    {
        public static async Task<CliCommandResponse> QueryAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var fuzzy = args.Flag("--fuzzy");
            var pinyin = args.Flag("--pinyin");
            var positional = args.Positionals();
            if (positional.Count == 0) return CliCommandResponse.Usage("用法: search <关键字> [--fuzzy] [--pinyin]");
            if (args.UnknownFlags() is { Count: > 0 } unknown)
                return CliCommandResponse.Usage("未知选项: " + string.Join(' ', unknown));

            var search = context.Get<SearchService>();
            await search.BuildIndexAsync(ct); // 幂等：索引缺失或库有变化时重建，已建时为低成本校验
            var options = new SearchMatchOptions(fuzzy, pinyin);
            var results = (await search.SearchAsync(positional[0], options, ct)).ToList();
            var data = results.Select(mod => new
            {
                id = mod.Id,
                name = mod.Name,
                category = mod.Category.ToString()
            }).ToList();
            var message = results.Count == 0
                ? $"没有匹配“{positional[0]}”的模组。"
                : $"匹配 {results.Count} 个模组。";
            return CliCommandResponse.Ok(message, data);
        }
    }
}
