using HD2Mimnt.Core.IO;
using System.Text.Json;
using System.Security.Cryptography;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Migration;

namespace HD2Mimnt.Infrastructure.Migration;

internal static class MigrationJson
{
    internal static async Task<JsonDocument> ReadAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }, token).ConfigureAwait(false);
    }

    internal static JsonElement Get(this JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    internal static string Text(this JsonElement value, string fallback = "")
        => value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    internal static bool Bool(this JsonElement value, bool fallback = false)
        => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    internal static int Int(this JsonElement value, int fallback = 0)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : fallback;
    internal static IEnumerable<JsonElement> Items(this JsonElement value)
        => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    internal static List<string> Strings(this JsonElement value) => value.Items().Select(item => item.Text()).ToList();

    internal static string Color(string value)
    {
        value = value.Trim();
        if (value.Length == 9 && value[0] == '#') value = "#" + value[3..];
        return value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit)
            ? value.ToUpperInvariant() : string.Empty;
    }

    internal static string Root(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
    internal static string DataDirectory(string path) => Root(File.Exists(path) ? Path.GetDirectoryName(path)! : path);
    internal static string Inside(string root, string relative)
    {
        var normalizedRoot = Root(root);
        var full = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) && !PathGuard.IsInsideRoot(normalizedRoot, full))
            throw new InvalidDataException($"相对路径越出模组目录：{relative}");
        return full;
    }

    internal static void CheckSource(ExternalMigrationPlan plan)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in plan.Mods)
        {
            if (!ids.Add(mod.SourceId)) throw new InvalidDataException($"源模组标识重复：{mod.SourceId}");
            if (!Directory.Exists(mod.SourcePath))
                plan.Issues.Add(new(mod.Manifest.Name, $"源模组目录不存在：{mod.SourcePath}"));
            else foreach (var file in mod.FileLayout)
                if (!File.Exists(Inside(mod.SourcePath, file.SourceRelativePath)))
                    plan.Issues.Add(new(mod.Manifest.Name, $"源资源集合文件不存在：{file.SourceRelativePath}"));
        }
        if (plan.Profiles.Select(profile => profile.SourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Profiles.Count)
            throw new InvalidDataException("源配置标识重复。");
        foreach (var profile in plan.Profiles)
            foreach (var member in profile.Mods.Where(member => !ids.Contains(member.SourceModId)))
                plan.Issues.Add(new(profile.Name, $"配置引用的模组不在源库中：{member.SourceModId}"));
    }

    internal static ModManifest Manifest(JsonElement mod, string nameKey, string idKey)
    {
        var sourceId = mod.Get(idKey).Text();
        Guid.TryParse(sourceId, out var guid);
        if (guid == Guid.Empty) guid = StableGuid(sourceId);
        return new ModManifest
        {
            Guid = guid, Name = mod.Get(nameKey).Text("未命名模组"),
            Description = mod.Get("description").Text(), Version = mod.Get("version").Text("1.0.0"),
            Author = mod.Get("author").Text("Unknown"), ManifestVersion = ManifestVersion.V1
        };
    }
}
