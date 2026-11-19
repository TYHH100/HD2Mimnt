using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Storage;

/// <summary>库根 .hd2mimnt-metadata/tag-colors.json：用户标签颜色，按标签名全局共享。</summary>
public sealed class TagColorStore(string libraryPath, ILogger? logger = null)
{
    private sealed record Document(int SchemaVersion, Dictionary<string, string>? Colors);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private string GetPath() => Path.Combine(libraryPath, ".hd2mimnt-metadata", "tag-colors.json");

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(CancellationToken token = default)
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var stream = new FileStream(GetPath(), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            var document = await JsonSerializer.DeserializeAsync<Document>(stream, cancellationToken: token);
            if (document is { SchemaVersion: 1 }) 
            {
                foreach (var (tag, color) in document.Colors ?? [])
                    if (!string.IsNullOrWhiteSpace(tag) && !string.IsNullOrWhiteSpace(color))
                        colors[tag] = color;
            }
            else logger?.LogWarning("标签颜色档案版本不匹配");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(exception, "读取标签颜色失败，按无自定义颜色处理");
        }
        return colors;
    }

    public async Task WriteAsync(IReadOnlyDictionary<string, string> colors, CancellationToken token = default)
    {
        var path = GetPath();
        if (colors.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new Document(1, colors.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)),
                    Options, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
