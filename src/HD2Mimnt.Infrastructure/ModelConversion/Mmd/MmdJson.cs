using System.Text.Json;

namespace HD2Mimnt.Infrastructure.ModelConversion.Mmd;

/// <summary>MMD 源包与登记内嵌 JSON 的解析和字段读取；键顺序必须保留，序列化才与上游逐字节一致。</summary>
public static class MmdJson
{
    public static IasValue Parse(ReadOnlySpan<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8.ToArray());
        return Convert(document.RootElement);
    }

    /// <summary>上游 <c>json_manifest</c>：跳过 BOM，去掉尾逗号后解析。</summary>
    public static IasValue.Object ParseManifest(ReadOnlySpan<byte> raw)
    {
        var text = System.Text.Encoding.UTF8.GetString(raw);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        text = StripTrailingCommas(text);
        var value = Parse(System.Text.Encoding.UTF8.GetBytes(text));
        if (value is not IasValue.Object obj || Text(obj, "Name") is null)
            throw new InvalidDataException("manifest.json 缺少模组名称。");
        return obj;
    }

    private static readonly System.Text.RegularExpressions.Regex TrailingComma = new(
        "(\"(?:\\\\.|[^\"\\\\])*\")|,\\s*([}\\]])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string StripTrailingCommas(string text)
        => TrailingComma.Replace(text, match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);

    private static IasValue Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ConvertObject(element),
        JsonValueKind.Array => new IasValue.Array(element.EnumerateArray().Select(Convert).ToList()),
        JsonValueKind.String => IasValue.Of(element.GetString()!),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? IasValue.Of(integer) : IasValue.Of(element.GetDouble()),
        JsonValueKind.True => IasValue.Of(true),
        JsonValueKind.False => IasValue.Of(false),
        JsonValueKind.Null => IasValue.Nil.Instance,
        _ => throw new InvalidDataException("MMD 数据包含不支持的 JSON 值。")
    };

    private static IasValue.Object ConvertObject(JsonElement element)
    {
        var entries = new List<KeyValuePair<string, IasValue>>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var value = Convert(property.Value);
            if (indexes.TryGetValue(property.Name, out var index)) entries[index] = new(property.Name, value);
            else
            {
                indexes[property.Name] = entries.Count;
                entries.Add(new(property.Name, value));
            }
        }
        return new IasValue.Object(entries);
    }

    public static IasValue? Get(IasValue.Object obj, string key)
        => obj.Entries.FirstOrDefault(entry => entry.Key == key).Value;

    public static string? Text(IasValue.Object obj, string key) => Get(obj, key) as IasValue.Text is { } text ? text.Value : null;

    public static long? Integer(IasValue.Object obj, string key) => Get(obj, key) is IasValue.Integer integer ? integer.Value : null;

    public static double? Number(IasValue.Object obj, string key) => Get(obj, key) switch
    {
        IasValue.Integer integer => integer.Value,
        IasValue.Float value when double.IsFinite(value.Value) => value.Value,
        _ => null
    };

    public static IasValue.Object With(IasValue.Object obj, string key, IasValue value)
    {
        var entries = obj.Entries.ToList();
        var index = entries.FindIndex(entry => entry.Key == key);
        if (index >= 0) entries[index] = new(key, value);
        else entries.Add(new(key, value));
        return new IasValue.Object(entries);
    }

    public static IasValue.Object Without(IasValue.Object obj, string key)
        => new(obj.Entries.Where(entry => entry.Key != key).ToList());
}
