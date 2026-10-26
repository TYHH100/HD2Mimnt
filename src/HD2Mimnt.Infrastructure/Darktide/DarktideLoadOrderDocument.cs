using System.Text;
using HD2Mimnt.Core.Darktide;

namespace HD2Mimnt.Infrastructure.Darktide;

public enum DarktideLoadOrderLineKind
{
    Blank,
    Comment,
    Active
}

public sealed class DarktideLoadOrderLine
{
    internal DarktideLoadOrderLine(string text, string newLine, DarktideLoadOrderLineKind kind)
    {
        Text = text;
        NewLine = newLine;
        Kind = kind;
    }

    public string Text { get; internal set; }
    public string NewLine { get; internal set; }
    public DarktideLoadOrderLineKind Kind { get; internal set; }
    public string DisplayText => Text.Trim();
    public bool IsActive => Kind == DarktideLoadOrderLineKind.Active;
    public bool IsComment => Kind == DarktideLoadOrderLineKind.Comment;
    public string? CommentContent => IsComment ? Text.Trim().TrimStart('-').Trim() : null;

    internal bool IsActiveEntry(string name)
        => IsActive && string.Equals(DisplayText, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 保留原始行、换行符与 BOM 的 mod_load_order.txt 编辑器。只改目标模组所在行的文本，
/// 注释、空行、未知条目和文件末尾状态按原样写回。
/// </summary>
public sealed class DarktideLoadOrderDocument
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly UTF8Encoding Utf8Bom = new(true);
    private readonly List<DarktideLoadOrderLine> _lines;
    private byte[] _originalBytes = Array.Empty<byte>();

    private DarktideLoadOrderDocument(string filePath, List<DarktideLoadOrderLine> lines,
        Encoding encoding, bool hasBom, string newLine)
    {
        FilePath = filePath;
        _lines = lines;
        Encoding = encoding;
        HasBom = hasBom;
        NewLine = newLine;
    }

    public string FilePath { get; }
    public Encoding Encoding { get; }
    public bool HasBom { get; }
    public string NewLine { get; }
    public IReadOnlyList<DarktideLoadOrderLine> Lines => _lines;
    public IReadOnlyList<string> ActiveNames => _lines.Where(line => line.IsActive)
        .Select(line => line.DisplayText).ToList();
    public IReadOnlyList<(string Name, int LineNumber)> CommentedEntries => _lines
        .Select((line, index) => (line, index))
        .Where(item => item.line.IsComment)
        .Select(item => (item.line.CommentContent ?? "", item.index + 1))
        .Where(item => item.Item1.Length > 0)
        .ToList();

    public static bool IsSupportedFile(string path) => File.Exists(path);

    public static DarktideLoadOrderDocument Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (encoding, hasBom, offset) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, offset, bytes.Length - offset);
        var lines = SplitLines(text, out var newLine);
        var document = new DarktideLoadOrderDocument(path, lines, encoding, hasBom, newLine)
        {
            _originalBytes = bytes
        };
        return document;
    }

    public bool IsEnabled(string name) => _lines.Any(line => line.IsActiveEntry(name));

    public int CountActive(string name)
        => _lines.Count(line => line.IsActiveEntry(name));

    public bool Enable(string name, int? atIndex = null)
    {
        ValidateName(name);
        if (IsEnabled(name)) return false;

        var commented = _lines.FirstOrDefault(line => line.IsComment
            && string.Equals(line.CommentContent, name, StringComparison.OrdinalIgnoreCase));
        if (commented != null)
        {
            var marker = commented.Text.IndexOf("--", StringComparison.Ordinal);
            if (marker >= 0)
            {
                var rest = commented.Text[(marker + 2)..];
                if (rest.StartsWith(' ')) rest = rest[1..];
                commented.Text = commented.Text[..marker] + rest;
                commented.Kind = DarktideLoadOrderLineKind.Active;
                return true;
            }
        }

        EnsureTrailingNewline();
        var insertAt = atIndex is { } requested
            ? Math.Clamp(requested, 0, _lines.Count)
            : _lines.Count;
        _lines.Insert(insertAt, new DarktideLoadOrderLine(name, NewLine, DarktideLoadOrderLineKind.Active));
        return true;
    }

    public int Disable(string name)
    {
        ValidateName(name);
        var changed = 0;
        foreach (var line in _lines.Where(line => line.IsActiveEntry(name)))
        {
            var text = line.Text;
            var firstNonWhitespace = 0;
            while (firstNonWhitespace < text.Length && char.IsWhiteSpace(text[firstNonWhitespace])) firstNonWhitespace++;
            line.Text = text[..firstNonWhitespace] + "-- " + text[firstNonWhitespace..];
            line.Kind = DarktideLoadOrderLineKind.Comment;
            changed++;
        }
        return changed;
    }

    public bool SetEnabled(string name, bool enabled)
        => enabled ? Enable(name) : Disable(name) > 0;

    /// <summary>按给定顺序重写现有有效行；集合必须与当前有效行完全一致，否则先启用/禁用。</summary>
    public void ReorderActive(IReadOnlyList<string> orderedNames)
    {
        var activeLines = _lines.Where(line => line.IsActive).ToList();
        if (activeLines.Count != orderedNames.Count)
            throw new InvalidOperationException("有效模组数量与目标顺序不一致。");
        if (activeLines.Select(line => line.DisplayText).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(orderedNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) == false)
            throw new InvalidOperationException("目标顺序包含未登记的模组。");

        for (var index = 0; index < activeLines.Count; index++)
        {
            var line = activeLines[index];
            var text = line.Text;
            var leadingEnd = 0;
            while (leadingEnd < text.Length && char.IsWhiteSpace(text[leadingEnd])) leadingEnd++;
            var trailingStart = text.Length;
            while (trailingStart > leadingEnd && char.IsWhiteSpace(text[trailingStart - 1])) trailingStart--;
            line.Text = text[..leadingEnd] + orderedNames[index] + text[trailingStart..];
        }
    }

    /// <summary>把目标集合设为有效登记；目标之外的现有登记会被注释掉，未知有效项保留在末尾。</summary>
    public void SetActiveOrder(IReadOnlyList<string> orderedNames)
    {
        var desired = orderedNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var existing in ActiveNames.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!desired.Contains(existing, StringComparer.OrdinalIgnoreCase)) Disable(existing);
        foreach (var name in desired) Enable(name);
        var fullOrder = desired.Concat(ActiveNames.Where(name =>
            !desired.Contains(name, StringComparer.OrdinalIgnoreCase))).ToList();
        ReorderActive(fullOrder);
    }

    public DarktideLoadOrderSaveResult Save(string? backupDirectory = null)
    {
        var bytes = RenderBytes();
        if (_originalBytes.AsSpan().SequenceEqual(bytes))
            return new DarktideLoadOrderSaveResult(null, false);

        if (File.Exists(FilePath) && File.GetAttributes(FilePath).HasFlag(FileAttributes.ReadOnly))
            throw new UnauthorizedAccessException("mod_load_order.txt 为只读文件。");

        string? backupPath = null;
        if (File.Exists(FilePath))
        {
            backupPath = CreateBackup(backupDirectory);
            if (backupPath == null) throw new IOException("无法创建加载顺序备份。");
        }

        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".mod_load_order.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            WriteAtomically(temporary, FilePath);
            _originalBytes = bytes;
        }
        finally
        {
            TryDelete(temporary);
        }

        PruneBackups(backupDirectory);
        return new DarktideLoadOrderSaveResult(backupPath, true);
    }

    private static void WriteAtomically(string temporary, string target)
    {
        if (!File.Exists(target))
        {
            File.Move(temporary, target);
            return;
        }
        try
        {
            File.Replace(temporary, target, null);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
        {
            File.Move(temporary, target, overwrite: true);
        }
    }

    private string? CreateBackup(string? backupDirectory)
    {
        try
        {
            var directory = backupDirectory ?? Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            var name = Path.GetFileName(FilePath);
            var backup = Path.Combine(directory,
                $"{name}.bak.{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
            File.Copy(FilePath, backup, overwrite: false);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void PruneBackups(string? backupDirectory)
    {
        try
        {
            var directory = backupDirectory ?? Path.GetDirectoryName(FilePath)!;
            var name = Path.GetFileName(FilePath) + ".bak.";
            foreach (var file in Directory.EnumerateFiles(directory, name + "*")
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(10))
                TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private byte[] RenderBytes()
    {
        var text = string.Concat(_lines.Select(line => line.Text + line.NewLine));
        var body = Encoding.GetBytes(text);
        if (!HasBom) return body;
        var preamble = Encoding.GetPreamble();
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private void EnsureTrailingNewline()
    {
        if (_lines.Count == 0) return;
        var last = _lines[^1];
        if (last.NewLine.Length == 0) last.NewLine = NewLine;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\r') || name.Contains('\n'))
            throw new ArgumentException("模组名称无效。", nameof(name));
    }

    private static (Encoding Encoding, bool HasBom, int Offset) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Utf8Bom, true, 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, true, 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, true, 2);
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return (Utf8NoBom, false, 0);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("mod_load_order.txt 不是受支持的 UTF-8/UTF-16 文本；未写入任何内容。");
        }
    }

    private static List<DarktideLoadOrderLine> SplitLines(string text, out string dominantNewLine)
    {
        var lines = new List<DarktideLoadOrderLine>();
        var newLineCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            while (index < text.Length && text[index] is not '\r' and not '\n') index++;
            var content = text[start..index];
            var newLine = "";
            if (index < text.Length)
            {
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    newLine = "\r\n";
                    index += 2;
                }
                else
                {
                    newLine = text[index].ToString();
                    index++;
                }
                newLineCounts[newLine] = newLineCounts.GetValueOrDefault(newLine) + 1;
            }
            lines.Add(new DarktideLoadOrderLine(content, newLine, Classify(content)));
        }
        dominantNewLine = newLineCounts.Count == 0
            ? (text.Contains('\r', StringComparison.Ordinal) ? "\r\n" : "\n")
            : newLineCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key.Length).First().Key;
        return lines;
    }

    private static DarktideLoadOrderLineKind Classify(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return DarktideLoadOrderLineKind.Blank;
        return trimmed.StartsWith("--", StringComparison.Ordinal)
            ? DarktideLoadOrderLineKind.Comment
            : DarktideLoadOrderLineKind.Active;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
