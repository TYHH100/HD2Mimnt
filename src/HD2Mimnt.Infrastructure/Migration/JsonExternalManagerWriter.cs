using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;

namespace HD2Mimnt.Infrastructure.Migration;

public sealed class Hd2ArsenalWriter : JsonExternalManagerWriter
{
    public override ExternalManagerKind Manager => ExternalManagerKind.HD2Arsenal;
}

public sealed class EchelonWriter : JsonExternalManagerWriter
{
    public override ExternalManagerKind Manager => ExternalManagerKind.Echelon;
}

public abstract class JsonExternalManagerWriter : IExternalManagerWriter
{
    public abstract ExternalManagerKind Manager { get; }
    private string DataFile(ExternalMigrationSource source) => Path.Combine(ExternalExportSupport.DataRoot(source), Manager == ExternalManagerKind.Echelon ? "state.json" : "hd2a_data.json");

    public async Task InspectAsync(ExternalMigrationPlan plan, CancellationToken cancellationToken = default)
    {
        var root = ExternalExportSupport.DataRoot(plan.Source);
        var data = await ExternalExportSupport.ReadObjectAsync(DataFile(plan.Source), cancellationToken);
        var echelon = Manager == ExternalManagerKind.Echelon;
        if (data.Count > 0 && (echelon ? data["version"]?.GetValue<int>() != 2 || data["library"] is not JsonObject || data["profiles"] is not JsonArray
            : data["modsLibrary"] is not JsonArray || data["modsList"] is not JsonObject))
            throw new InvalidDataException("目标管理器数据版本或结构不受支持。");
        var locationsPath = Path.Combine(root, "locations.json");
        var configured = echelon ? (await ExternalExportSupport.ReadObjectAsync(locationsPath, cancellationToken))["mods"]?.GetValue<string>()
            : data["userModsDir"]?.GetValue<string>();
        plan.TargetModsPath = Path.GetFullPath(plan.Source.ModsPath ?? (string.IsNullOrWhiteSpace(configured) ? Path.Combine(root, "mods") : configured), root);
        plan.TargetDeploymentOrder = echelon ? data["settings"]?["priority"]?.GetValue<string>() == "first" ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom
            : data["setTopPriority"]?.GetValue<bool>() == true ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom;
        await ExternalExportSupport.InspectFilesAsync(plan, echelon ? [DataFile(plan.Source), locationsPath, Path.Combine(root, "library.json")] : [DataFile(plan.Source)], cancellationToken);
    }

    public async Task<ExternalMigrationResult> WriteAsync(ExternalMigrationPlan plan, IProgress<ExternalMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var token = cancellationToken;
        ExternalExportSupport.EnsureStopped(plan.Source);
        var data = await ExternalExportSupport.ReadObjectAsync(DataFile(plan.Source), token);
        await using var session = new ExternalExportSession(plan);
        var echelon = Manager == ExternalManagerKind.Echelon;
        if (echelon)
        {
            data["version"] = 2;
            data["library"] ??= new JsonObject(); data["profiles"] ??= new JsonArray(); data["settings"] ??= new JsonObject();
            data["settings"]!["priority"] = plan.TargetDeploymentOrder == DeploymentOrder.BottomToTop ? "first" : "last";
        }
        else { data["modsLibrary"] ??= new JsonArray(); data["modsList"] ??= new JsonObject(); data["profileOrder"] ??= new JsonArray(); }
        var library = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var definitions = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
        var completed = 0; var total = plan.Mods.Count + plan.Profiles.Count;
        foreach (var mod in plan.Mods)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new(completed, total, "复制模组文件", mod.Manifest.Name));
            var id = ExternalExportSupport.ModId(mod);
            var target = Path.Combine(plan.TargetModsPath, id);
            var stage = await session.PrepareModAsync(mod, target, token);
            if (stage != null)
            {
                await ExternalExportSupport.SaveManifestAsync(mod, stage, token);
                await session.MarkModAsync(mod, stage, token);
            }
            var manifest = NativeManifest(mod.Manifest);
            if (echelon && manifest.Options is not { Count: > 0 })
                manifest.Options = [new() { Name = "基本文件", Include = ["."], DefaultEnabled = true }];
            definitions[mod.SourceId] = manifest;
            var node = echelon ? BuildEchelonMod(mod, id, manifest) : BuildArsenalMod(mod, id, target, manifest);
            if (echelon)
            {
                var image = mod.ImagePath ?? (mod.Manifest.IconPath == null ? null : MigrationJson.Inside(mod.SourcePath, mod.Manifest.IconPath));
                if (image != null && File.Exists(image))
                {
                    var name = id + Path.GetExtension(image);
                    await ExternalExportSupport.CopyFileAsync(image, session.AddFile(Path.Combine(ExternalExportSupport.DataRoot(plan.Source), "images", name)), token);
                    node["image"] = name;
                }
            }
            library[mod.SourceId] = node;
            if (echelon) data["library"]![id] ??= node.DeepClone();
            else if (!data["modsLibrary"]!.AsArray().Any(item => item?["uuid"]?.GetValue<string>() == id)) data["modsLibrary"]!.AsArray().Add(node.DeepClone());
            session.Result.ModIds[mod.SourceId] = Guid.Parse(id);
            progress?.Report(new(++completed, total, "处理模组完成", mod.Manifest.Name));
        }
        var names = (echelon ? data["profiles"]!.AsArray().Select(item => item?["name"]?.GetValue<string>() ?? "")
            : data["modsList"]!.AsObject().Select(item => item.Value?["label"]?.GetValue<string>() ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var original in plan.Profiles)
        {
            token.ThrowIfCancellationRequested();
            var profile = ExternalExportSupport.MapProfile(original, plan);
            var id = ExternalExportSupport.ProfileId(original, plan);
            if (profile.IsLibraryLayout && !echelon)
            {
                RestoreArsenalLibraryLayout(data["modsLibrary"]!.AsArray(), profile, library);
                session.Result.ProfileIds[original.SourceId] = Guid.Parse(id);
                session.Result.ImportedProfiles++;
            }
            else
            {
                var exists = echelon ? data["profiles"]!.AsArray().Any(item => item?["id"]?.GetValue<string>() == id) : data["modsList"]![id] != null;
                if (exists) session.Result.ReusedProfiles++;
                else
                {
                    var entries = new JsonArray();
                    foreach (var entry in ExternalExportSupport.Entries(profile))
                    {
                        if (entry.Separator is { } separator) entries.Add(Separator(separator, echelon));
                        else if (entry.Mod is { } member)
                        {
                            var mod = plan.Mods.Single(mod => mod.SourceId.Equals(member.SourceModId, StringComparison.OrdinalIgnoreCase));
                            var state = NativeState(mod.Manifest, member.Options, definitions[member.SourceModId]);
                            if (echelon) entries.Add(new JsonObject { ["type"] = "mod", ["id"] = ExternalExportSupport.ModId(mod), ["enabled"] = member.Enabled,
                                ["options"] = StateNode(state) });
                            else
                            {
                                var node = (JsonObject)library[member.SourceModId].DeepClone();
                                node["enabled"] = member.Enabled;
                                node["options"] = ArsenalOptions(definitions[member.SourceModId], state, node["path"]!.GetValue<string>());
                                entries.Add(node);
                            }
                        }
                    }
                    if (profile.Mods.Any(member => plan.Mods.Single(mod => mod.SourceId == member.SourceModId).Role != BslModRole.None))
                    {
                        var normalCount = profile.Mods.Count(member => plan.Mods.Single(mod => mod.SourceId == member.SourceModId).Role == BslModRole.None);
                        var boundary = plan.TargetDeploymentOrder == DeploymentOrder.BottomToTop ? echelon ? profile.Mods.Count - normalCount : 0 : normalCount;
                        var entryIndex = 0; var memberCount = 0;
                        while (entryIndex < entries.Count && memberCount < boundary)
                        { if (entries[entryIndex]?[echelon ? "type" : "uuid"]?.GetValue<string>() is { } type && (echelon ? type == "mod" : entries[entryIndex]?["type"]?.GetValue<string>() != "separator")) memberCount++; entryIndex++; }
                        var lua = Separator(new("Bingus", boundary, profile.IsLuaGroupCollapsed, profile.LuaSeparatorColor), echelon);
                        if (echelon) { lua["bingus"] = true; lua["auto"] = true; } else lua["hd2mimntLua"] = true;
                        entries.Insert(entryIndex, lua);
                    }
                    var name = ExternalExportSupport.UniqueName(profile.Name, names);
                    if (echelon) data["profiles"]!.AsArray().Add(new JsonObject { ["id"] = id, ["name"] = name, ["entries"] = entries });
                    else { data["modsList"]![id] = new JsonObject { ["label"] = name, ["mods"] = entries }; data["profileOrder"]!.AsArray().Add(id); }
                    session.Result.ImportedProfiles++;
                }
                session.Result.ProfileIds[original.SourceId] = Guid.Parse(id);
                if (original.WasActive)
                {
                    if (echelon) data["active_profile"] = id; else data["selectedProfile"] = id;
                    session.Result.SourceActiveProfileId = Guid.Parse(id);
                }
            }
            progress?.Report(new(++completed, total, "准备配置完成", original.Name));
        }
        if (echelon)
        {
            var locationsPath = Path.Combine(ExternalExportSupport.DataRoot(plan.Source), "locations.json");
            var locations = await ExternalExportSupport.ReadObjectAsync(locationsPath, token);
            locations["mods"] = plan.TargetModsPath;
            await File.WriteAllTextAsync(session.AddFile(locationsPath), locations.ToJsonString(ExternalExportSupport.JsonOptions), token);
        }
        else data["userModsDir"] = plan.TargetModsPath;
        await File.WriteAllTextAsync(session.AddFile(DataFile(plan.Source)), data.ToJsonString(ExternalExportSupport.JsonOptions), token);
        progress?.Report(new(completed, total, "保存迁移结果", ""));
        await session.CommitAsync(token);
        session.Result.Issues.AddRange(plan.Issues);
        return session.Result;
    }

    private static ModManifest NativeManifest(ModManifest source)
    {
        var manifest = JsonSerializer.Deserialize<ModManifest>(JsonSerializer.Serialize(source))!;
        if (source.ManifestVersion == ManifestVersion.Legacy && source.LegacyOptions is { Count: > 0 })
            manifest.Options = [new ModOption { Name = "变体", Include = [], DefaultEnabled = true,
                SubOptions = source.LegacyOptions.Select(option => new ModSubOption { Name = option, Include = [option] }).ToList() }];
        manifest.ManifestVersion = ManifestVersion.V1;
        manifest.LegacyOptions = null;
        if (manifest.Options?.Any(option => option.Include == null || option.SubOptions?.Any(sub => sub.Include == null) == true) == true)
        {
            foreach (var option in manifest.Options)
            {
                option.Include = [];
                if (option.SubOptions != null) foreach (var sub in option.SubOptions) sub.Include = [];
            }
            manifest.Options.Add(new() { Name = "全部文件", Include = ["."], DefaultEnabled = true });
        }
        return manifest;
    }

    private static ProfileModOptionsState NativeState(ModManifest original, ProfileModOptionsState state, ModManifest native)
    {
        var effective = ProfileModOptionResolver.ResolveEffective(original, state.ToJson());
        if (original.ManifestVersion == ManifestVersion.Legacy && original.LegacyOptions is { Count: > 0 })
            return new() { OptionToggles = [true], OptionSelections = [effective.LegacySelectedIndex ?? 0] };
        if ((native.Options?.Count ?? 0) > (original.Options?.Count ?? 0))
        {
            effective.OptionToggles = (effective.OptionToggles ?? []).Concat([true]).ToList();
            effective.OptionSelections = (effective.OptionSelections ?? []).Concat([0]).ToList();
        }
        return effective;
    }

    private static JsonObject StateNode(ProfileModOptionsState state) => new()
    { ["on"] = JsonSerializer.SerializeToNode(state.OptionToggles ?? []), ["sel"] = JsonSerializer.SerializeToNode(state.OptionSelections ?? []) };

    private static JsonObject BuildArsenalMod(ExternalMigrationMod mod, string id, string target, ModManifest manifest) => new()
    {
        ["uuid"] = id, ["path"] = target, ["label"] = manifest.Name, ["description"] = manifest.Description,
        ["version"] = manifest.Version, ["author"] = manifest.Author, ["tags"] = JsonSerializer.SerializeToNode(mod.Tags),
        ["iconPath"] = mod.ImagePath != null ? Path.Combine(target, "hd2mimnt-preview" + Path.GetExtension(mod.ImagePath))
            : manifest.IconPath == null ? "" : MigrationJson.Inside(target, manifest.IconPath),
        ["options"] = ArsenalOptions(manifest, ProfileModOptionResolver.ResolveEffective(manifest, null), target),
        ["patchFileNames"] = JsonSerializer.SerializeToNode(Directory.EnumerateFiles(mod.SourcePath, "*.patch_*", SearchOption.AllDirectories).Select(Path.GetFileName)),
        ["contentHash"] = mod.ContentHash, ["addedAt"] = DateTime.UtcNow.ToString("O"), ["previousVersions"] = new JsonArray(),
        ["nexusData"] = manifest.NexusData == null ? null : new JsonObject { ["modId"] = manifest.NexusData.ModId, ["version"] = manifest.NexusData.Version }
    };

    private static JsonArray ArsenalOptions(ModManifest manifest, ProfileModOptionsState state, string root)
    {
        var array = new JsonArray();
        foreach (var (option, index) in (manifest.Options ?? []).Select((option, index) => (option, index)))
        {
            var subs = new JsonArray();
            foreach (var (sub, subIndex) in (option.SubOptions ?? []).Select((sub, subIndex) => (sub, subIndex)))
                subs.Add(new JsonObject { ["name"] = sub.Name, ["description"] = sub.Description, ["include"] = JsonSerializer.SerializeToNode(sub.Include ?? []),
                    ["enabled"] = (state.OptionSelections?.ElementAtOrDefault(index) ?? 0) == subIndex,
                    ["iconPath"] = sub.Image == null ? "" : MigrationJson.Inside(root, sub.Image) });
            array.Add(new JsonObject { ["name"] = option.Name, ["description"] = option.Description, ["include"] = JsonSerializer.SerializeToNode(option.Include ?? []),
                ["enabled"] = state.OptionToggles?.ElementAtOrDefault(index) ?? option.DefaultEnabled ?? true,
                ["selected"] = state.OptionSelections?.ElementAtOrDefault(index) ?? 0, ["suboptions"] = subs,
                ["iconPath"] = option.Image == null ? "" : MigrationJson.Inside(root, option.Image) });
        }
        return array;
    }

    private static JsonObject BuildEchelonMod(ExternalMigrationMod mod, string id, ModManifest manifest)
    {
        var groups = Directory.EnumerateFiles(mod.SourcePath, "*", SearchOption.AllDirectories)
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"^[0-9a-fA-F]{16}\.patch_\d+(\.stream|\.gpu_resources)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .GroupBy(file => System.Text.RegularExpressions.Regex.Replace(Path.GetRelativePath(mod.SourcePath, file), @"\.(stream|gpu_resources)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => Path.GetDirectoryName(group.Key), StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => Path.GetFileName(group.Key)[..16], StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => PatchNumber(group.Key)).ToList();
        var sets = new JsonArray();
        foreach (var group in groups)
        {
            var files = new JsonObject();
            foreach (var file in group) files[Path.GetFileName(file).EndsWith(".stream", StringComparison.OrdinalIgnoreCase) ? ".stream"
                : Path.GetFileName(file).EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) ? ".gpu_resources" : ""] = Path.GetRelativePath(mod.SourcePath, file);
            sets.Add(new JsonObject { ["hash"] = Path.GetFileName(group.Key)[..16], ["num"] = PatchNumber(group.Key), ["files"] = files });
        }
        JsonNode Indices(List<string>? directories) => JsonSerializer.SerializeToNode(groups.Select((group, index) => (group, index))
            .Where(item => (directories ?? []).Any(directory => item.group.Any(file => MigrationJson.Root(Path.GetDirectoryName(file)!)
                .Equals(MigrationJson.Inside(mod.SourcePath, directory), StringComparison.OrdinalIgnoreCase))))
            .Select(item => item.index))!;
        var options = new JsonArray();
        foreach (var option in manifest.Options ?? [])
        {
            var subs = new JsonArray();
            foreach (var sub in option.SubOptions ?? []) subs.Add(new JsonObject { ["name"] = sub.Name, ["description"] = sub.Description, ["include"] = Indices(sub.Include) });
            options.Add(new JsonObject { ["name"] = option.Name, ["description"] = option.Description, ["include"] = Indices(option.Include), ["subs"] = subs });
        }
        return new JsonObject { ["id"] = id, ["guid"] = manifest.Guid.ToString(), ["store"] = id, ["name"] = manifest.Name, ["description"] = manifest.Description,
            ["author"] = manifest.Author, ["version"] = manifest.Version, ["sets"] = sets, ["options"] = options,
            ["option_defaults"] = StateNode(ProfileModOptionResolver.ResolveEffective(manifest, null)), ["tags"] = JsonSerializer.SerializeToNode(mod.Tags),
            ["added_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["fp"] = mod.ContentHash };
    }

    private static int PatchNumber(string path)
        => int.Parse(path.AsSpan(path.LastIndexOf(".patch_", StringComparison.OrdinalIgnoreCase) + 7), System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject Separator(ExternalMigrationSeparator separator, bool echelon) => echelon
        ? new() { ["type"] = "sep", ["id"] = Guid.NewGuid().ToString("N"), ["title"] = separator.Name, ["bg"] = separator.Color,
            ["fg"] = "#FFFFFF", ["collapsed"] = separator.IsCollapsed }
        : new() { ["type"] = "separator", ["uuid"] = "separator-" + Guid.NewGuid().ToString("N"), ["label"] = separator.Name,
            ["backgroundColor"] = separator.Color, ["textColor"] = "#FFFFFF", ["collapsed"] = separator.IsCollapsed };

    private static void RestoreArsenalLibraryLayout(JsonArray target, ExternalMigrationProfile layout, Dictionary<string, JsonObject> library)
    {
        var exportedIds = library.Values.Select(mod => mod["uuid"]!.GetValue<string>()).ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--)
            if (exportedIds.Contains(target[index]?["uuid"]?.GetValue<string>() ?? "") || target[index]?["hd2mimntLayout"]?.GetValue<string>() == layout.SourceId) target.RemoveAt(index);
        foreach (var entry in ExternalExportSupport.Entries(layout))
        {
            if (entry.Separator is { } separator) { var node = Separator(separator, false); node["hd2mimntLayout"] = layout.SourceId; target.Add(node); }
            else target.Add(library[entry.Mod!.SourceModId].DeepClone());
        }
    }
}
