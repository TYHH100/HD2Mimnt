using HD2Mimnt.PluginSDK;
using MoonSharp.Interpreter;

namespace HD2Mimnt.PluginLua;

/// <summary>Lua 表的取值辅助，容忍字段缺失与类型不符。</summary>
internal static class LuaTables
{
    public static string? Str(Table? table, string key)
    {
        var value = table?.Get(key);
        if (value is null || value.Type is DataType.Nil or DataType.Void)
        {
            return null;
        }

        return value.Type == DataType.String ? value.String : value.CastToString();
    }

    public static int Int(Table? table, string key, int fallback)
    {
        var value = table?.Get(key);
        return value is { Type: DataType.Number } ? (int)value.Number : fallback;
    }

    public static double Number(Table? table, string key, double fallback)
    {
        var value = table?.Get(key);
        return value is { Type: DataType.Number } ? value.Number : fallback;
    }

    public static bool Bool(Table? table, string key, bool fallback)
    {
        var value = table?.Get(key);
        return value is { Type: DataType.Boolean } ? value.Boolean : fallback;
    }

    public static Table? Sub(Table? table, string key)
        => table?.Get(key) is { Type: DataType.Table } value ? value.Table : null;

    /// <summary>取数组部分（1..n 连续整数键）。</summary>
    public static IEnumerable<Table> ArrayTables(Table? table)
    {
        if (table is null)
        {
            yield break;
        }

        for (var i = 1; i <= table.Length; i++)
        {
            var item = table.Get(i);
            if (item is { Type: DataType.Table })
            {
                yield return item.Table!;
            }
        }
    }
}

/// <summary>
/// 把脚本返回的表转换成声明式界面模型。
///
/// 这里仍是“数据 → 数据”：脚本只能描述区块，管理器用自己的控件渲染，
/// 脚本拿不到任何 UI 对象，也就无法借此读到进程内的凭据。
/// </summary>
internal static class LuaUi
{
    /// <summary>把 ui 表转换成界面更新；无法识别时返回 null。</summary>
    public static PluginUiUpdate? ToUpdate(DynValue value)
    {
        if (value.Type != DataType.Table)
        {
            return null;
        }

        var table = value.Table!;
        var hasUpdateShape =
            LuaTables.Sub(table, "blocks") is not null
            || LuaTables.Str(table, "mode") is not null
            || LuaTables.Str(table, "message") is not null;

        if (hasUpdateShape)
        {
            return new PluginUiUpdate
            {
                Mode = NormalizeMode(LuaTables.Str(table, "mode")),
                TargetId = LuaTables.Str(table, "targetId") ?? LuaTables.Str(table, "target"),
                Message = LuaTables.Str(table, "message"),
                Blocks = ReadBlocks(table)
            };
        }

        // 允许直接返回区块数组，等价于 replace
        if (table.Length > 0)
        {
            return PluginUiUpdate.Replace(ReadBlocks(table).ToArray());
        }

        return null;
    }

    public static List<PluginUiBlock> ReadBlocks(Table table)
    {
        var blocks = new List<PluginUiBlock>();
        foreach (var blockTable in LuaTables.ArrayTables(LuaTables.Sub(table, "blocks") ?? table))
        {
            var block = ToBlock(blockTable);
            if (block is not null)
            {
                blocks.Add(block);
            }
        }
        return blocks;
    }

    public static PluginUiBlock? ToBlock(Table table)
    {
        var type = LuaTables.Str(table, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            // 只给了 text 时按文本块处理
            type = LuaTables.Str(table, "text") is null ? null : PluginUiBlockTypes.Text;
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var block = new PluginUiBlock
        {
            Type = type!,
            Id = LuaTables.Str(table, "id"),
            Text = LuaTables.Str(table, "text"),
            Title = LuaTables.Str(table, "title"),
            Level = LuaTables.Int(table, "level", 2),
            Tone = LuaTables.Str(table, "tone"),
            Icon = LuaTables.Str(table, "icon")
        };

        foreach (var actionTable in LuaTables.ArrayTables(LuaTables.Sub(table, "actions")))
        {
            block.Actions.Add(ToAction(actionTable));
        }

        foreach (var factTable in LuaTables.ArrayTables(LuaTables.Sub(table, "facts")))
        {
            block.Facts.Add(new PluginUiFact
            {
                Label = LuaTables.Str(factTable, "label") ?? string.Empty,
                Value = LuaTables.Str(factTable, "value") ?? string.Empty
            });
        }

        foreach (var itemTable in LuaTables.ArrayTables(LuaTables.Sub(table, "items")))
        {
            block.Items.Add(new PluginUiListItem
            {
                Text = LuaTables.Str(itemTable, "text") ?? string.Empty,
                Detail = LuaTables.Str(itemTable, "detail"),
                Icon = LuaTables.Str(itemTable, "icon")
            });
        }

        if (LuaTables.Sub(table, "input") is { } inputTable)
        {
            block.Input = new PluginUiInput
            {
                Id = LuaTables.Str(inputTable, "id") ?? "value",
                Label = LuaTables.Str(inputTable, "label"),
                Placeholder = LuaTables.Str(inputTable, "placeholder"),
                Value = LuaTables.Str(inputTable, "value"),
                Multiline = LuaTables.Bool(inputTable, "multiline", true),
                Rows = LuaTables.Int(inputTable, "rows", 4)
            };
        }

        return block;
    }

    private static PluginUiAction ToAction(Table table) => new()
    {
        Id = LuaTables.Str(table, "id"),
        Label = LuaTables.Str(table, "label") ?? LuaTables.Str(table, "text") ?? string.Empty,
        Icon = LuaTables.Str(table, "icon"),
        Style = LuaTables.Str(table, "style") ?? PluginUiActionStyles.Secondary,
        Command = LuaTables.Str(table, "command"),
        Arguments = LuaJson.Serialize(table.Get("arguments")),
        Dialog = LuaTables.Str(table, "dialog"),
        RequiredPermission = LuaTables.Str(table, "requiredPermission") ?? LuaTables.Str(table, "permission"),
        SubmitInputs = LuaTables.Bool(table, "submitInputs", true)
    };

    private static string NormalizeMode(string? mode)
        => string.Equals(mode, PluginUiUpdateModes.Append, StringComparison.OrdinalIgnoreCase)
            ? PluginUiUpdateModes.Append
            : PluginUiUpdateModes.Replace;
}
