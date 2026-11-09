namespace HD2Mimnt.PluginSDK;

/// <summary>
/// 命令在宿主界面中的出现位置。
///
/// manifest 的 <c>commands[].placement</c> 使用这里的取值；同时接受常见别名
/// （<c>mod</c>、<c>context-menu</c> 等），比较前统一归一化，避免作者因拼写差异
/// 得到一个永远不会出现的命令。
/// </summary>
public static class PluginCommandPlacements
{
    /// <summary>默认位置：只出现在设置页的插件管理列表中。</summary>
    public const string PluginPage = "pluginPage";

    /// <summary>出现在模组卡片右键菜单中；调用时会附带目标模组的上下文参数。</summary>
    public const string ModContextMenu = "modContextMenu";

    /// <summary>出现在导航栏的“插件”功能菜单中，适合全局动作与浮窗入口。</summary>
    public const string MainMenu = "mainMenu";

    /// <summary>出现在模组库左侧工具栏，适合对当前库生效的动作。</summary>
    public const string LibraryToolbar = "libraryToolbar";

    /// <summary>把作者写的 placement 归一化为受支持的四个位置之一。</summary>
    public static string Normalize(string? placement)
    {
        if (string.IsNullOrWhiteSpace(placement))
        {
            return PluginPage;
        }

        var key = new string(placement
            .Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '_' or '.'))
            .Select(char.ToLowerInvariant)
            .ToArray());

        return key switch
        {
            "mod" or "mods" or "modcontext" or "modcontextmenu" or "contextmenu" or "modmenu"
                => ModContextMenu,
            "main" or "menu" or "mainmenu" or "toolbar" or "functionmenu" or "pluginsmenu"
                => MainMenu,
            "library" or "librarytoolbar" or "modtoolbar" or "listtoolbar" or "toolstrip"
                => LibraryToolbar,
            _ => PluginPage
        };
    }

    /// <summary>该命令是否应出现在模组卡片右键菜单。</summary>
    public static bool IsModContextMenu(string? placement)
        => string.Equals(Normalize(placement), ModContextMenu, StringComparison.Ordinal);

    /// <summary>该命令是否应出现在导航栏的插件功能菜单。</summary>
    public static bool IsMainMenu(string? placement)
        => string.Equals(Normalize(placement), MainMenu, StringComparison.Ordinal);

    /// <summary>该命令是否应出现在模组库工具栏。</summary>
    public static bool IsLibraryToolbar(string? placement)
        => string.Equals(Normalize(placement), LibraryToolbar, StringComparison.Ordinal);
}
