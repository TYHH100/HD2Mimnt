namespace HD2Mimnt.PluginSDK;

/// <summary>
/// 声明式界面贡献：插件只描述“有哪些区块、按钮和文本”，
/// 由管理器用自己的控件渲染，插件程序集始终不进入 UI 进程。
///
/// 这条约束是刻意设计的：一旦插件代码跑在 UI 进程里，就能读到主程序内存中的
/// API Key 与凭据，前面做的密钥隔离会全部失效。声明式贡献让插件能加页面、
/// 加菜单、开浮窗、换主题，但接触不到任何 UI 对象或密钥材料。
/// </summary>
public sealed class PluginUiContributions
{
    /// <summary>独立页面，安装后作为导航入口出现。</summary>
    public List<PluginPageContribution> Pages { get; set; } = new();

    /// <summary>浮窗，可由菜单项或页面按钮打开。</summary>
    public List<PluginDialogContribution> Dialogs { get; set; } = new();

    /// <summary>菜单项，按 placement 出现在功能菜单、模组右键菜单或模组库工具栏。</summary>
    public List<PluginMenuContribution> Menus { get; set; } = new();

    /// <summary>主题包：只允许覆盖白名单内的设计令牌。</summary>
    public List<PluginThemeContribution> Themes { get; set; } = new();
}

/// <summary>页面贡献。</summary>
public sealed class PluginPageContribution
{
    /// <summary>页面标识，在同一插件内唯一；同时作为界面事件回传的 surfaceId。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>导航项标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>导航图标（UtilityIcon 的 Kind 名称，可选）。</summary>
    public string? Icon { get; set; }

    /// <summary>页面说明，显示在标题下方。</summary>
    public string? Description { get; set; }

    /// <summary>页面初始区块。</summary>
    public List<PluginUiBlock> Blocks { get; set; } = new();
}

/// <summary>浮窗贡献。</summary>
public sealed class PluginDialogContribution
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public double Width { get; set; } = 560;
    public double Height { get; set; } = 480;
    public List<PluginUiBlock> Blocks { get; set; } = new();
}

/// <summary>菜单贡献。</summary>
public sealed class PluginMenuContribution
{
    public string Id { get; set; } = string.Empty;

    /// <summary>出现位置，取值见 <see cref="PluginCommandPlacements"/>。</summary>
    public string Placement { get; set; } = PluginCommandPlacements.MainMenu;

    public string Label { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Description { get; set; }

    /// <summary>点击后执行的命令名；留空时必须提供 <see cref="Dialog"/>。</summary>
    public string? Command { get; set; }

    /// <summary>点击后打开的浮窗 id；与 <see cref="Command"/> 二选一。</summary>
    public string? Dialog { get; set; }

    /// <summary>命令的静态参数（JSON 对象字符串），会与界面事件合并后传给插件。</summary>
    public string? Arguments { get; set; }

    public string? RequiredPermission { get; set; }
}

/// <summary>主题包：token 覆盖，不执行任何样式代码。</summary>
public sealed class PluginThemeContribution
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// 设计令牌覆盖表。键必须是管理器白名单内的令牌名，值为颜色（#RRGGBB）或数值。
    /// 未知键会被忽略，避免插件通过资源键影响非预期控件。
    /// </summary>
    public Dictionary<string, string> Tokens { get; set; } = new();
}

/// <summary>声明式界面区块。</summary>
public sealed class PluginUiBlock
{
    /// <summary>区块类型，取值见 <see cref="PluginUiBlockTypes"/>。</summary>
    public string Type { get; set; } = PluginUiBlockTypes.Text;

    /// <summary>区块标识，用于增量更新时定位（可选）。</summary>
    public string? Id { get; set; }

    /// <summary>正文；markdown 类型按 Markdown 渲染。</summary>
    public string? Text { get; set; }

    /// <summary>标题（heading / notice / input 的标签）。</summary>
    public string? Title { get; set; }

    /// <summary>标题层级（heading 使用，1-4，默认 2）。</summary>
    public int Level { get; set; } = 2;

    /// <summary>语义色调（notice 使用），取值见 <see cref="PluginUiTones"/>。</summary>
    public string? Tone { get; set; }

    /// <summary>图标（UtilityIcon 的 Kind 名称，可选）。</summary>
    public string? Icon { get; set; }

    /// <summary>按钮组。</summary>
    public List<PluginUiAction> Actions { get; set; } = new();

    /// <summary>键值对列表（facts 使用）。</summary>
    public List<PluginUiFact> Facts { get; set; } = new();

    /// <summary>条目列表（list 使用）。</summary>
    public List<PluginUiListItem> Items { get; set; } = new();

    /// <summary>输入框（input 使用）。</summary>
    public PluginUiInput? Input { get; set; }
}

/// <summary>区块类型常量。</summary>
public static class PluginUiBlockTypes
{
    public const string Markdown = "markdown";
    public const string Text = "text";
    public const string Heading = "heading";
    public const string Divider = "divider";
    public const string Notice = "notice";
    public const string Facts = "facts";
    public const string List = "list";
    public const string Input = "input";
    public const string Actions = "actions";
}

/// <summary>按钮样式常量。</summary>
public static class PluginUiActionStyles
{
    public const string Primary = "primary";
    public const string Secondary = "secondary";
    public const string Danger = "danger";
}

/// <summary>提示色调常量。</summary>
public static class PluginUiTones
{
    public const string Info = "info";
    public const string Success = "success";
    public const string Warning = "warning";
    public const string Danger = "danger";
}

/// <summary>按钮 / 菜单动作定义。</summary>
public sealed class PluginUiAction
{
    /// <summary>动作标识，点击时随界面事件回传（可选）。</summary>
    public string? Id { get; set; }

    public string Label { get; set; } = string.Empty;

    public string? Icon { get; set; }

    /// <summary>样式，取值见 <see cref="PluginUiActionStyles"/>。</summary>
    public string Style { get; set; } = PluginUiActionStyles.Secondary;

    /// <summary>要执行的命令名；留空时使用所属页面的默认命令（若有）。</summary>
    public string? Command { get; set; }

    /// <summary>静态参数（JSON 对象字符串）。</summary>
    public string? Arguments { get; set; }

    /// <summary>打开浮窗的 id；设置后点击只打开浮窗，不执行命令。</summary>
    public string? Dialog { get; set; }

    public string? RequiredPermission { get; set; }

    /// <summary>执行时是否把同页面输入框的值放进界面事件（默认 true）。</summary>
    public bool SubmitInputs { get; set; } = true;
}

/// <summary>输入框定义。</summary>
public sealed class PluginUiInput
{
    public string Id { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Placeholder { get; set; }
    public string? Value { get; set; }
    public bool Multiline { get; set; } = true;
    public int Rows { get; set; } = 4;
}

/// <summary>键值对。</summary>
public sealed class PluginUiFact
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>列表项。</summary>
public sealed class PluginUiListItem
{
    public string Text { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? Icon { get; set; }
}

/// <summary>
/// 命令执行后返回给管理器的界面更新。
/// 声明式结构，管理器自行渲染；插件无法借此访问 UI 对象。
/// </summary>
public sealed class PluginUiUpdate
{
    /// <summary>替换或追加区块。</summary>
    public string Mode { get; set; } = PluginUiUpdateModes.Replace;

    /// <summary>目标区块 id；为空表示整个界面。</summary>
    public string? TargetId { get; set; }

    public List<PluginUiBlock> Blocks { get; set; } = new();

    /// <summary>可选的状态栏提示。</summary>
    public string? Message { get; set; }

    public static PluginUiUpdate Replace(params PluginUiBlock[] blocks)
        => new() { Mode = PluginUiUpdateModes.Replace, Blocks = blocks.ToList() };

    public static PluginUiUpdate Append(params PluginUiBlock[] blocks)
        => new() { Mode = PluginUiUpdateModes.Append, Blocks = blocks.ToList() };
}

/// <summary>界面更新模式。</summary>
public static class PluginUiUpdateModes
{
    public const string Replace = "replace";
    public const string Append = "append";
}
