namespace HD2Mimnt.Core.Services;

/// <summary>
/// 模组目录内由管理器生成的派生内容约定：审查用 Lua 拆包输出目录。
/// 判重指纹与内容戳都不计入这些目录，拆包不应使模组身份或既有检查结果失效。
/// </summary>
public static class ModDerivedContent
{
    /// <summary>模组根目录下存放自动拆包 Lua 的文件夹名（审查用途，不参与部署与判重）。</summary>
    public const string LuaFolderName = "LuaExtracted";
}
