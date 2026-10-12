using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Infrastructure.Migration;

public sealed class EchelonReader : IExternalManagerReader
{
    public ExternalManagerKind Manager => ExternalManagerKind.Echelon;

    public async Task<ExternalMigrationPlan> ReadAsync(ExternalMigrationSource source,
        CancellationToken cancellationToken = default)
    {
        var root = MigrationJson.DataDirectory(source.DataPath);
        using var document = await MigrationJson.ReadAsync(Path.Combine(root, "state.json"), cancellationToken);
        var state = document.RootElement;
        if (state.Get("version").Int() != 2) throw new InvalidDataException("仅支持 Echelon state.json 版本 2。");
        if (state.Get("library").ValueKind != JsonValueKind.Object || state.Get("profiles").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Echelon state.json 缺少 library 或 profiles。");
        var modsRoot = source.ModsPath ?? Path.Combine(root, "mods");
        if (source.ModsPath == null && File.Exists(Path.Combine(root, "locations.json")))
        {
            using var locations = await MigrationJson.ReadAsync(Path.Combine(root, "locations.json"), cancellationToken);
            var configured = locations.RootElement.Get("mods").Text();
            if (configured.Length > 0) modsRoot = Path.GetFullPath(configured, root);
        }
        var plan = new ExternalMigrationPlan { Source = source, SourceIdentity = root };
        var defaults = new Dictionary<string, ProfileModOptionsState>(StringComparer.OrdinalIgnoreCase);
        var hasBase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraryFolders = await ReadLibraryFoldersAsync(root, cancellationToken);
        foreach (var property in state.Get("library").EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = property.Value;
            var sourceId = item.Get("id").Text(property.Name);
            var store = item.Get("store").Text();
            var path = MigrationJson.Inside(modsRoot, store);
            if (source.ModsPath == null && !Directory.Exists(path))
            {
                var local = MigrationJson.Inside(Path.Combine(root, "mods"), store);
                if (Directory.Exists(local)) path = local;
                else if (libraryFolders.TryGetValue(sourceId, out var exported) && Directory.Exists(exported)) path = exported;
            }
            var manifest = MigrationJson.Manifest(item, "name", "id");
            if (Guid.TryParse(item.Get("guid").Text(), out var storedGuid)) manifest.Guid = storedGuid;
            var onDiskPath = Path.Combine(path, "manifest.json");
            if (File.Exists(onDiskPath))
            {
                using var onDisk = await MigrationJson.ReadAsync(onDiskPath, cancellationToken);
                var metadata = new ManifestParser().ParseFromJsonElement(onDisk.RootElement);
                if (Guid.TryParse(onDisk.RootElement.Get("Guid").Text(), out var guid)) manifest.Guid = guid;
                manifest.Author = metadata.Author;
                manifest.Version = metadata.Version;
                manifest.NexusData = metadata.NexusData;
            }
            var sets = item.Get("sets").Items().ToList();
            var layout = new List<ExternalMigrationFile>();
            for (var index = 0; index < sets.Count; index++)
            {
                var files = sets[index].Get("files");
                foreach (var file in files.ValueKind == JsonValueKind.Object
                    ? files.EnumerateObject().Select(value => value.Value.Text()) : files.Strings())
                {
                    _ = MigrationJson.Inside(path, file);
                    layout.Add(new(file, $"migration-sets/{index:D4}/{Path.GetFileName(file.Replace('\\', '/'))}"));
                }
            }
            var options = item.Get("options").Items().ToList();
            // Echelon 有选项时仅部署选项引用的集合；无选项时才部署全部集合。
            var baseSets = options.Count == 0 ? Enumerable.Range(0, sets.Count).ToList() : [];
            var declaredDefaults = ReadState(item.Get("option_defaults"));
            manifest.Options = options.Select((option, index) => new ModOption
            {
                Name = option.Get("name").Text(), Description = option.Get("description").Text(),
                DefaultEnabled = index < declaredDefaults.OptionToggles?.Count ? declaredDefaults.OptionToggles[index] : true,
                Include = SetDirectories(option.Get("include"), sets),
                SubOptions = option.Get("subs").Items().Select(sub => new ModSubOption
                {
                    Name = sub.Get("name").Text(), Description = sub.Get("description").Text(),
                    Include = SetDirectories(sub.Get("include"), sets)
                }).ToList()
            }).ToList();
            if (baseSets.Count > 0)
            {
                manifest.Options.Insert(0, new ModOption { Name = "基础文件", DefaultEnabled = true,
                    Include = baseSets.SelectMany(index => SetDirectories(index, sets)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
                hasBase.Add(sourceId);
            }
            if (sets.Count == 0) plan.Issues.Add(new(manifest.Name, "源模组未声明文件集合。"));
            defaults[sourceId] = declaredDefaults;
            plan.Mods.Add(new ExternalMigrationMod
            {
                SourceId = sourceId, SourcePath = path, Manifest = manifest, FileLayout = layout,
                Tags = item.Get("tags").Strings(),
                ImagePath = item.Get("image").Text() is { Length: > 0 } image ? MigrationJson.Inside(Path.Combine(root, "images"), image) : null
            });
        }
        var sourceProfiles = state.Get("profiles").Items().ToList();
        // 版本 2 没有默认 ID 标记；仅识别唯一的原生 Default 名称，不把活动或第一份配置当默认。
        var defaultProfiles = sourceProfiles.Where(item => item.Get("name").Text() == "Default").ToList();
        foreach (var item in sourceProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = new ExternalMigrationProfile
            {
                SourceId = item.Get("id").Text(), Name = item.Get("name").Text("Echelon 配置"),
                WasActive = item.Get("id").Text() == state.Get("active_profile").Text(),
                IsDefault = defaultProfiles.Count == 1 && item.Get("id").Text() == defaultProfiles[0].Get("id").Text(),
                DeploymentOrder = state.Get("settings").Get("priority").Text("last") == "first"
                    ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom
            };
            foreach (var entry in item.Get("entries").Items())
            {
                var type = entry.Get("type").Text();
                if (type == "sep")
                {
                    if (entry.Get("bingus").Bool() && entry.Get("auto").Bool())
                    {
                        profile.IsLuaGroupCollapsed = entry.Get("collapsed").Bool();
                        profile.LuaSeparatorColor = MigrationJson.Color(entry.Get("bg").Text());
                    }
                    else profile.Separators.Add(new(entry.Get("title").Text("分隔符"), profile.Mods.Count,
                        entry.Get("collapsed").Bool(), MigrationJson.Color(entry.Get("bg").Text())));
                }
                else if (type == "mod")
                {
                    var id = entry.Get("id").Text();
                    var selection = entry.Get("opts");
                    if (selection.ValueKind != JsonValueKind.Object) selection = entry.Get("options");
                    var options = selection.ValueKind == JsonValueKind.Object ? ReadState(selection)
                        : defaults.GetValueOrDefault(id) ?? new();
                    var fallback = defaults.GetValueOrDefault(id) ?? new();
                    var definition = plan.Mods.FirstOrDefault(mod => mod.SourceId.Equals(id, StringComparison.OrdinalIgnoreCase));
                    var count = (definition?.Manifest.Options?.Count ?? 0) - (hasBase.Contains(id) ? 1 : 0);
                    options = new ProfileModOptionsState
                    {
                        OptionToggles = Enumerable.Range(0, count).Select(index => index < options.OptionToggles?.Count ? options.OptionToggles[index]
                            : index < fallback.OptionToggles?.Count ? fallback.OptionToggles[index] : true).ToList(),
                        OptionSelections = Enumerable.Range(0, count).Select(index => index < options.OptionSelections?.Count ? options.OptionSelections[index]
                            : index < fallback.OptionSelections?.Count ? fallback.OptionSelections[index] : 0).ToList()
                    };
                    if (hasBase.Contains(id))
                    {
                        options.OptionToggles = new[] { true }.Concat(options.OptionToggles ?? []).ToList();
                        options.OptionSelections = new[] { 0 }.Concat(options.OptionSelections ?? []).ToList();
                    }
                    profile.Mods.Add(new(id, entry.Get("enabled").Bool(true), options));
                }
                else plan.Issues.Add(new(profile.Name, $"未识别的配置成员类型：{type}"));
            }
            for (var index = 0; index < profile.Separators.Count; index++)
                profile.Separators[index] = profile.Separators[index] with
                { MemberCount = (index + 1 < profile.Separators.Count ? profile.Separators[index + 1].Order : profile.Mods.Count) - profile.Separators[index].Order };
            plan.Profiles.Add(profile);
        }
        MigrationJson.CheckSource(plan);
        return plan;
    }

    private static async Task<Dictionary<string, string>> ReadLibraryFoldersAsync(string root, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, "library.json");
        if (!File.Exists(path)) return result;
        try
        {
            using var document = await MigrationJson.ReadAsync(path, token);
            foreach (var item in document.RootElement.Get("mods").Items())
            {
                var id = item.Get("id").Text();
                var folder = item.Get("folder").Text();
                if (id.Length > 0 && folder.Length > 0) result[id] = folder;
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("Echelon library.json 损坏，无法读取备用模组路径。", ex); }
        return result;
    }

    private static List<string> SetDirectories(JsonElement indices, IReadOnlyList<JsonElement> sets)
        => indices.Items().Select(value => value.Int(-1)).SelectMany(index => SetDirectories(index, sets))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> SetDirectories(int index, IReadOnlyList<JsonElement> sets)
    {
        if (index < 0 || index >= sets.Count)
            throw new InvalidDataException($"Echelon 选项引用无效集合索引：{index}");
        return [$"migration-sets/{index:D4}"];
    }

    private static ProfileModOptionsState ReadState(JsonElement state) => new()
    {
        OptionToggles = state.Get("on").ValueKind == JsonValueKind.Array
            ? state.Get("on").Items().Select(value => value.Bool(true)).ToList() : null,
        OptionSelections = state.Get("sel").ValueKind == JsonValueKind.Array
            ? state.Get("sel").Items().Select(value => value.Int()).ToList() : null
    };
}
