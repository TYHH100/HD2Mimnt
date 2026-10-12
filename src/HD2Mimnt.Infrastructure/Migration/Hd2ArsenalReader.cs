using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Infrastructure.Migration;

public sealed class Hd2ArsenalReader : IExternalManagerReader
{
    public ExternalManagerKind Manager => ExternalManagerKind.HD2Arsenal;

    public async Task<ExternalMigrationPlan> ReadAsync(ExternalMigrationSource source,
        CancellationToken cancellationToken = default)
    {
        var root = MigrationJson.DataDirectory(source.DataPath);
        using var document = await MigrationJson.ReadAsync(Path.Combine(root, "hd2a_data.json"), cancellationToken);
        var data = document.RootElement;
        if (data.Get("modsList").ValueKind != JsonValueKind.Object || data.Get("modsLibrary").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("HD2Arsenal 数据缺少 modsList 配置列表或 modsLibrary 模组库。");
        var plan = new ExternalMigrationPlan { Source = source, SourceIdentity = root };
        var mods = new Dictionary<string, ExternalMigrationMod>(StringComparer.OrdinalIgnoreCase);
        var librarySeparators = new List<ExternalMigrationSeparator>();
        var libraryPosition = 0;
        foreach (var item in data.Get("modsLibrary").Items())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSeparator(item)) librarySeparators.Add(ReadSeparator(item, libraryPosition));
            else { AddMod(item); libraryPosition++; }
        }

        var profiles = data.Get("modsList").EnumerateObject().ToDictionary(item => item.Name, item => item.Value);
        var ordered = data.Get("profileOrder").Strings().Where(profiles.ContainsKey).Distinct().ToList();
        ordered.AddRange(profiles.Keys.Where(id => !ordered.Contains(id)));
        foreach (var id in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = profiles[id];
            var profile = new ExternalMigrationProfile
            {
                SourceId = id, Name = value.Get("label").Text(id),
                WasActive = data.Get("selectedProfile").Text() == id,
                DeploymentOrder = data.Get("setTopPriority").Bool() ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom
            };
            var damaged = false;
            foreach (var item in value.Get("mods").Items())
            {
                if (IsSeparator(item)) profile.Separators.Add(ReadSeparator(item, profile.Mods.Count));
                else
                {
                    var mod = AddMod(item);
                    if (mod == null) { damaged = true; continue; }
                    profile.Mods.Add(new(mod.SourceId, item.Get("enabled").Bool(true), ReadOptions(item, mod.Manifest)));
                }
            }
            SetMemberCounts(profile.Separators, profile.Mods.Count);
            if (!damaged) plan.Profiles.Add(profile);
        }

        // 当前模组库没有用户分组实体；库中独有的分隔符连同库顺序保存在独立组织配置。
        if (librarySeparators.Count > 0)
        {
            var profile = new ExternalMigrationProfile { SourceId = "library-layout", Name = "HD2Arsenal 模组库分组" };
            foreach (var item in data.Get("modsLibrary").Items().Where(item => !IsSeparator(item)))
                if (mods.TryGetValue(item.Get("uuid").Text(), out var mod))
                    profile.Mods.Add(new(mod.SourceId, false, ReadOptions(item, mod.Manifest)));
            SetMemberCounts(librarySeparators, profile.Mods.Count);
            profile.Separators.AddRange(librarySeparators);
            plan.Profiles.Add(profile);
        }
        MigrationJson.CheckSource(plan);
        return plan;

        ExternalMigrationMod? AddMod(JsonElement item)
        {
            var id = item.Get("uuid").Text();
            if (string.IsNullOrWhiteSpace(id))
            {
                plan.Issues.Add(new(item.Get("label").Text(), "源模组缺少 uuid。"));
                return null;
            }
            if (mods.TryGetValue(id, out var existing)) return existing;
            var originalPath = item.Get("path").Text();
            if (string.IsNullOrWhiteSpace(originalPath))
            {
                plan.Issues.Add(new(id, "源模组缺少文件路径。"));
                return null;
            }
            var path = source.ModsPath is { Length: > 0 }
                ? Path.Combine(source.ModsPath, Path.GetFileName(originalPath))
                : Path.IsPathRooted(originalPath) ? originalPath : Path.Combine(root, originalPath);
            var manifest = MigrationJson.Manifest(item, "label", "uuid");
            // 清单保留作者和加载器 GUID；选项以 Arsenal 库中的当前定义为准。
            var onDisk = File.Exists(Path.Combine(path, "manifest.json"))
                ? new ManifestParser().ParseFromFile(Path.Combine(path, "manifest.json")) : null;
            if (onDisk != null)
            {
                using var original = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (Guid.TryParse(original.RootElement.Get("Guid").Text(), out var guid)) manifest.Guid = guid;
                manifest.Author = onDisk.Author;
                manifest.Version = onDisk.Version;
                manifest.NexusData = onDisk.NexusData;
            }
            manifest.Options = item.Get("options").ValueKind == JsonValueKind.Array ? ReadManifestOptions(item.Get("options"), path) : onDisk?.Options;
            if (onDisk?.ManifestVersion == ManifestVersion.Legacy && item.Get("options").ValueKind != JsonValueKind.Array)
            {
                manifest.ManifestVersion = ManifestVersion.Legacy;
                manifest.LegacyOptions = onDisk.LegacyOptions;
            }
            manifest.IconPath = RelativeImage(item.Get("iconPath").Text(), path);
            manifest.Tags = item.Get("tags").Strings();
            var mod = new ExternalMigrationMod
            {
                SourceId = id, SourcePath = path, Manifest = manifest, Tags = manifest.Tags,
                ImagePath = item.Get("iconPath").Text() is { Length: > 0 } image ? Path.GetFullPath(image, path) : null
            };
            mods.Add(id, mod);
            plan.Mods.Add(mod);
            return mod;
        }
    }

    private static List<ModOption> ReadManifestOptions(JsonElement options, string root)
        => options.Items().Select(option => new ModOption
        {
            Name = option.Get("name").Text(), Description = option.Get("description").Text(),
            DefaultEnabled = option.Get("enabled").Bool(true), Include = option.Get("include").ValueKind == JsonValueKind.Array ? option.Get("include").Strings() : null,
            Image = RelativeImage(option.Get("iconPath").Text(), root),
            SubOptions = option.Get("suboptions").Items().Select(sub => new ModSubOption
            {
                Name = sub.Get("name").Text(), Description = sub.Get("description").Text(),
                Include = sub.Get("include").ValueKind == JsonValueKind.Array ? sub.Get("include").Strings() : null, Image = RelativeImage(sub.Get("iconPath").Text(), root)
            }).ToList()
        }).ToList();

    private static ProfileModOptionsState ReadOptions(JsonElement item, ModManifest manifest)
    {
        var options = item.Get("options").Items().ToList();
        if (options.Count == 0) options = item.Get("optionsConfig").Items().ToList();
        if (options.Count == 0) return new();
        if (manifest.ManifestVersion == ManifestVersion.Legacy)
            return new ProfileModOptionsState { LegacySelectedIndex = item.Get("selected").Int() };
        options = (manifest.Options ?? []).Select((definition, index) => options.FirstOrDefault(option =>
            option.Get("name").Text().Equals(definition.Name, StringComparison.OrdinalIgnoreCase)) is var matched
            && matched.ValueKind == JsonValueKind.Object ? matched : index < options.Count ? options[index] : default).ToList();
        return new ProfileModOptionsState
        {
            OptionToggles = options.Select(option => option.Get("enabled").Bool(true)).ToList(),
            OptionSelections = options.Select(option =>
            {
                var subs = option.Get("suboptions").Items().ToList();
                var selected = subs.FindIndex(sub => sub.Get("enabled").Bool());
                return selected >= 0 ? selected : option.Get("selected").Int();
            }).ToList()
        };
    }

    private static bool IsSeparator(JsonElement item)
        => item.Get("type").Text().Equals("separator", StringComparison.OrdinalIgnoreCase) || item.Get("isSeparator").Bool();

    private static ExternalMigrationSeparator ReadSeparator(JsonElement item, int order)
        => new(item.Get("title").Text(item.Get("label").Text(item.Get("name").Text("分隔符"))), order,
            item.Get("collapsed").Bool(), MigrationJson.Color(item.Get("color").Text(item.Get("backgroundColor").Text())));

    private static string? RelativeImage(string image, string root)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        return Path.IsPathRooted(image) ? Path.GetRelativePath(root, image) : image;
    }

    private static void SetMemberCounts(List<ExternalMigrationSeparator> separators, int count)
    {
        for (var index = 0; index < separators.Count; index++)
            separators[index] = separators[index] with
            { MemberCount = Math.Max(0, (index + 1 < separators.Count ? separators[index + 1].Order : count) - separators[index].Order) };
    }
}
