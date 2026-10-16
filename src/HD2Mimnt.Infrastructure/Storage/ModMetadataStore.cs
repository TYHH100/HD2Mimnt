using System.Text.Json;
using HD2Mimnt.Core.Entities;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Storage;

public sealed class ModMetadataStore(string libraryPath, ILogger? logger = null)
{
    private sealed record Document(int SchemaVersion, Guid ModId, ModPlatformLink Source);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private string GetPath(Guid id) => Path.Combine(libraryPath, ".hd2mimnt-metadata", id.ToString("N") + ".json");

    public async Task<ModPlatformLink?> ReadAsync(Guid id, CancellationToken token = default)
    {
        try
        {
            await using var stream = new FileStream(GetPath(id), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            var document = await JsonSerializer.DeserializeAsync<Document>(stream, cancellationToken: token);
            if (document is { SchemaVersion: 1 } && document.ModId == id) return document.Source;
            logger?.LogWarning("模组元数据版本或编号不匹配: {ModId}", id);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogWarning(exception, "读取模组元数据失败，使用数据库兼容记录: {ModId}", id);
        }
        return null;
    }

    public async Task WriteAsync(Guid id, ModPlatformLink? source, CancellationToken token = default)
    {
        var path = GetPath(id);
        if (source == null)
        {
            if (File.Exists(path)) File.Delete(path);
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(1, id, source), Options, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Apply(Mod mod, ModPlatformLink source)
    {
        mod.PlatformLink = source;
        mod.PlatformId = source.SupportsUpdates ? source.PlatformId : null;
        mod.PlatformModId = source.SupportsUpdates ? source.ModId : null;
        mod.PlatformVersion = source.LatestVersion;
        mod.NexusModId = source.PlatformId == "nexusmods" && int.TryParse(source.ModId, out var nexusId) ? nexusId : null;
    }
}
