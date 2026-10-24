using HD2Mimnt.Core.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Archive;

namespace HD2Mimnt.Application.Services;

/// <summary>清单编辑与保存。扩展字段存入独立 JSON，文件操作集中于 Application 层。</summary>
public sealed class ModCreationService(StorageSettingsService? storage = null)
{
    private readonly ManifestParser _parser = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public ModCreationDocument Create() => new(new ModManifest
    {
        Guid = Guid.NewGuid(), ManifestVersion = ManifestVersion.V1, Version = "1.0.0", Options = []
    });

    public async Task<byte[]?> ReadPreviewImageAsync(string root, string? relativePath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var directory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(directory, relativePath));
            if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            return await File.ReadAllBytesAsync(path, token);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public async Task SavePreviewImageAsync(string path, byte[] png, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, png, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task<ModCreationDirectory> ReadDirectoryAsync(string path, CancellationToken token = default)
        => Task.Run(() => ReadDirectory(new DirectoryInfo(Path.GetFullPath(path)), ".", token), token);

    private static ModCreationDirectory ReadDirectory(DirectoryInfo directory, string relative, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? image = null;
        var hasPatches = false;
        foreach (var file in directory.EnumerateFiles())
        {
            token.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (image == null && file.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                image = relative == "." ? file.Name : relative + "/" + file.Name;
            if (file.Name.Contains(".patch_", StringComparison.OrdinalIgnoreCase)) hasPatches = true;
        }
        var children = new List<ModCreationDirectory>();
        foreach (var child in directory.EnumerateDirectories())
        {
            token.ThrowIfCancellationRequested();
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            children.Add(ReadDirectory(child, relative == "." ? child.Name : relative + "/" + child.Name, token));
        }
        return new(directory.Name, relative, image, hasPatches, children);
    }

    public List<ModOption> CreateDirectoryOptions(ModCreationDirectory root, string baseFilesName)
    {
        var options = new List<ModOption>();
        if (root.HasPatches)
            options.Add(new() { Name = baseFilesName, Include = ["."], Image = root.Image, DefaultEnabled = true });
        foreach (var directory in root.Children)
        {
            if (directory.Name.Equals("Models", StringComparison.OrdinalIgnoreCase)
                || directory.Name.Equals("Model", StringComparison.OrdinalIgnoreCase))
                options.AddRange(directory.Children.Select(CreateDirectoryOption));
            else options.Add(CreateDirectoryOption(directory));
        }
        return options;
    }

    private static ModOption CreateDirectoryOption(ModCreationDirectory directory) => new()
    {
        Name = directory.Name, Image = directory.Image, DefaultEnabled = true,
        Include = [directory.RelativePath],
        SubOptions = directory.Children.Count == 0 ? null : directory.Children.Select(child => new ModSubOption
        { Name = child.Name, Image = child.Image, Include = [child.RelativePath] }).ToList()
    };

    public async Task<ModCreationDocument> LoadAsync(string path, CancellationToken token = default)
    {
        var root = await ManifestPackage.ReadAsync(path, token);
        var json = root.ToJsonString();
        var manifest = _parser.ParseFromJson(json) ?? throw new InvalidDataException("Creator.InvalidManifest");
        var document = new ModCreationDocument(manifest) { SourceDirectory = Path.GetDirectoryName(Path.GetFullPath(path))! };
        document.Original[manifest] = root;
        if (Get(root, "Options") is JsonArray options)
        {
            var originals = options.OfType<JsonObject>().ToList();
            for (var i = 0; i < (manifest.Options?.Count ?? 0); i++)
            {
                var option = manifest.Options![i];
                document.Original[option] = originals[i];
                if (Get(originals[i], "SubOptions") is not JsonArray children) continue;
                var subOriginals = children.OfType<JsonObject>().ToList();
                for (var j = 0; j < (option.SubOptions?.Count ?? 0); j++)
                    document.Original[option.SubOptions![j]] = subOriginals[j];
            }
        }
        return document;
    }

    public string Preview(ModCreationDocument document)
        => ManifestPackage.Separate(BuildCombined(document)).Manifest.ToJsonString(JsonOptions);

    public string PreviewMetadata(ModCreationDocument document)
        => ManifestPackage.Separate(BuildCombined(document)).Metadata.ToJsonString(JsonOptions);

    private static JsonObject BuildCombined(ModCreationDocument document)
    {
        var manifest = document.Manifest;
        var root = Original(document, manifest);
        Set(root, "Version", manifest.ManifestVersion == ManifestVersion.V1 ? JsonValue.Create(1) : null);
        Set(root, "Guid", JsonValue.Create(manifest.Guid.ToString()));
        Set(root, "Name", JsonValue.Create(manifest.Name));
        Set(root, "Author", JsonValue.Create(manifest.Author));
        Set(root, "AuthorUrl", Text(AuthorWebsite.Normalize(manifest.AuthorUrl) ?? manifest.AuthorUrl));
        Set(root, "ModVersion", JsonValue.Create(manifest.Version));
        Set(root, "Description", JsonValue.Create(manifest.Description));
        Set(root, "IconPath", Text(manifest.IconPath));
        Set(root, "Options", manifest.ManifestVersion == ManifestVersion.Legacy
            ? manifest.LegacyOptions?.Count > 0
                ? new JsonArray(manifest.LegacyOptions.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()) : null
            : manifest.Options?.Count > 0
                ? new JsonArray(manifest.Options.Select(option => (JsonNode)WriteOption(document, option)).ToArray()) : null);
        return root;
    }

    private static JsonObject WriteOption(ModCreationDocument document, ModOption option)
    {
        var node = Original(document, option);
        WriteFields(node, option.Name, option.Description, option.Image, option.Include);
        Set(node, "DefaultEnabled", option.DefaultEnabled.HasValue ? JsonValue.Create(option.DefaultEnabled.Value) : null);
        Set(node, "SubOptions", option.SubOptions?.Count > 0
            ? new JsonArray(option.SubOptions.Select(sub =>
            {
                var child = Original(document, sub);
                WriteFields(child, sub.Name, sub.Description, sub.Image, sub.Include);
                return (JsonNode)child;
            }).ToArray()) : null);
        return node;
    }

    private static void WriteFields(JsonObject node, string name, string description, string? image, List<string>? include)
    {
        Set(node, "Name", JsonValue.Create(name)); Set(node, "Description", JsonValue.Create(description));
        Set(node, "Image", Text(image));
        Set(node, "Include", include == null ? null : new JsonArray(include.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()));
    }

    public void Validate(ModCreationDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Manifest.Name)) throw new InvalidDataException("Creator.NameRequired");
        if (!string.IsNullOrWhiteSpace(document.Manifest.AuthorUrl) && AuthorWebsite.Normalize(document.Manifest.AuthorUrl) == null)
            throw new InvalidDataException("Creator.InvalidAuthorUrl");
        if (!Directory.Exists(document.SourceDirectory)) throw new InvalidDataException("Creator.DirectoryRequired");
        ValidateImage(document.SourceDirectory, document.Manifest.IconPath);
        if (document.Manifest.ManifestVersion == ManifestVersion.Legacy)
        {
            ValidatePaths(document.SourceDirectory, document.Manifest.LegacyOptions);
            return;
        }
        foreach (var option in document.Manifest.Options ?? [])
        {
            if (string.IsNullOrWhiteSpace(option.Name)) throw new InvalidDataException("Creator.OptionNameRequired");
            ValidatePaths(document.SourceDirectory, option.Include);
            ValidateImage(document.SourceDirectory, option.Image);
            foreach (var sub in option.SubOptions ?? [])
            {
                if (string.IsNullOrWhiteSpace(sub.Name)) throw new InvalidDataException("Creator.OptionNameRequired");
                ValidatePaths(document.SourceDirectory, sub.Include); ValidateImage(document.SourceDirectory, sub.Image);
            }
        }
    }

    private static void ValidatePaths(string root, List<string>? paths)
    {
        foreach (var path in paths ?? [])
            if (!Directory.Exists(ResolvePath(root, path))) throw new DirectoryNotFoundException(path);
    }

    private static void ValidateImage(string root, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(ResolvePath(root, path))) throw new FileNotFoundException(path);
    }

    public static string ResolvePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Creator.RelativePathRequired");
        var directory = Path.GetFullPath(root);
        var full = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), directory);
        if (!string.Equals(full, directory, StringComparison.OrdinalIgnoreCase) && !PathGuard.IsInsideRoot(directory, full))
            throw new InvalidDataException("Creator.RelativePathRequired");
        return full;
    }

    public string GetRelativePath(string root, string selected)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidDataException("Creator.DirectoryRequired");
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(selected)).Replace('\\', '/');
        ResolvePath(root, relative);
        return relative;
    }

    public async Task SaveAsync(ModCreationDocument document, string path, CancellationToken token = default)
    {
        Validate(document);
        var full = Path.GetFullPath(path);
        if (!Path.GetFileName(full).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(full)!),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(document.SourceDirectory)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Creator.SaveInSource");
        await ManifestPackage.SaveAsync(full, BuildCombined(document), token);
    }

    public Task ExportArchiveAsync(ModCreationDocument document, string path, CancellationToken token = default)
        => ExportArchiveAsync(document, path, ModArchiveCompression.Ultra, token);

    public Task ExportArchiveAsync(ModCreationDocument document, string path, ModArchiveCompression compression, CancellationToken token = default)
        => ExportArchiveAsync(document, path, compression, null, token);

    public Task ExportArchiveAsync(ModCreationDocument document, string path, ModArchiveCompression compression,
        string? password, CancellationToken token = default)
    {
        if (password?.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new InvalidDataException("ArchivePasswords.InvalidPassword");
        // UI 在启动操作前冻结编辑；先生成独立快照，打包期间不再读取可变表单。
        var package = ManifestPackage.Separate(BuildCombined(document));
        var json = package.Manifest.ToJsonString(JsonOptions);
        var metadataJson = package.Metadata.ToJsonString(JsonOptions);
        var source = document.SourceDirectory;
        return Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            Validate(document);
            if (!Enum.IsDefined(compression)) throw new ArgumentOutOfRangeException(nameof(compression));
            var destination = Path.GetFullPath(path);
            if (PathGuard.IsInsideRoot(source, destination)) throw new InvalidDataException("Creator.ArchiveOutsideSource");
            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var format = Path.GetExtension(destination).TrimStart('.').ToLowerInvariant();
                if (format is not ("7z" or "zip")) throw new InvalidDataException("Creator.ArchiveFormatRequired");
                // 7-Zip 的 ZIP 加密只接受 ASCII 密码，Unicode 密码使用 7z。
                if (format == "zip" && password?.Any(c => c > 127) == true)
                    throw new InvalidDataException("ArchivePasswords.ZipAsciiRequired");
                if (format == "7z" || !string.IsNullOrEmpty(password))
                {
                    await ExportSevenZipAsync(source, temporary, json, metadataJson, compression, token, password, format);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, overwrite: true);
                    return;
                }
                if (!Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Creator.ArchiveFormatRequired");
                var zipCompression = compression switch
                {
                    ModArchiveCompression.Store => CompressionLevel.NoCompression,
                    ModArchiveCompression.Fast => CompressionLevel.Fastest,
                    ModArchiveCompression.Normal => CompressionLevel.Optimal,
                    _ => CompressionLevel.SmallestSize
                };
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                    var manifestEntry = archive.CreateEntry("manifest.json", zipCompression);
                    await using (var entryStream = manifestEntry.Open())
                    await using (var writer = new StreamWriter(entryStream))
                        await writer.WriteAsync(json.AsMemory(), token);
                    var metadataEntry = archive.CreateEntry(ManifestPackage.MetadataFileName, zipCompression);
                    await using (var entryStream = metadataEntry.Open())
                    await using (var writer = new StreamWriter(entryStream))
                        await writer.WriteAsync(metadataJson.AsMemory(), token);
                    var pending = new Stack<string>();
                    pending.Push(Path.GetFullPath(source));
                    while (pending.TryPop(out var directory))
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                        {
                            token.ThrowIfCancellationRequested();
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Creator.LinkedFilesUnsupported");
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                archive.CreateEntry(Path.GetRelativePath(source, entry).Replace('\\', '/') + "/");
                                pending.Push(entry); continue;
                            }
                            var relative = Path.GetRelativePath(source, entry).Replace('\\', '/');
                            if (relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                                || relative.Equals(ManifestPackage.MetadataFileName, StringComparison.OrdinalIgnoreCase)) continue;
                            var zipEntry = archive.CreateEntry(relative, zipCompression);
                            await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                            await using var output = zipEntry.Open();
                            await input.CopyToAsync(output, token);
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, token);
    }

    private async Task ExportSevenZipAsync(string source, string temporary, string json, string metadataJson,
        ModArchiveCompression compression, CancellationToken token, string? password, string format)
    {
        var inputs = new List<string>();
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(source));
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            var entries = Directory.GetFileSystemEntries(directory);
            if (entries.Length == 0 && directory != Path.GetFullPath(source))
                inputs.Add(Path.GetRelativePath(source, directory));
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Creator.LinkedFilesUnsupported");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                var relative = Path.GetRelativePath(source, entry);
                if (!relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                    && !relative.Equals(ManifestPackage.MetadataFileName, StringComparison.OrdinalIgnoreCase)) inputs.Add(relative);
            }
        }
        var stage = Path.Combine(storage?.Current.TempPath ?? Path.GetTempPath(), "HD2Mimnt-Creation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(stage, "manifest.json"), json, token);
            await File.WriteAllTextAsync(Path.Combine(stage, ManifestPackage.MetadataFileName), metadataJson, token);
            var list = Path.Combine(stage, "files.txt");
            await File.WriteAllLinesAsync(list, inputs, new System.Text.UTF8Encoding(false), token);
            await SevenZipArchiveWriter.CreateAsync(temporary, Path.GetFullPath(source), inputs.Count > 0 ? list : null, stage, compression, token, password, format);
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    private static JsonNode? Get(JsonObject node, string key) => node.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    private static JsonObject Original(ModCreationDocument document, object model) => document.Original.TryGetValue(model, out var value)
        ? (JsonObject)value.DeepClone() : new JsonObject();
    private static JsonValue? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value);
    private static void Set(JsonObject node, string key, JsonNode? value)
    {
        foreach (var existing in node.Select(pair => pair.Key).Where(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray()) node.Remove(existing);
        if (value != null) node[key] = value;
    }
}

public sealed record ModCreationDirectory(string Name, string RelativePath, string? Image, bool HasPatches,
    IReadOnlyList<ModCreationDirectory> Children);

public sealed class ModCreationDocument(ModManifest manifest)
{
    public ModManifest Manifest { get; } = manifest;
    public string SourceDirectory { get; set; } = string.Empty;
    internal Dictionary<object, JsonObject> Original { get; } = new(ReferenceEqualityComparer.Instance);
}
