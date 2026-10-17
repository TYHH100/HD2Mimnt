using System.Text;
using System.Text.RegularExpressions;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

/// <summary>只清理 ReShade 对 HD2IAS 字体目录的引用，保留其它 INI 内容及换行格式。</summary>
public static class ReShadeFontCleaner
{
    public static byte[]? Clean(byte[] contents, string gamePath)
    {
        var utf8 = new UTF8Encoding(false, true);
        var text = utf8.GetString(contents);
        var lines = Regex.Split(text, "(\r\n|\n|\r)");
        var bin = Path.Combine(Path.GetFullPath(gamePath), "bin");
        var fonts = Path.Combine(bin, "HD2IndependentRig", "fonts") + Path.DirectorySeparatorChar;
        var section = "";
        var previousFont = "";
        foreach (var line in lines)
        {
            if (!TryRead(line, ref section, out var key, out var value, out _)) continue;
            if (section.Equals("HD2Interface", StringComparison.OrdinalIgnoreCase)
                && key.Equals("previous_font", StringComparison.OrdinalIgnoreCase)) previousFont = value;
        }
        var oldPath = Resolve(previousFont);
        var restoredFont = oldPath != null && !IsSystemFont(previousFont) && File.Exists(oldPath) ? previousFont : "";
        section = "";
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!TryRead(line, ref section, out var key, out var value, out var equals) || !IsSystemFont(value)) continue;
            var overlay = section.Equals("OVERLAY", StringComparison.OrdinalIgnoreCase) || section.Equals("STYLE", StringComparison.OrdinalIgnoreCase);
            if (overlay && (key.Equals("Font", StringComparison.OrdinalIgnoreCase)
                || key.Equals("EditorFont", StringComparison.OrdinalIgnoreCase) || key.Equals("LatinFont", StringComparison.OrdinalIgnoreCase)))
                lines[index] = line[..(equals + 1)] + (key.Equals("Font", StringComparison.OrdinalIgnoreCase) ? restoredFont : "");
            else if (section.Equals("HD2Interface", StringComparison.OrdinalIgnoreCase) && key.Equals("previous_font", StringComparison.OrdinalIgnoreCase))
                lines[index] = line[..(equals + 1)];
            else continue;
            changed = true;
        }
        return changed ? utf8.GetBytes(string.Concat(lines)) : null;

        string? Resolve(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try { return Path.GetFullPath(value.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar), bin); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
        bool IsSystemFont(string value) => Resolve(value)?.StartsWith(fonts, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryRead(string line, ref string section, out string key, out string value, out int equals)
    {
        key = value = ""; equals = -1;
        var trimmed = line.Trim().TrimStart('\uFEFF');
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { section = trimmed[1..^1].Trim(); return false; }
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#') return false;
        equals = line.IndexOf('=');
        if (equals < 0) return false;
        key = line[..equals].Trim(); value = line[(equals + 1)..].Trim();
        return true;
    }
}
