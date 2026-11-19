using System.Text.Json;
using HD2Mimnt.Core.Entities;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Storage;

/// <summary>库根 .hd2mimnt-metadata 档案内容：来源关联与用户标签，均不修改模组包本身。</summary>
public sealed record ModMetadataDocument(Guid ModId, ModPlatformLink? Source, IReadOnlyList<string>? UserTags);

public sealed class ModMetadataStore(string libraryPath, ILogger? logger = null)
{
    private sealed record Document(int SchemaVersion, Guid ModId, ModPlatformLink? Source, List<string>? UserTags);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private string GetPath(Guid id) => Path.Combine(libraryPath, ".hd2mimnt-metadata", id.ToString("N") + ".json");

    /// <summary>读取完整档案。UserTags 为 null 表示旧版档案没有该字段，调用方应保留数据库记录。</summary>
    public async Task<ModMetadataDocument?> ReadDocumentAsync(Guid id, CancellationToken token = default)
    {
        try
        {
            await using var stream = new FileStream(GetPath(id), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            var document = await JsonSerializer.DeserializeAsync<Document>(stream, cancellationToken: token);
            if (document is { SchemaVersion: 1 or 2 } && document.ModId == id)
                return new(id, document.Source, document.SchemaVersion >= 2 ? document.UserTags ?? [] : null);
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

    public async Task<ModPlatformLink?> ReadAsync(Guid id, CancellationToken token = default)
        => (await ReadDocumentAsync(id, token))?.Source;

    /// <summary>只更新来源，保留档案中已有的用户标签。</summary>
    public Task WriteAsync(Guid id, ModPlatformLink? source, CancellationToken token = default)
        => WriteAsync(id, source, userTags: null, token);

    /// <param name="userTags">null 表示保留档案中的已有标签；空集合表示清空。</param>
    public async Task WriteAsync(Guid id, ModPlatformLink? source, IReadOnlyList<string>? userTags, CancellationToken token)
    {
        var tags = userTags ?? (await ReadDocumentAsync(id, token))?.UserTags ?? [];
        var path = GetPath(id);
        if (source == null && tags.Count == 0)
        {
            DeleteFile(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(2, id, source, tags.ToList()), Options, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task DeleteAsync(Guid id) => WriteAsync(id, null, Array.Empty<string>(), CancellationToken.None);

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
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
