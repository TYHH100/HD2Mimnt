using System.Text;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Archive;
using HD2Mimnt.Infrastructure.ModelConversion;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 把 HD2CL 转换产物（文件夹）打成可独立分发的 7z，写到用户选择的目录。
/// 只导出压缩包，不导入模组库、不改配置；先写临时包再改名，失败或取消不会留下半成品。
/// </summary>
internal static class Hd2ClArchiveExport
{
    /// <summary>导出包相对源 HD2IAS 模组的可见标记，与游戏内登记名风格一致。</summary>
    internal const string NameMarker = " ▲";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>给导出名追加 ▲，已是 ▲ 结尾时不重复添加。</summary>
    internal static string WithHd2ClMarker(string name)
    {
        var trimmed = (name ?? string.Empty).TrimEnd();
        if (trimmed.Length == 0) return "HD2CL" + NameMarker;
        return trimmed.EndsWith("▲", StringComparison.Ordinal) ? trimmed : trimmed + NameMarker;
    }

    public static async Task<string> WriteAsync(string outputDirectory, string productName,
        string artifactDirectory, IProgress<string>? progress, CancellationToken token)
    {
        if (!Directory.Exists(artifactDirectory))
            throw new InvalidOperationException("没有生成可导出的成品目录。");
        Directory.CreateDirectory(outputDirectory);
        var destination = ReservePath(outputDirectory, productName);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        progress?.Report("正在打包压缩包…");
        try
        {
            // 包内清单名与文件名保持一致：源 IAS 与 HD2CL 成品同名时靠 ▲ 区分。
            await StampManifestNameAsync(artifactDirectory, productName, token);
            await SevenZipArchiveWriter.CreateFromDirectoryAsync(temporary, artifactDirectory,
                ModArchiveCompression.Normal, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 把导出包内 manifest.json 的 Name 改写成 productName（已带 ▲），其余字节原样保留。
    /// 只替换顶层 Name 的值字面量，避免整体重序列化改动清单格式或转义风格。
    /// </summary>
    internal static async Task StampManifestNameAsync(string artifactDirectory, string name, CancellationToken token)
    {
        var manifestPath = Path.Combine(artifactDirectory, "manifest.json");
        if (!File.Exists(manifestPath)) return;
        var json = await File.ReadAllTextAsync(manifestPath, token);
        var updated = ReplaceNameValue(json, name);
        if (updated is null) return;
        await File.WriteAllTextAsync(manifestPath, updated, new UTF8Encoding(false), token);
    }

    /// <summary>返回把顶层 Name 的值替换为 name 后的 JSON；找不到合法字符串值时不改动（返回 null）。</summary>
    private static string? ReplaceNameValue(string json, string name)
    {
        const string key = "\"Name\"";
        var keyIndex = json.IndexOf(key, StringComparison.Ordinal);
        if (keyIndex < 0) return null;
        var colon = keyIndex + key.Length;
        while (colon < json.Length && char.IsWhiteSpace(json[colon])) colon++;
        if (colon >= json.Length || json[colon] != ':') return null;
        var valueStart = colon + 1;
        while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
        if (valueStart >= json.Length || json[valueStart] != '"') return null;

        var cursor = valueStart + 1;
        while (cursor < json.Length)
        {
            if (json[cursor] == '\\') { cursor += 2; continue; }
            if (json[cursor] == '"')
            {
                var literal = IasLua.QuoteJson(name, ensureAscii: false);
                return string.Concat(json.AsSpan(0, valueStart), literal, json.AsSpan(cursor + 1));
            }
            cursor++;
        }
        return null;
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var replaced = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        var trimmed = replaced.Trim().TrimEnd('.', ' ');
        if (trimmed.Length == 0) return "HD2CL";
        return ReservedNames.Contains(trimmed) ? "_" + trimmed : trimmed;
    }

    /// <summary>同名压缩包已存在时追加序号，避免静默覆盖用户已导出的文件。</summary>
    internal static string ReservePath(string directory, string productName)
    {
        var name = SanitizeFileName(productName);
        var path = Path.Combine(directory, name + ".7z");
        for (var index = 2; File.Exists(path); index++)
            path = Path.Combine(directory, $"{name} ({index}).7z");
        return path;
    }
}
