using MoonSharp.Interpreter;

namespace HD2Mimnt.PluginLua;

/// <summary>Table 的便捷写入：直接传 Table 时自动包成 DynValue。</summary>
internal static class LuaTableExtensions
{
    public static void Set(this Table table, string key, Table value)
        => table.Set(key, DynValue.NewTable(value));

    public static void Set(this Table table, int index, Table value)
        => table.Set(index, DynValue.NewTable(value));
}
