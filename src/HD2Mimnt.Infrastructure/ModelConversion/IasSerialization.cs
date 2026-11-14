using System.Globalization;
using System.Text;

namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>
/// 与上游 Python 实现逐字节一致的 Lua/JSON 序列化。
/// 上游用 <c>json.dumps(..., ensure_ascii=True, sort_keys=True, separators=(',', ':'))</c>
/// 生成目录/清单摘要，用 <c>lua()</c> 生成模型目录字面量；差异会直接改变产物哈希。
/// </summary>
public abstract record IasValue
{
    public sealed record Nil : IasValue
    {
        public static readonly Nil Instance = new();
    }

    public sealed record Bool(bool Value) : IasValue;
    public sealed record Integer(long Value) : IasValue;
    public sealed record Float(double Value) : IasValue;
    public sealed record Text(string Value) : IasValue;
    public sealed record Array(IReadOnlyList<IasValue> Items) : IasValue;
    public sealed record Object(IReadOnlyList<KeyValuePair<string, IasValue>> Entries) : IasValue;

    public static IasValue Of(string value) => new Text(value);
    public static IasValue Of(long value) => new Integer(value);
    public static IasValue Of(int value) => new Integer(value);
    public static IasValue Of(bool value) => new Bool(value);
    public static IasValue Of(double value) => new Float(value);
    public static IasValue Of(IEnumerable<IasValue> items) => new Array(items.ToList());
    public static IasValue Obj(params (string Key, IasValue Value)[] entries)
        => new Object(entries.Select(entry => new KeyValuePair<string, IasValue>(entry.Key, entry.Value)).ToList());
}

public static class IasLua
{
    public static string Literal(IasValue value) => value switch
    {
        IasValue.Nil => "nil",
        IasValue.Bool b => b.Value ? "true" : "false",
        IasValue.Integer i => i.Value.ToString(CultureInfo.InvariantCulture),
        IasValue.Float f => PythonFloat.Repr(f.Value),
        IasValue.Text t => QuoteJson(t.Value, ensureAscii: false),
        IasValue.Array a => "{" + string.Join(",", a.Items.Select(Literal)) + "}",
        IasValue.Object o => "{" + string.Join(",", o.Entries
            .OrderBy(entry => entry.Key, IasCodePointComparer.Instance)
            .Select(entry => "[" + QuoteJson(entry.Key, ensureAscii: false) + "]=" + Literal(entry.Value))) + "}",
        _ => throw new InvalidOperationException("未知的 IasValue 节点。")
    };

    /// <summary>上游 <c>json_bytes</c>：ASCII、键排序、无空白的紧凑形式。</summary>
    public static byte[] JsonBytes(IasValue value)
        => Encoding.ASCII.GetBytes(WriteJson(value, ensureAscii: true, sortKeys: true, indent: 0));

    /// <summary>上游 <c>native_model_data</c> 的内嵌 JSON：ASCII、键保持插入顺序、无空白。</summary>
    public static string CompactAscii(IasValue value)
        => WriteJson(value, ensureAscii: true, sortKeys: false, indent: 0);

    /// <summary>清单 <c>json.dumps(..., ensure_ascii=False, indent=2)</c>。</summary>
    public static string PrettyManifest(IasValue value)
        => WriteJson(value, ensureAscii: false, sortKeys: false, indent: 2) + "\n";

    public static string WriteJson(IasValue value, bool ensureAscii, bool sortKeys, int indent)
    {
        var builder = new StringBuilder();
        Write(value, builder, ensureAscii, sortKeys, indent, 0);
        return builder.ToString();
    }

    private static void Write(IasValue value, StringBuilder builder, bool ensureAscii, bool sortKeys, int indent, int level)
    {
        switch (value)
        {
            case IasValue.Nil:
                builder.Append("null");
                break;
            case IasValue.Bool b:
                builder.Append(b.Value ? "true" : "false");
                break;
            case IasValue.Integer i:
                builder.Append(i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case IasValue.Float f:
                if (!double.IsFinite(f.Value)) throw new InvalidDataException("JSON 不允许非有限数值。");
                builder.Append(PythonFloat.Repr(f.Value));
                break;
            case IasValue.Text t:
                builder.Append(QuoteJson(t.Value, ensureAscii));
                break;
            case IasValue.Array array:
                if (array.Items.Count == 0) { builder.Append("[]"); break; }
                builder.Append('[');
                for (var index = 0; index < array.Items.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    NewLine(builder, indent, level + 1);
                    Write(array.Items[index], builder, ensureAscii, sortKeys, indent, level + 1);
                }
                NewLine(builder, indent, level);
                builder.Append(']');
                break;
            case IasValue.Object obj:
                if (obj.Entries.Count == 0) { builder.Append("{}"); break; }
                var entries = sortKeys
                    ? obj.Entries.OrderBy(entry => entry.Key, IasCodePointComparer.Instance).ToList()
                    : obj.Entries;
                builder.Append('{');
                for (var index = 0; index < entries.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    NewLine(builder, indent, level + 1);
                    builder.Append(QuoteJson(entries[index].Key, ensureAscii)).Append(':');
                    if (indent > 0) builder.Append(' ');
                    Write(entries[index].Value, builder, ensureAscii, sortKeys, indent, level + 1);
                }
                NewLine(builder, indent, level);
                builder.Append('}');
                break;
            default:
                throw new InvalidOperationException("未知的 IasValue 节点。");
        }
    }

    private static void NewLine(StringBuilder builder, int indent, int level)
    {
        if (indent <= 0) return;
        builder.Append('\n').Append(' ', indent * level);
    }

    public static string QuoteJson(string value, bool ensureAscii)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            switch (ch)
            {
                case '"': builder.Append("\\\""); continue;
                case '\\': builder.Append("\\\\"); continue;
                case '\b': builder.Append("\\b"); continue;
                case '\f': builder.Append("\\f"); continue;
                case '\n': builder.Append("\\n"); continue;
                case '\r': builder.Append("\\r"); continue;
                case '\t': builder.Append("\\t"); continue;
            }
            if (ch < 0x20)
            {
                AppendEscape(builder, ch);
                continue;
            }
            if (ensureAscii && ch >= 0x7F)
            {
                AppendEscape(builder, ch);
                continue;
            }
            // 代理对被 JSON 原样保留；ensure_ascii 时会逐半区转义，与 Python 一致。
            builder.Append(ch);
        }
        builder.Append('"');
        return builder.ToString();
    }

    private static void AppendEscape(StringBuilder builder, char ch)
        => builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
}

/// <summary>按 Unicode 码点比较，对应 Python <c>sorted(str)</c>。</summary>
public sealed class IasCodePointComparer : IComparer<string>
{
    public static readonly IasCodePointComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var length = Math.Min(x.Length, y.Length);
        for (var index = 0; index < length; index++)
        {
            var left = x[index];
            var right = y[index];
            if (left == right) continue;
            return CodePoint(left) < CodePoint(right) ? -1 : 1;
        }
        return x.Length.CompareTo(y.Length);
    }

    private static int CodePoint(char ch) => ch;
}

/// <summary>Python <c>repr(float)</c>：最短往返表示与固定/科学计数切换阈值。</summary>
public static class PythonFloat
{
    public static string Repr(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";
        var sign = value < 0 ? "-" : string.Empty;
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var exponentIndex = text.IndexOfAny(['E', 'e']);
        if (exponentIndex >= 0)
        {
            exponent = int.Parse(text[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..exponentIndex];
        }
        var decimalIndex = text.IndexOf('.');
        var integerDigits = decimalIndex < 0 ? text.Length : decimalIndex;
        var mantissa = decimalIndex < 0 ? text : text.Remove(decimalIndex, 1);
        var firstSignificant = 0;
        while (firstSignificant < mantissa.Length && mantissa[firstSignificant] == '0') firstSignificant++;
        if (firstSignificant == mantissa.Length) return sign + "0.0";
        var digits = mantissa[firstSignificant..].TrimEnd('0');
        var decimalPoint = integerDigits + exponent - firstSignificant;
        return sign + Format(digits, decimalPoint);
    }

    private static string Format(string digits, int decimalPoint)
    {
        if (decimalPoint <= -4 || decimalPoint > 16)
        {
            var body = digits.Length > 1 ? digits[..1] + "." + digits[1..] : digits;
            var exponent = decimalPoint - 1;
            var exponentText = Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
            return body + "e" + (exponent < 0 ? "-" : "+") + exponentText;
        }
        if (decimalPoint <= 0) return "0." + new string('0', -decimalPoint) + digits;
        if (decimalPoint >= digits.Length) return digits + new string('0', decimalPoint - digits.Length) + ".0";
        return digits[..decimalPoint] + "." + digits[decimalPoint..];
    }
}
