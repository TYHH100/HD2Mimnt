using System.Text.Json;
using System.Text.Json.Nodes;

namespace HD2Mimnt.Core.Services;

/// <summary>标准清单与 HD2Mimnt 扩展分开存储；合并仅用于应用内读取。</summary>
public static class ManifestPackage
{
    public const string MetadataFileName = "hd2mimnt.json";

    /// <summary>扩展元数据中标记“档案由导入生成”的字段；内容哈希与判重据此排除该档案。</summary>
    public const string GeneratedManifestExtension = "GeneratedManifest";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonDocumentOptions ReadOptions = new()
    { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly HashSet<string> RootFields = new(
        ["Version", "Guid", "Name", "Description", "IconPath", "Options", "NexusData"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> OptionFields = new(
        ["Name", "Description", "Include", "Image", "SubOptions"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SubOptionFields = new(
        ["Name", "Description", "Include", "Image"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> NexusFields = new(["ModId", "Version"], StringComparer.OrdinalIgnoreCase);

    public static JsonObject Parse(string json) => JsonNode.Parse(json, documentOptions: ReadOptions) as JsonObject
        ?? throw new InvalidDataException("Manifest must be a JSON object.");
    private static JsonNode? Get(JsonObject node, string name)
        => node.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static string MetadataPath(string path) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, MetadataFileName);

    public static (JsonObject Manifest, JsonObject Metadata) Separate(JsonObject combined)
    {
        var manifest = (JsonObject)combined.DeepClone();
        var extensions = Extract(manifest, RootFields);
        return (manifest, new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["ManifestGuid"] = Get(manifest, "Guid")?.DeepClone(),
            ["Extensions"] = extensions
        });
    }

    public static JsonObject PreserveUnknownExtensions(JsonObject current, JsonObject original)
    {
        var previous = (JsonObject)original.DeepClone();
        Remove(previous, "Author", "AuthorUrl", "ModVersion", "Category", "Tags");
        if (Get(previous, "Options") is JsonArray options)
            foreach (var option in options.OfType<JsonObject>()) Remove(option, "DefaultEnabled");
        var metadata = Separate(previous).Metadata;
        metadata["ManifestGuid"] = Get(current, "Guid")?.DeepClone();
        return Merge(current, metadata);
    }

    private static void Remove(JsonObject node, params string[] names)
    {
        foreach (var key in node.Select(pair => pair.Key).Where(key => names.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray())
            node.Remove(key);
    }

    private static JsonObject Extract(JsonObject node, HashSet<string> fields)
    {
        var extensions = new JsonObject();
        foreach (var pair in node.ToArray())
        {
            if (!fields.Contains(pair.Key))
            {
                extensions[pair.Key] = pair.Value?.DeepClone();
                node.Remove(pair.Key);
            }
            else if (pair.Value is JsonArray children &&
                (pair.Key.Equals("Options", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("SubOptions", StringComparison.OrdinalIgnoreCase)))
            {
                var childFields = ReferenceEquals(fields, RootFields) ? OptionFields : SubOptionFields;
                var childExtensions = new JsonArray();
                foreach (var child in children)
                {
                    if (child is not JsonObject item) { childExtensions.Add(null); continue; }
                    var extra = Extract(item, childFields);
                    // 名称只用于校验对应项，合并时不覆盖标准字段。
                    extra["Name"] = Get(item, "Name")?.DeepClone();
                    childExtensions.Add(extra);
                }
                extensions[pair.Key] = childExtensions;
            }
            else if (pair.Key.Equals("NexusData", StringComparison.OrdinalIgnoreCase) && pair.Value is JsonObject nexus)
                extensions[pair.Key] = Extract(nexus, NexusFields);
            if (fields.Contains(pair.Key))
            {
                var canonical = fields.First(key => key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                if (canonical != pair.Key)
                {
                    node.Remove(pair.Key);
                    if (!node.ContainsKey(canonical)) node[canonical] = pair.Value;
                }
            }
        }
        return extensions;
    }

    public static JsonObject Merge(JsonObject manifest, JsonObject metadata)
    {
        if (Get(metadata, "SchemaVersion") is not JsonValue schema || !schema.TryGetValue<int>(out var version) || version != 1
            || !Guid.TryParse(Get(manifest, "Guid")?.ToString(), out var manifestGuid)
            || !Guid.TryParse(Get(metadata, "ManifestGuid")?.ToString(), out var metadataGuid) || manifestGuid != metadataGuid
            || Get(metadata, "Extensions") is not JsonObject extensions)
            throw new InvalidDataException("HD2Mimnt metadata does not match the manifest or uses an unsupported schema.");
        var combined = (JsonObject)manifest.DeepClone();
        Apply(combined, extensions, RootFields);
        return combined;
    }

    private static void Apply(JsonObject node, JsonObject extensions, HashSet<string> fields)
    {
        foreach (var pair in extensions)
        {
            if (!fields.Contains(pair.Key))
            {
                foreach (var key in node.Select(p => p.Key).Where(key => key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).ToArray())
                    node.Remove(key);
                node[pair.Key] = pair.Value?.DeepClone();
            }
            else if (pair.Value is JsonArray extras && Get(node, pair.Key) is JsonArray children)
            {
                var childFields = ReferenceEquals(fields, RootFields) ? OptionFields : SubOptionFields;
                for (var i = 0; i < Math.Min(extras.Count, children.Count); i++)
                    if (extras[i] is JsonObject extra && children[i] is JsonObject child
                        && JsonNode.DeepEquals(Get(extra, "Name"), Get(child, "Name")))
                        Apply(child, extra, childFields);
            }
            else if (pair.Key.Equals("NexusData", StringComparison.OrdinalIgnoreCase)
                && pair.Value is JsonObject nexusExtra && Get(node, pair.Key) is JsonObject nexus)
                Apply(nexus, nexusExtra, NexusFields);
        }
    }

    public static JsonObject Read(string path, Action<Exception>? metadataError = null)
    {
        var manifest = Parse(File.ReadAllText(path));
        var metadataPath = MetadataPath(path);
        if (!File.Exists(metadataPath)) return manifest;
        try { return Merge(manifest, Parse(File.ReadAllText(metadataPath))); }
        catch (Exception error) when (metadataError != null && error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { metadataError(error); return manifest; }
    }

    public static async Task<JsonObject> ReadAsync(string path, CancellationToken token = default, Action<Exception>? metadataError = null)
    {
        var manifest = Parse(await File.ReadAllTextAsync(path, token));
        var metadataPath = MetadataPath(path);
        if (!File.Exists(metadataPath)) return manifest;
        try { return Merge(manifest, Parse(await File.ReadAllTextAsync(metadataPath, token))); }
        catch (Exception error) when (metadataError != null && error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { metadataError(error); return manifest; }
    }

    /// <summary>目录中的档案是否由导入时自动生成（标记写入扩展元数据，作者清单不会带该标记）。</summary>
    public static bool IsGeneratedManifest(string modRoot)
    {
        var metadataPath = Path.Combine(modRoot, MetadataFileName);
        if (!File.Exists(metadataPath)) return false;
        try
        {
            var metadata = Parse(File.ReadAllText(metadataPath));
            return Get(metadata, "Extensions") is JsonObject extensions
                && Get(extensions, GeneratedManifestExtension) is JsonValue value
                && value.TryGetValue<bool>(out var generated) && generated;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static void Save(string path, JsonObject combined)
    {
        var (manifest, metadata) = Separate(combined);
        var temporary = Path.GetFullPath(path) + $".{Guid.NewGuid():N}.tmp";
        var metadataTemporary = MetadataPath(path) + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, manifest.ToJsonString(JsonOptions));
            File.WriteAllText(metadataTemporary, metadata.ToJsonString(JsonOptions));
            Publish(path, temporary, metadataTemporary);
        }
        finally { Cleanup(temporary, metadataTemporary); }
    }

    public static async Task SaveAsync(string path, JsonObject combined, CancellationToken token = default)
    {
        var (manifest, metadata) = Separate(combined);
        var temporary = Path.GetFullPath(path) + $".{Guid.NewGuid():N}.tmp";
        var metadataTemporary = MetadataPath(path) + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, manifest.ToJsonString(JsonOptions), token);
            await File.WriteAllTextAsync(metadataTemporary, metadata.ToJsonString(JsonOptions), token);
            token.ThrowIfCancellationRequested();
            Publish(path, temporary, metadataTemporary);
        }
        finally { Cleanup(temporary, metadataTemporary); }
    }

    private static void Publish(string path, string temporary, string metadataTemporary)
    {
        var metadataPath = MetadataPath(path);
        var previousMetadata = File.Exists(metadataPath) ? File.ReadAllBytes(metadataPath) : null;
        File.Move(metadataTemporary, metadataPath, overwrite: true);
        try { File.Move(temporary, path, overwrite: true); }
        catch
        {
            if (previousMetadata == null) File.Delete(metadataPath);
            else
            {
                File.WriteAllBytes(metadataTemporary, previousMetadata);
                File.Move(metadataTemporary, metadataPath, overwrite: true);
            }
            throw;
        }
    }

    private static void Cleanup(params string[] paths)
    { foreach (var path in paths) if (File.Exists(path)) File.Delete(path); }
}
