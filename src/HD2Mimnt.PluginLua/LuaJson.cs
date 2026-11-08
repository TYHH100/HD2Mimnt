using System.Text.Json;
using MoonSharp.Interpreter;

namespace HD2Mimnt.PluginLua;

/// <summary>
/// Lua 值与 JSON 的双向转换。
///
/// 规则刻意保持可预测：连续整数键 1..n 视为数组，其余键视为对象键；
/// 函数/协程等无法表达的值直接跳过，避免把宿主内部对象泄露给脚本或 IPC。
/// </summary>
internal static class LuaJson
{
    /// <summary>把 JSON 文本解析成 Lua 值；空串与非法 JSON 返回 nil。</summary>
    public static DynValue Parse(Script script, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return DynValue.Nil;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return FromElement(script, document.RootElement);
        }
        catch (JsonException)
        {
            return DynValue.Nil;
        }
    }

    /// <summary>JSON 元素 → Lua 值。</summary>
    public static DynValue FromElement(Script script, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var table = new Table(script);
                foreach (var property in element.EnumerateObject())
                {
                    table.Set(property.Name, FromElement(script, property.Value));
                }
                return DynValue.NewTable(table);
            }
            case JsonValueKind.Array:
            {
                var table = new Table(script);
                var index = 1;
                foreach (var item in element.EnumerateArray())
                {
                    table.Set(index++, FromElement(script, item));
                }
                return DynValue.NewTable(table);
            }
            case JsonValueKind.String:
                return DynValue.NewString(element.GetString() ?? string.Empty);
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l)
                    ? DynValue.NewNumber(l)
                    : DynValue.NewNumber(element.GetDouble());
            case JsonValueKind.True:
                return DynValue.NewBoolean(true);
            case JsonValueKind.False:
                return DynValue.NewBoolean(false);
            default:
                return DynValue.Nil;
        }
    }

    /// <summary>Lua 值 → JSON 文本；nil 返回 null（调用方据此省略字段）。</summary>
    public static string? Serialize(DynValue value)
    {
        var clr = ToClr(value);
        if (clr is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(clr, PluginJsonOptions);
    }

    /// <summary>Lua 值 → 可序列化的 CLR 对象图。</summary>
    public static object? ToClr(DynValue value)
    {
        switch (value.Type)
        {
            case DataType.Nil:
            case DataType.Void:
                return null;
            case DataType.Boolean:
                return value.Boolean;
            case DataType.Number:
                return value.Number;
            case DataType.String:
                return value.String;
            case DataType.Table:
                return TableToClr(value.Table!);
            default:
                // 函数、协程、userdata 不向外传递
                return null;
        }
    }

    private static object? TableToClr(Table table)
    {
        var length = table.Length;
        if (length > 0)
        {
            var list = new List<object?>(length);
            for (var i = 1; i <= length; i++)
            {
                list.Add(ToClr(table.Get(i)));
            }
            return list;
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in table.Pairs)
        {
            if (pair.Key.Type is not (DataType.String or DataType.Number))
            {
                continue;
            }

            var key = pair.Key.Type == DataType.String
                ? pair.Key.String
                : pair.Key.Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            map[key] = ToClr(pair.Value);
        }
        return map;
    }

    private static readonly JsonSerializerOptions PluginJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
