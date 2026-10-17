using System.Text.Json;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Storage;

/// <summary>版本索引在模组内容目录之外，路径相对模组库保存，随库一起迁移。</summary>
public sealed class ModVersionStore(string libraryPath)
{
    private sealed record Document(int SchemaVersion, Guid ModId, List<LocalModVersion> Versions);
    private string GetPath(Guid id) => Path.Combine(libraryPath, ".hd2mimnt-versions", id.ToString("N") + ".json");

    public async Task<List<LocalModVersion>> ReadAsync(Guid id, CancellationToken token = default)
    {
        var path = GetPath(id);
        if (!File.Exists(path)) return new();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var document = await JsonSerializer.DeserializeAsync<Document>(stream, cancellationToken: token);
        if (document is not { SchemaVersion: 1 } || document.ModId != id)
            throw new InvalidDataException("本地版本索引无效。");
        foreach (var entry in document.Versions)
        {
            entry.Content.Path = Resolve(entry.Content.Path);
            if (entry.Content.ThumbnailPath.Length > 0) entry.Content.ThumbnailPath = Resolve(entry.Content.ThumbnailPath);
        }
        return document.Versions;
    }

    public async Task WriteAsync(Guid id, IReadOnlyList<LocalModVersion> versions, CancellationToken token = default)
    {
        var path = GetPath(id);
        if (versions.Count == 0)
        {
            // 从未保留版本，或最后一份索引已清理时，父目录可能不存在。
            try { File.Delete(path); }
            catch (DirectoryNotFoundException) { return; }
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            return;
        }
        var copies = versions.Select(entry => entry with { Content = Clone(entry.Content) }).ToList();
        foreach (var entry in copies)
        {
            entry.Content.Path = Path.GetRelativePath(libraryPath, entry.Content.Path);
            if (entry.Content.ThumbnailPath.Length > 0)
                entry.Content.ThumbnailPath = Path.GetRelativePath(libraryPath, entry.Content.ThumbnailPath);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(1, id, copies), cancellationToken: token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static Mod Clone(Mod mod) => JsonSerializer.Deserialize<Mod>(JsonSerializer.Serialize(mod))!;

    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(relative, Path.GetFullPath(libraryPath));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryPath)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("版本文件不在模组库中。");
        return full;
    }
}
