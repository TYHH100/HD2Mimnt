namespace HD2Mimnt.Application.Cli;

/// <summary>
/// CLI 命令注册表：命令名（"组 动词"或单词）→ 元数据 + handler。
/// RequiresYes 的命令在 <see cref="DispatchAsync"/> 统一拦截，handler 内不用重复判断确认。
/// </summary>
public sealed class CliCommandCatalog
{
    private readonly Dictionary<string, (CliCommandDescriptor Descriptor, CliCommandHandler Handler)> _commands;

    private CliCommandCatalog(Dictionary<string, (CliCommandDescriptor, CliCommandHandler)> commands) => _commands = commands;

    public IReadOnlyCollection<CliCommandDescriptor> Commands => _commands.Values.Select(entry => entry.Descriptor).ToList();

    public static CliCommandCatalog Build()
    {
        var commands = new Dictionary<string, (CliCommandDescriptor Descriptor, CliCommandHandler Handler)>(StringComparer.OrdinalIgnoreCase);
        void Add(CliCommandDescriptor descriptor, CliCommandHandler handler) => commands[descriptor.Name] = (descriptor, handler);

        Add(new CliCommandDescriptor("status", "status", "总览：版本、数据目录、游戏路径、活动配置与模组统计。"), CliHandlers.Status.StatusAsync);
        Add(new CliCommandDescriptor("help", "help [命令]", "列出命令或查看某条命令的用法。"), HelpAsync);

        Add(new CliCommandDescriptor("mod list", "mod list [--search 关键字] [--limit 数量]", "列出模组库。"), CliHandlers.Mods.ListAsync);
        Add(new CliCommandDescriptor("mod show", "mod show <ID|名称>", "查看模组详情（ID 可用名称代替）。"), CliHandlers.Mods.ShowAsync);
        Add(new CliCommandDescriptor("mod import", "mod import <路径...>", "导入压缩包或文件夹模组。"), CliHandlers.Mods.ImportAsync);
        Add(new CliCommandDescriptor("mod delete", "mod delete <ID|名称>", "从模组库删除模组。", RequiresYes: true), CliHandlers.Mods.DeleteAsync);
        Add(new CliCommandDescriptor("mod enable", "mod enable <ID|名称>", "在活动配置中启用模组（不在配置内会先加入）。"), CliHandlers.Mods.SetEnabledAsync(true));
        Add(new CliCommandDescriptor("mod disable", "mod disable <ID|名称>", "在活动配置中禁用模组。"), CliHandlers.Mods.SetEnabledAsync(false));
        Add(new CliCommandDescriptor("mod rename", "mod rename <ID|名称> <新名称>", "重命名模组。"), CliHandlers.Mods.RenameAsync);

        Add(new CliCommandDescriptor("profile list", "profile list", "列出全部配置。"), CliHandlers.Profiles.ListAsync);
        Add(new CliCommandDescriptor("profile show", "profile show <ID|名称>", "查看配置成员明细。"), CliHandlers.Profiles.ShowAsync);
        Add(new CliCommandDescriptor("profile create", "profile create <名称>", "新建配置。", RequiresYes: true), CliHandlers.Profiles.CreateAsync);
        Add(new CliCommandDescriptor("profile activate", "profile activate <ID|名称>", "切换活动配置。", RequiresYes: true), CliHandlers.Profiles.ActivateAsync);
        Add(new CliCommandDescriptor("profile add-mod", "profile add-mod <配置> <模组>", "把模组加入配置（默认启用）。"), CliHandlers.Profiles.AddModAsync);
        Add(new CliCommandDescriptor("profile remove-mod", "profile remove-mod <配置> <模组>", "把模组移出配置。"), CliHandlers.Profiles.RemoveModAsync);

        Add(new CliCommandDescriptor("deploy", "deploy [--profile 配置] [--method copy|symlink|hardlink] [--order top|bottom]", "把配置部署到游戏目录。", RequiresYes: true), CliHandlers.Deploy.DeployAsync);
        Add(new CliCommandDescriptor("deploy status", "deploy status", "查看当前游戏安装的部署记录概要。"), CliHandlers.Deploy.StatusAsync);
        Add(new CliCommandDescriptor("undeploy", "undeploy", "清除当前游戏安装的全部已部署文件并删除记录。", RequiresYes: true), CliHandlers.Deploy.UndeployAsync);

        Add(new CliCommandDescriptor("compat check", "compat check <ID|名称|all> [--reference 版本]", "检查模组与游戏版本的兼容性。"), CliHandlers.Compat.CheckAsync);
        Add(new CliCommandDescriptor("compat repair", "compat repair <ID|名称>", "按诊断修复模组结构问题。", RequiresYes: true), CliHandlers.Compat.RepairAsync);

        Add(new CliCommandDescriptor("settings list", "settings list", "列出可通过 CLI 修改的设置白名单及当前值。"), CliHandlers.Settings.ListAsync);
        Add(new CliCommandDescriptor("settings get", "settings get <键>", "读取白名单内的设置。"), CliHandlers.Settings.GetAsync);
        Add(new CliCommandDescriptor("settings set", "settings set <键> <值>", "修改白名单内的设置。", RequiresYes: true), CliHandlers.Settings.SetAsync);

        Add(new CliCommandDescriptor("loader list", "loader list", "列出加载器识别配置（角色、互斥组、从属关系）。"), CliHandlers.Settings.LoaderListAsync);
        Add(new CliCommandDescriptor("ias status", "ias status", "查看 IAS 安装位置与运行时状态。"), CliHandlers.Settings.IasStatusAsync);
        Add(new CliCommandDescriptor("ias components", "ias components", "列出 IAS 模型组件。"), CliHandlers.Settings.IasComponentsAsync);
        Add(new CliCommandDescriptor("ias manage status", "ias manage status", "查看 HD2IAS 包管理状态。"), CliHandlers.IasExt.ManagementStateAsync);
        Add(new CliCommandDescriptor("ias install", "ias install <包路径>", "安装 HD2IAS 包到游戏目录。", RequiresYes: true), CliHandlers.IasExt.InstallAsync);
        Add(new CliCommandDescriptor("ias uninstall", "ias uninstall", "卸载当前 HD2IAS 安装。", RequiresYes: true), CliHandlers.IasExt.UninstallAsync);
        Add(new CliCommandDescriptor("ias generate", "ias generate", "按当前配置生成 HD2IAS 内容。", RequiresYes: true), CliHandlers.IasExt.GenerateAsync);
        Add(new CliCommandDescriptor("ias companion-regenerate", "ias companion-regenerate", "重建 armor 配套组件。", RequiresYes: true), CliHandlers.IasExt.CompanionRegenerateAsync);
        Add(new CliCommandDescriptor("ias runtime list", "ias runtime list", "列出 IAS 运行时与当前活动运行时。"), CliHandlers.IasExt.RuntimeListAsync);
        Add(new CliCommandDescriptor("ias runtime set", "ias runtime set <运行时ID>", "切换活动 IAS 运行时。"), CliHandlers.IasExt.RuntimeSetAsync);

        Add(new CliCommandDescriptor("profile delete", "profile delete <ID|名称>", "删除配置。", RequiresYes: true), CliHandlers.ProfilesExt.DeleteAsync);
        Add(new CliCommandDescriptor("profile rename", "profile rename <ID|名称> <新名称>", "重命名配置。"), CliHandlers.ProfilesExt.RenameAsync);
        Add(new CliCommandDescriptor("profile duplicate", "profile duplicate <ID|名称> --name 新名称", "复制配置。"), CliHandlers.ProfilesExt.DuplicateAsync);
        Add(new CliCommandDescriptor("profile separator add", "profile separator add <配置> <名称> [--color #RRGGBB] [--at 位置]", "添加分隔符分组。"), CliHandlers.ProfilesExt.SeparatorAddAsync);
        Add(new CliCommandDescriptor("profile separator update", "profile separator update <配置> <分隔符> [--name 新名] [--color #RRGGBB]", "更新分隔符。"), CliHandlers.ProfilesExt.SeparatorUpdateAsync);
        Add(new CliCommandDescriptor("profile separator remove", "profile separator remove <配置> <分隔符>", "移除分隔符（保留成员）。"), CliHandlers.ProfilesExt.SeparatorRemoveAsync);
        Add(new CliCommandDescriptor("profile separator collapse", "profile separator collapse <配置> <分隔符> [--open]", "折叠/展开分隔符。"), CliHandlers.ProfilesExt.SeparatorCollapseAsync);
        Add(new CliCommandDescriptor("profile move-mod", "profile move-mod <配置> <分隔符> <模组...>", "把模组移入分隔符分组。"), CliHandlers.ProfilesExt.MoveModsAsync);
        Add(new CliCommandDescriptor("profile reorder", "profile reorder <配置> <模组...>", "按给定顺序重排配置成员。"), CliHandlers.ProfilesExt.ReorderAsync);
        Add(new CliCommandDescriptor("profile normalize", "profile normalize <配置>", "标准化配置结构。"), CliHandlers.ProfilesExt.NormalizeAsync);
        Add(new CliCommandDescriptor("profile compare", "profile compare <配置A> <配置B>", "对比两个配置的成员与启停差异。"), CliHandlers.ProfilesExt.CompareAsync);

        Add(new CliCommandDescriptor("conflicts scan", "conflicts scan [配置]", "扫描配置的补丁资源覆盖冲突（只读）。"), CliHandlers.Conflicts.ScanAsync);
        Add(new CliCommandDescriptor("search", "search <关键字> [--fuzzy] [--pinyin]", "全库搜索（精确/模糊/拼音）。"), CliHandlers.Search.QueryAsync);

        Add(new CliCommandDescriptor("mod tag add", "mod tag add <ID|名称> <标签>", "给模组加用户标签。"), CliHandlers.ModsExt.TagAddAsync);
        Add(new CliCommandDescriptor("mod tag remove", "mod tag remove <ID|名称> <标签>", "移除用户标签。"), CliHandlers.ModsExt.TagRemoveAsync);
        Add(new CliCommandDescriptor("mod tag colors", "mod tag colors", "列出全部标签颜色。"), CliHandlers.ModsExt.TagColorsAsync);
        Add(new CliCommandDescriptor("mod tag color", "mod tag color <标签> [--color #RRGGBB]", "设置/清除标签颜色。"), CliHandlers.ModsExt.TagColorSetAsync);
        Add(new CliCommandDescriptor("mod versions list", "mod versions list <ID|名称>", "列出本地版本记录。"), CliHandlers.ModsExt.VersionsListAsync);
        Add(new CliCommandDescriptor("mod versions switch", "mod versions switch <ID|名称> <版本ID>", "切换到本地版本。"), CliHandlers.ModsExt.VersionSwitchAsync);
        Add(new CliCommandDescriptor("mod versions delete", "mod versions delete <ID|名称> <版本ID>", "删除本地版本记录。"), CliHandlers.ModsExt.VersionDeleteAsync);
        Add(new CliCommandDescriptor("mod thumbnail", "mod thumbnail <ID|名称> [--generate | --out 导出路径]", "查看/生成/导出缩略图。"), CliHandlers.ModsExt.ThumbnailAsync);
        Add(new CliCommandDescriptor("mod update-check", "mod update-check <ID|名称>", "检查在线更新。"), CliHandlers.ModsExt.UpdateCheckAsync);
        Add(new CliCommandDescriptor("mod link get", "mod link get <ID|名称>", "查看模组来源链接。"), CliHandlers.ModsExt.LinkGetAsync);
        Add(new CliCommandDescriptor("mod link set", "mod link set <ID|名称> <平台链接>", "保存模组来源链接。"), CliHandlers.ModsExt.LinkSetAsync);
        Add(new CliCommandDescriptor("mod link fetch", "mod link fetch <平台链接>", "解析平台链接信息。"), CliHandlers.ModsExt.LinkFetchAsync);
        Add(new CliCommandDescriptor("mod gpu-scan", "mod gpu-scan profile | invalidate", "GPU 占用扫描（活动配置）或清缓存。"), CliHandlers.ModsExt.GpuScanAsync);
        Add(new CliCommandDescriptor("mod hash", "mod hash <ID|名称>", "计算模组内容哈希。"), CliHandlers.ModsExt.HashAsync);

        Add(new CliCommandDescriptor("compat repair-batch", "compat repair-batch <ID|名称|all> [--dry-run]", "批量兼容性修复（计划或执行）。", RequiresYes: true), CliHandlers.CompatExt.RepairBatchAsync);
        Add(new CliCommandDescriptor("compat backups", "compat backups <ID|名称>", "列出模组的修复备份。"), CliHandlers.CompatExt.BackupsAsync);
        Add(new CliCommandDescriptor("compat restore-backup", "compat restore-backup <ID|名称> <备份路径|时间>", "恢复/回滚兼容性备份。", RequiresYes: true), CliHandlers.CompatExt.RestoreBackupAsync);
        Add(new CliCommandDescriptor("compat refresh-names", "compat refresh-names", "刷新兼容性资源名数据。"), CliHandlers.CompatExt.RefreshNamesAsync);

        Add(new CliCommandDescriptor("settings list", "settings list", "列出全部可读写设置键、类型与当前值。"), CliHandlers.SettingsExt.ListAllAsync);
        Add(new CliCommandDescriptor("settings get", "settings get <键>", "读取设置。"), CliHandlers.SettingsExt.GetAsync);
        Add(new CliCommandDescriptor("settings set", "settings set <键> <值>", "修改设置（全量白名单，类型校验）。", RequiresYes: true), CliHandlers.SettingsExt.SetAsync);
        Add(new CliCommandDescriptor("settings reset", "settings reset [前缀]", "恢复设置到默认（缺省全部）。", RequiresYes: true), CliHandlers.SettingsExt.ResetAsync);

        Add(new CliCommandDescriptor("game launch", "game launch", "启动游戏（官方或 HellForge）。", RequiresYes: true), CliHandlers.Game.LaunchAsync);
        Add(new CliCommandDescriptor("game detect", "game detect", "自动检测游戏目录。"), CliHandlers.Game.DetectAsync);
        Add(new CliCommandDescriptor("game validate", "game validate <目录>", "校验游戏目录有效性。"), CliHandlers.Game.ValidateAsync);
        Add(new CliCommandDescriptor("game version", "game version", "查询游戏版本。"), CliHandlers.Game.VersionAsync);

        Add(new CliCommandDescriptor("backup list", "backup list", "列出数据库备份。"), CliHandlers.Backup.ListAsync);
        Add(new CliCommandDescriptor("backup create", "backup create", "创建数据库备份。"), CliHandlers.Backup.CreateAsync);
        Add(new CliCommandDescriptor("backup restore", "backup restore <备份路径>", "从备份恢复数据库（重启后生效）。", RequiresYes: true), CliHandlers.Backup.RestoreAsync);
        Add(new CliCommandDescriptor("backup delete", "backup delete <备份路径>", "删除备份。", RequiresYes: true), CliHandlers.Backup.DeleteAsync);

        Add(new CliCommandDescriptor("app update-check", "app update-check", "检查应用更新。"), CliHandlers.App.UpdateCheckAsync);
        Add(new CliCommandDescriptor("app update-apply", "app update-apply", "下载并暂存新版本。", RequiresYes: true), CliHandlers.App.UpdateApplyAsync);

        Add(new CliCommandDescriptor("hellforge status", "hellforge status", "查看 HellForge 安装与运行状态。"), CliHandlers.HellForge.StatusAsync);
        Add(new CliCommandDescriptor("hellforge profiles", "hellforge profiles", "列出 HellForge 档案。"), CliHandlers.HellForge.ProfilesAsync);
        Add(new CliCommandDescriptor("hellforge profile create", "hellforge profile create <名称> [模板]", "创建 HellForge 档案。", RequiresYes: true), CliHandlers.HellForge.CreateProfileAsync);
        Add(new CliCommandDescriptor("hellforge profile select", "hellforge profile select <名称>", "切换 HellForge 档案。", RequiresYes: true), CliHandlers.HellForge.SelectProfileAsync);
        Add(new CliCommandDescriptor("hellforge session", "hellforge session start|stop", "启动/停止 HellForge 会话。", RequiresYes: true), CliHandlers.HellForge.SessionAsync);
        Add(new CliCommandDescriptor("hellforge mods", "hellforge mods list|add|remove|switch|order", "管理 HellForge 模组。", RequiresYes: true), CliHandlers.HellForge.ModsAsync);
        Add(new CliCommandDescriptor("hellforge settings", "hellforge settings get | set --键 值", "读写 HellForge 启动器设置。", RequiresYes: true), CliHandlers.HellForge.SettingsAsync);
        Add(new CliCommandDescriptor("hellforge logs", "hellforge logs", "查看 HellForge 会话日志。"), CliHandlers.HellForge.LogsAsync);
        Add(new CliCommandDescriptor("hellforge sync", "hellforge sync <配置> [--order top|bottom] [--remove]", "把配置同步到 HellForge（Worker 任务）。", RequiresYes: true), CliHandlers.HellForge.SyncAsync);

        Add(new CliCommandDescriptor("hd2cl candidates", "hd2cl candidates", "列出可转换的 IAS 模组。"), CliHandlers.Hd2Cl.CandidatesAsync);
        Add(new CliCommandDescriptor("hd2cl variants", "hd2cl variants <ID|名称>", "列出模组的可用转换变体。"), CliHandlers.Hd2Cl.VariantsAsync);
        Add(new CliCommandDescriptor("hd2cl convert", "hd2cl convert <ID|名称> --variant 变体名 | --folders 列表", "转换为 HD2CL 角色库（Worker 编译）。", RequiresYes: true), CliHandlers.Hd2Cl.ConvertAsync);
        Add(new CliCommandDescriptor("hd2cl export", "hd2cl export <ID|名称> <输出目录>", "导出角色库压缩包。"), CliHandlers.Hd2Cl.ExportAsync);
        Add(new CliCommandDescriptor("mmd inspect", "mmd inspect <资源包路径> [--source 选项]", "检视 MMD 资源包。"), CliHandlers.Mmd.InspectAsync);
        Add(new CliCommandDescriptor("mmd convert", "mmd convert <资源包路径> [--source 选项]", "MMD 资源适配（Worker 执行）。", RequiresYes: true), CliHandlers.Mmd.ConvertAsync);
        Add(new CliCommandDescriptor("mmd export", "mmd export <资源包路径> <输出目录>", "导出 MMD 适配包。", RequiresYes: true), CliHandlers.Mmd.ExportAsync);

        Add(new CliCommandDescriptor("plugin list", "plugin list", "列出已安装插件。"), CliHandlers.Plugins.ListAsync);
        Add(new CliCommandDescriptor("plugin install", "plugin install <插件包路径>", "安装插件。", RequiresYes: true), CliHandlers.Plugins.InstallAsync);
        Add(new CliCommandDescriptor("plugin enable", "plugin enable <插件ID>", "启用插件。", RequiresYes: true), CliHandlers.Plugins.EnableAsync);
        Add(new CliCommandDescriptor("plugin disable", "plugin disable <插件ID>", "停用插件。", RequiresYes: true), CliHandlers.Plugins.DisableAsync);
        Add(new CliCommandDescriptor("plugin uninstall", "plugin uninstall <插件ID>", "卸载插件。", RequiresYes: true), CliHandlers.Plugins.UninstallAsync);
        Add(new CliCommandDescriptor("plugin permissions", "plugin permissions <插件ID>", "查看插件权限。"), CliHandlers.Plugins.PermissionsAsync);
        Add(new CliCommandDescriptor("plugin grant", "plugin grant <插件ID> <权限名>", "授予插件权限。", RequiresYes: true), CliHandlers.Plugins.GrantAsync);
        Add(new CliCommandDescriptor("plugin revoke", "plugin revoke <插件ID> <权限名>", "撤销插件权限。", RequiresYes: true), CliHandlers.Plugins.RevokeAsync);
        Add(new CliCommandDescriptor("plugin commands", "plugin commands <插件ID>", "列出插件贡献的命令。"), CliHandlers.Plugins.CommandsAsync);
        Add(new CliCommandDescriptor("plugin exec", "plugin exec <插件ID> <命令名> [--args JSON]", "执行插件命令。", RequiresYes: true), CliHandlers.Plugins.ExecAsync);
        Add(new CliCommandDescriptor("plugin history", "plugin history <插件ID>", "查看插件执行历史。"), CliHandlers.Plugins.HistoryAsync);

        Add(new CliCommandDescriptor("browse platforms", "browse platforms", "列出可用下载平台。"), CliHandlers.Browse.PlatformsAsync);
        Add(new CliCommandDescriptor("browse select", "browse select <平台ID>", "切换当前浏览平台。"), CliHandlers.Browse.SelectAsync);
        Add(new CliCommandDescriptor("browse search", "browse search <关键字> [--page 页码]", "在当前平台搜索。"), CliHandlers.Browse.SearchAsync);
        Add(new CliCommandDescriptor("browse details", "browse details <平台模组ID|链接>", "查看平台模组详情与文件。"), CliHandlers.Browse.DetailsAsync);
        Add(new CliCommandDescriptor("browse download", "browse download <平台模组ID|链接> [--file 序号]", "下载并导入（Worker 任务）。", RequiresYes: true), CliHandlers.Browse.DownloadAsync);
        Add(new CliCommandDescriptor("download direct", "download direct <URL> [--out 目录]", "直链下载。", RequiresYes: true), CliHandlers.Download.DirectAsync);
        Add(new CliCommandDescriptor("account nexus status", "account nexus status", "检查 Nexus 凭据与限流状态（只读）。"), CliHandlers.Account.NexusStatusAsync);
        Add(new CliCommandDescriptor("account nexus api-key", "account nexus api-key [--key KEY]", "保存 Nexus API Key（CLI 授权路径）。", RequiresYes: true), CliHandlers.Account.NexusApiKeyAsync);
        Add(new CliCommandDescriptor("account nexus logout", "account nexus logout", "退出 Nexus 登录。", RequiresYes: true), CliHandlers.Account.NexusLogoutAsync);
        Add(new CliCommandDescriptor("account ayaka status", "account ayaka status", "查看 Ayaka 凭据状态（只读）。"), CliHandlers.Account.AyakaStatusAsync);
        Add(new CliCommandDescriptor("account ayaka api-key", "account ayaka api-key [--key KEY]", "保存 Ayaka API Key。", RequiresYes: true), CliHandlers.Account.AyakaApiKeyAsync);
        Add(new CliCommandDescriptor("account ayaka logout", "account ayaka logout", "清除 Ayaka API Key。", RequiresYes: true), CliHandlers.Account.AyakaLogoutAsync);

        Add(new CliCommandDescriptor("tool list", "tool list", "列出注册的外部工具。"), CliHandlers.Tools.ListAsync);
        Add(new CliCommandDescriptor("tool register", "tool register <名称> <exe 路径>", "注册外部工具。", RequiresYes: true), CliHandlers.Tools.RegisterAsync);
        Add(new CliCommandDescriptor("tool delete", "tool delete <工具ID>", "删除外部工具。", RequiresYes: true), CliHandlers.Tools.DeleteAsync);
        Add(new CliCommandDescriptor("tool launch", "tool launch <工具ID> [关联文件]", "启动外部工具。", RequiresYes: true), CliHandlers.Tools.LaunchAsync);
        Add(new CliCommandDescriptor("tool soundfix scan", "tool soundfix scan", "扫描音效结构问题（只读）。"), CliHandlers.Tools.SoundFixScanAsync);
        Add(new CliCommandDescriptor("tool soundfix repair", "tool soundfix repair", "修复全部音效结构问题。", RequiresYes: true), CliHandlers.Tools.SoundFixRepairAsync);
        Add(new CliCommandDescriptor("tool lua-extract", "tool lua-extract", "全库提取 Lua 字节码到独立目录。", RequiresYes: true), CliHandlers.Tools.LuaExtractAsync);

        Add(new CliCommandDescriptor("migration import preview", "migration import preview <源管理器目录>", "预览从外部管理器导入。"), CliHandlers.Migration.ImportPreviewAsync);
        Add(new CliCommandDescriptor("migration import execute", "migration import execute <源管理器目录>", "执行导入。", RequiresYes: true), CliHandlers.Migration.ImportExecuteAsync);
        Add(new CliCommandDescriptor("migration export preview", "migration export preview <目标管理器目录>", "预览导出到外部管理器。"), CliHandlers.Migration.ExportPreviewAsync);
        Add(new CliCommandDescriptor("migration export execute", "migration export execute <目标管理器目录>", "执行导出。", RequiresYes: true), CliHandlers.Migration.ExportExecuteAsync);

        Add(new CliCommandDescriptor("mod create from-dir", "mod create from-dir <模组目录> <输出 manifest>", "从目录生成模组清单。", RequiresYes: true), CliHandlers.Creation.FromDirAsync);
        Add(new CliCommandDescriptor("mod create pack", "mod create pack <模组目录> <输出压缩包>", "从目录打包模组。", RequiresYes: true), CliHandlers.Creation.PackAsync);

        Add(new CliCommandDescriptor("task list", "task list [--limit 数量]", "列出运行中与历史任务。"), CliHandlers.Tasks.ListAsync);
        Add(new CliCommandDescriptor("task show", "task show <任务ID>", "查看任务详情（进度、阶段、错误）。"), CliHandlers.Tasks.ShowAsync);
        Add(new CliCommandDescriptor("task cancel", "task cancel <任务ID>", "取消运行中的任务。"), CliHandlers.Tasks.CancelAsync);
        Add(new CliCommandDescriptor("task kill", "task kill <任务ID>", "强制终止任务进程。"), CliHandlers.Tasks.KillAsync);
        Add(new CliCommandDescriptor("task clear", "task clear", "删除全部已结束的任务记录。"), CliHandlers.Tasks.ClearAsync);
        Add(new CliCommandDescriptor("task wait", "task wait <任务ID> [--timeout 秒]", "等待任务到达终态后输出结果。"), CliHandlers.Tasks.WaitAsync);
        Add(new CliCommandDescriptor("schema", "schema [--json]", "机器可读的全部命令清单（名称、用法、破坏性标志）。"), SchemaAsync);

        return new CliCommandCatalog(commands);
    }

    /// <summary>解析 token：最长前缀匹配命令名（支持 "mod list" 两段与 "mod tag add" 三段式）。</summary>
    public (CliCommandDescriptor Descriptor, CliCommandHandler Handler, CliCommandRequest Request)? Resolve(
        IReadOnlyList<string> tokens, bool json, bool yes, bool async = false)
    {
        if (tokens.Count == 0) return null;
        // 从最长（3 段）到最短（1 段）尝试命中，保证 "deploy status" 不被单词 "deploy" 抢先。
        for (var take = Math.Min(3, tokens.Count); take >= 1; take--)
        {
            var name = string.Join(' ', tokens.Take(take));
            if (_commands.TryGetValue(name, out var entry))
                return (entry.Descriptor, entry.Handler,
                    new CliCommandRequest(entry.Descriptor.Name, tokens.Skip(take).ToList(), json, yes, async));
        }
        return null;
    }

    /// <summary>统一入口：确认拦截 → 执行 → 把业务异常翻译为 ExitCode 1。</summary>
    public async Task<CliCommandResponse> DispatchAsync(IReadOnlyList<string> tokens, IServiceProvider services, bool json, bool yes, CancellationToken ct,
        CliCommandSession? session = null, bool async = false)
    {
        if (Resolve(tokens, json, yes, async) is not { } resolved)
            return CliCommandResponse.Usage(unknownCommandText(tokens));
        var (descriptor, handler, request) = resolved;
        if (descriptor.RequiresYes && !request.Yes) return CliCommandResponse.ConfirmRequired(descriptor.Usage);
        var context = new CliCommandContext { Request = request, Services = services, Session = session ?? CliCommandSession.None };
        try
        {
            return await handler(context, ct);
        }
        catch (ArgumentException ex)
        {
            return CliCommandResponse.Usage(ex.Message + "\n用法: " + descriptor.Usage);
        }
        catch (KeyNotFoundException ex)
        {
            return CliCommandResponse.Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return CliCommandResponse.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidDataException)
        {
            return CliCommandResponse.Fail(ex.Message);
        }
    }

    private string unknownCommandText(IReadOnlyList<string> tokens)
    {
        var name = tokens.Count == 0 ? "(空)" : string.Join(' ', tokens);
        return $"未知命令：{name}\n\n{BuildHelpText()}";
    }

    private static Task<CliCommandResponse> HelpAsync(CliCommandContext context, CancellationToken _)
    {
        // "help mod list" / "mod list --help" 都可能到达这里：拼接全部位置参数得到完整命令名。
        var positional = string.Join(' ', context.Request.Arguments.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)));
        return Task.FromResult(CliCommandResponse.Ok(CommandHelpText(positional.Length > 0 ? positional : null)));
    }

    /// <summary>schema：输出机器可读的命令清单，供 AI/脚本一次调用发现全部能力。</summary>
    private static Task<CliCommandResponse> SchemaAsync(CliCommandContext context, CancellationToken _)
    {
        var catalog = Build();
        var commands = catalog.Commands
            .Select(descriptor => new
            {
                name = descriptor.Name,
                usage = descriptor.Usage,
                summary = descriptor.Summary,
                requiresYes = descriptor.RequiresYes
            })
            .OrderBy(entry => entry.name, StringComparer.Ordinal)
            .ToList();
        var data = new
        {
            version = HD2Mimnt.Core.AppInfo.Version,
            exitCodes = new[] { "0=成功", "1=业务失败", "2=用法错误或缺少 --yes 确认" },
            globalOptions = new[] { "--json", "--yes/-y", "--stream", "--async", "--timeout <秒>", "--help/-h", "--version", "--（其后按字面参数处理）" },
            commands
        };
        return Task.FromResult(CliCommandResponse.Ok($"共 {commands.Count} 条命令，其中 {commands.Count(item => item.requiresYes)} 条需要 --yes 确认。", data));
    }

    public string BuildHelpText()
        => "可用命令（标 ✱ 的需要 --yes 确认）：\n" +
           string.Join("\n", _commands.Values
               .Select(entry => $"  {(entry.Descriptor.RequiresYes ? "✱ " : "  ")}{entry.Descriptor.Usage}")
               .OrderBy(line => line, StringComparer.Ordinal)) +
           "\n\n全局选项：--json 输出 JSON；--yes 确认破坏性操作；--timeout 秒数（默认 600）。";

    public static string CommandHelpText(string? name)
    {
        var catalog = Build();
        if (name == null) return catalog.BuildHelpText();
        return catalog._commands.TryGetValue(name, out var entry)
            ? $"{entry.Descriptor.Summary}\n用法: {entry.Descriptor.Usage}{(entry.Descriptor.RequiresYes ? "\n需要 --yes 确认。" : "")}"
            : $"未知命令：{name}\n\n{catalog.BuildHelpText()}";
    }
}
