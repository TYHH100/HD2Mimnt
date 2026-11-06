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

        Add(new CliCommandDescriptor("gmod-ias list",
            "gmod-ias list <addon.gma|目录>",
            "列出 GMOD addon 内可转换的模型（骨骼/网格/开关/skin/动骨/材质统计），用于挑选 convert 的模型参数。"),
            CliHandlers.GmodIas.ListAsync);

        Add(new CliCommandDescriptor("gmod-ias armors",
            "gmod-ias armors [--domain body|helmet] [--filter 关键字]",
            "列出可作为替换目标的身体护甲与头盔（代号、名称、Archive ID、Unit 闭包大小），供 convert 的 --armor 使用。"),
            CliHandlers.GmodIas.ArmorsAsync);

        Add(new CliCommandDescriptor("gmod-ias convert",
            "gmod-ias convert <addon.gma|目录> <model.mdl> <输出目录> [--project ID] " +
            "[--armor 代号|Archive] [--helmet-armor 代号|Archive] " +
            "[--archive 十六进制] [--domain body|helmet] [--equipment 十六进制] [--native 十六进制,...] " +
            "[--switch-mode auto|independent|composite] [--split both|body|helmet|none] " +
            "[--helmet-archive 十六进制] [--no-skin] [--no-physics] [--no-source]",
            "把 GMOD(Source) 模型转换成 HD2 IAS 转换包。默认按骨骼权重把头与身体拆成 body/ 与 helmet/ 两套包，" +
            "保留全部备选/皮肤/材质/贴图/动骨源文件并生成 IAS 开关。用 --armor 选择游戏内要替换的护甲，" +
            "产物会同时给出 package/ 数据模组与 *.hd2irpack.zip 独立骨架配套包。",
            RequiresYes: true), CliHandlers.GmodIas.ConvertAsync);

        return new CliCommandCatalog(commands);
    }

    /// <summary>解析 token（首个为命令名，支持 "mod list" 两段式）；返回 null 表示未注册。</summary>
    public (CliCommandDescriptor Descriptor, CliCommandHandler Handler, CliCommandRequest Request)? Resolve(IReadOnlyList<string> tokens, bool json, bool yes)
    {
        if (tokens.Count == 0) return null;
        var first = tokens[0];
        // 先查两段式（"deploy status"），再退回单词（"deploy"），避免子命令被宿主命令抢先命中。
        if (tokens.Count >= 2 && _commands.TryGetValue(first + " " + tokens[1], out var twoPart))
            return (twoPart.Descriptor, twoPart.Handler,
                new CliCommandRequest(twoPart.Descriptor.Name, tokens.Skip(2).ToList(), json, yes));
        if (_commands.TryGetValue(first, out var single))
            return (single.Descriptor, single.Handler,
                new CliCommandRequest(single.Descriptor.Name, tokens.Skip(1).ToList(), json, yes));
        return null;
    }

    /// <summary>统一入口：确认拦截 → 执行 → 把业务异常翻译为 ExitCode 1。</summary>
    public async Task<CliCommandResponse> DispatchAsync(IReadOnlyList<string> tokens, IServiceProvider services, bool json, bool yes, CancellationToken ct)
    {
        if (Resolve(tokens, json, yes) is not { } resolved)
            return CliCommandResponse.Usage(unknownCommandText(tokens));
        var (descriptor, handler, request) = resolved;
        if (descriptor.RequiresYes && !request.Yes) return CliCommandResponse.ConfirmRequired(descriptor.Usage);
        var context = new CliCommandContext { Request = request, Services = services };
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
        var positional = context.Request.Arguments.FirstOrDefault(arg => !arg.StartsWith("--"));
        return Task.FromResult(CliCommandResponse.Ok(CommandHelpText(positional)));
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
