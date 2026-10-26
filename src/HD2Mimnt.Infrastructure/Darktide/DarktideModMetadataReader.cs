using System.Text.Json;
using HD2Mimnt.Core.Darktide;

namespace HD2Mimnt.Infrastructure.Darktide;

public sealed class DarktideModMetadataReader
{
    private const long MaxInfoJsonBytes = 1024 * 1024;

    public DarktideInstalledMod? ReadInstalledMod(string folderPath)
    {
        var folderName = Path.GetFileName(folderPath);
        if (!DarktidePaths.IsSafeModFolderName(folderName)) return null;
        if (DarktidePaths.IsStagingOrBackupFolder(folderName)) return null;

        var expectedModFile = Path.Combine(folderPath, folderName + ".mod");
        var metadata = ReadMetadata(folderPath, folderName, expectedModFile);
        return new DarktideInstalledMod(folderName, folderName, folderPath, metadata,
            File.Exists(expectedModFile), DarktidePaths.IsFrameworkFolder(folderName));
    }

    public (DarktideModMetadata Metadata, bool FolderNameMatches) ReadStagedMod(string stagedRoot, string internalName)
    {
        var modFile = Path.Combine(stagedRoot, internalName + ".mod");
        return (ReadMetadata(stagedRoot, internalName, modFile), File.Exists(modFile));
    }

    private static DarktideModMetadata ReadMetadata(string folderPath, string internalName, string modFilePath)
    {
        var modFileExists = File.Exists(modFilePath);
        var hasModFile = modFileExists && new FileInfo(modFilePath).Length > 0;
        var infoPath = Path.Combine(folderPath, "info.json");
        var hasInfo = File.Exists(infoPath);
        string? error = null;
        var name = internalName;
        var version = "";
        var author = "";
        var description = "";
        string? homepage = null;
        string? source = null;
        var required = new List<string>();
        var loadAfter = new List<string>();
        var loadBefore = new List<string>();

        if (hasInfo)
        {
            try
            {
                if (new FileInfo(infoPath).Length > MaxInfoJsonBytes)
                    throw new InvalidDataException("info.json 超过 1MB，已忽略。");
                using var document = JsonDocument.Parse(File.ReadAllText(infoPath));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("info.json 根节点不是对象。");
                name = ReadString(root, "name") ?? internalName;
                version = ReadString(root, "version") ?? "";
                author = ReadString(root, "author") ?? "";
                description = ReadString(root, "description") ?? "";
                homepage = ReadString(root, "homepage");
                source = ReadString(root, "source");
                if (TryGetProperty(root, "dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                {
                    required.AddRange(ReadStringArray(dependencies, "required"));
                    loadAfter.AddRange(ReadStringArray(dependencies, "self_after"));
                    loadBefore.AddRange(ReadStringArray(dependencies, "self_before"));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                error = $"info.json 无法解析：{ex.Message}";
            }
        }

        return new DarktideModMetadata(internalName, name, version, author, description, homepage, source,
            Clean(required), Clean(loadAfter), Clean(loadBefore), hasInfo, hasModFile, HasScripts(folderPath), error);
    }

    private static bool HasScripts(string folderPath)
    {
        var scripts = Path.Combine(folderPath, "scripts");
        try
        {
            return Directory.Exists(scripts) && Directory.EnumerateFiles(scripts, "*.lua", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> Clean(IEnumerable<string> values)
        => values.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static IEnumerable<string> ReadStringArray(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                yield return text;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }
}
