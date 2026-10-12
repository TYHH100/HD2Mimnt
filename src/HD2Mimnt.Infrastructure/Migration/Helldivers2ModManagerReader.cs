using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;
using HD2Mimnt.Core.Services;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Migration;

public sealed class Helldivers2ModManagerReader : IExternalManagerReader
{
    public ExternalManagerKind Manager => ExternalManagerKind.Helldivers2ModManager;
    private sealed record SavedState(bool Enabled, string Toggled, string Selected, int Order);

    public async Task<ExternalMigrationPlan> ReadAsync(ExternalMigrationSource source, CancellationToken cancellationToken = default)
    {
        var root = MigrationJson.DataDirectory(source.DataPath);
        var installation = MigrationJson.Root(source.InstallationPath ?? root);
        var plan = new ExternalMigrationPlan { Source = source, SourceIdentity = root };
        JsonDocument? settings = null;
        var settingsPath = Path.Combine(installation, "settings.json");
        if (File.Exists(settingsPath))
        {
            try { settings = await MigrationJson.ReadAsync(settingsPath, cancellationToken); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            { plan.Issues.Add(new(settingsPath, $"HD2MM 设置读取失败：{ex.Message}")); }
        }
        using var settingsLifetime = settings;
        var storage = source.ModsPath ?? settings?.RootElement.Get("StorageDirectory").Text();
        storage = string.IsNullOrWhiteSpace(storage) ? root : Path.GetFullPath(storage, installation);
        var modsPath = Directory.Exists(Path.Combine(storage, "Mods")) ? Path.Combine(storage, "Mods") : storage;
        ReadMods(modsPath, plan, cancellationToken);
        var dbPath = Path.Combine(root, "mod_manager.db");
        if (!File.Exists(dbPath)) dbPath = Path.Combine(storage, "mod_manager.db");
        if (!File.Exists(dbPath)) plan.Issues.Add(new(dbPath, "HD2MM 配置数据库不存在，无法迁移配置。"));
        else
        {
            try { await ReadDatabaseAsync(dbPath, modsPath, settings?.RootElement ?? default, plan, cancellationToken); }
            catch (Exception ex) when (ex is SqliteException or JsonException or IOException or UnauthorizedAccessException)
            { plan.Issues.Add(new(dbPath, $"HD2MM 配置数据库读取失败：{ex.Message}")); }
        }
        ApplyDeploymentOrder(settings?.RootElement ?? default, plan);
        ApplyOptionOrders(settings?.RootElement ?? default, plan);
        MigrationJson.CheckSource(plan);
        return plan;
    }

    private static void ReadMods(string root, ExternalMigrationPlan plan, CancellationToken token)
    {
        if (!Directory.Exists(root)) { plan.Issues.Add(new(root, "HD2MM 模组库目录不存在。")); return; }
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "manifest.json");
            ModManifest? manifest = null;
            Guid declared = Guid.Empty;
            if (File.Exists(path))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    manifest = new ManifestParser().ParseFromJsonElement(document.RootElement);
                    Guid.TryParse(document.RootElement.Get("Guid").Text(), out declared);
                    if (Guid.TryParse(document.RootElement.Get("HD2MimntOriginalGuid").Text(), out var originalGuid)) manifest.Guid = originalGuid;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
                { plan.Issues.Add(new(directory, $"HD2MM 清单读取失败：{ex.Message}")); continue; }
            }
            var folder = Path.GetFileName(directory);
            if (declared == Guid.Empty) Guid.TryParse(folder, out declared);
            var id = declared == Guid.Empty ? folder : declared.ToString();
            manifest ??= new ModManifest
            {
                Name = folder, ManifestVersion = ManifestVersion.Legacy,
                LegacyOptions = Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>().ToList()
            };
            if (manifest.Guid == Guid.Empty) manifest.Guid = declared == Guid.Empty ? MigrationJson.StableGuid($"hd2mm:{id}") : declared;
            plan.Mods.Add(new ExternalMigrationMod { SourceId = id, SourcePath = directory, Manifest = manifest, Tags = manifest.Tags.ToList() });
        }
    }

    private static async Task<bool> HasTableAsync(SqliteConnection connection, string table, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) > 0;
    }

    private static async Task ReadDatabaseAsync(string path, string modsPath, JsonElement settings, ExternalMigrationPlan plan, CancellationToken token)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        await connection.OpenAsync(token);
        var groups = new List<(string Id, string Name, List<string> Members)>();
        if (await HasTableAsync(connection, "mod_groups", token))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Name, ModGuids FROM mod_groups ORDER BY DisplayIndex";
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                groups.Add((reader.GetString(0), reader.GetString(1), JsonSerializer.Deserialize<List<string>>(reader.IsDBNull(2) ? "[]" : reader.GetString(2)) ?? []));
        }
        var global = new Dictionary<string, SavedState>(StringComparer.OrdinalIgnoreCase);
        var grouped = new Dictionary<string, Dictionary<string, SavedState>>(StringComparer.OrdinalIgnoreCase);
        var tags = settings.Get("Tags").Items().ToDictionary(item => item.Get("id").Text(), item => item.Get("name").Text(), StringComparer.OrdinalIgnoreCase);
        var known = plan.Mods.ToDictionary(mod => mod.SourceId, StringComparer.OrdinalIgnoreCase);
        var presentFolders = Directory.Exists(modsPath) ? Directory.EnumerateDirectories(modsPath).Select(Path.GetFileName).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (await HasTableAsync(connection, "enabled_mods", token))
        {
            using var columns = connection.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('enabled_mods') WHERE name='TagIds'";
            var hasTags = Convert.ToInt32(await columns.ExecuteScalarAsync(token)) > 0;
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT Guid, Enabled, Toggled, Selected, SortOrder, {(hasTags ? "TagIds" : "'[]'")} FROM enabled_mods ORDER BY SortOrder";
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetString(0);
                global[id] = ReadStateRow(reader, 1);
                if (known.TryGetValue(id, out var mod))
                {
                    foreach (var tagId in JsonSerializer.Deserialize<List<string>>(reader.IsDBNull(5) ? "[]" : reader.GetString(5)) ?? [])
                        if (tags.TryGetValue(tagId, out var name) && !mod.Tags.Contains(name, StringComparer.OrdinalIgnoreCase)) mod.Tags.Add(name);
                }
            }
        }
        if (await HasTableAsync(connection, "group_enabled_mods", token))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT GroupId, Guid, Enabled, Toggled, Selected, SortOrder FROM group_enabled_mods ORDER BY SortOrder";
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var group = reader.GetString(0);
                if (!grouped.TryGetValue(group, out var states)) grouped[group] = states = new(StringComparer.OrdinalIgnoreCase);
                states[reader.GetString(1)] = ReadStateRow(reader, 2);
            }
        }
        if (groups.Count == 0 && global.Count > 0)
            groups.Add(("current", "HD2MM 当前配置", global.OrderBy(item => item.Value.Order).Select(item => item.Key).ToList()));
        if (groups.Count == 0) { plan.Issues.Add(new(path, "HD2MM 数据库没有可识别的配置。")); return; }
        var separators = settings.Get("Separators").Items().OrderBy(item => item.Get("displayIndex").Int()).ToList();
        for (var index = 0; index < groups.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var group = groups[index];
            var profile = new ExternalMigrationProfile
            {
                SourceId = group.Id, Name = string.IsNullOrWhiteSpace(group.Name) ? $"HD2MM 配置 {index + 1}" : group.Name,
                WasActive = index == 0,
                IsDefault = Guid.TryParse(group.Id, out var groupId) && groupId == Guid.Parse("00000000-0000-0000-0000-000000000001"),
                DeploymentOrder = settings.Get("DeployBottomToTop").Bool() ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom
            };
            var states = grouped.GetValueOrDefault(group.Id) ?? global;
            var memberSet = group.Members.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ordered = states.Where(item => memberSet.Contains(item.Key)).OrderBy(item => item.Value.Order).Select(item => item.Key).ToList();
            ordered.AddRange(group.Members.Where(id => !ordered.Contains(id, StringComparer.OrdinalIgnoreCase)));
            ordered = ordered.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var damaged = false;
            var stale = new List<string>();
            foreach (var id in ordered)
            {
                // ModGuids 是缓存：已无文件且没有任何保存状态的引用是删除残留。
                // 有保存状态的缺失成员仍保留，交给完整配置校验报告文件缺失。
                if (!known.ContainsKey(id) && !presentFolders.Contains(id) && !states.ContainsKey(id) && !global.ContainsKey(id))
                {
                    stale.Add(id);
                    continue;
                }
                var state = states.GetValueOrDefault(id) ?? new SavedState(false, "[]", "[]", 0);
                try
                {
                    var toggled = JsonSerializer.Deserialize<List<bool>>(state.Toggled) ?? [];
                    var selected = JsonSerializer.Deserialize<List<int>>(state.Selected) ?? [];
                    var v1 = !known.TryGetValue(id, out var mod) || mod.Manifest.ManifestVersion == ManifestVersion.V1;
                    profile.Mods.Add(new(id, state.Enabled, v1
                        ? new ProfileModOptionsState { OptionToggles = toggled.Count == 0 ? null : toggled, OptionSelections = selected.Count == 0 ? null : selected }
                        : new ProfileModOptionsState { LegacySelectedIndex = selected.FirstOrDefault() }));
                }
                catch (JsonException ex) { damaged = true; plan.Issues.Add(new(profile.Name, $"成员 {id} 选项状态损坏：{ex.Message}")); }
            }
            if (stale.Count > 0)
                plan.Issues.Add(new(profile.Name, $"已忽略 {stale.Count} 个已删除且无保存状态的配置缓存引用：{string.Join(", ", stale)}"));
            for (var s = 0; s < separators.Count; s++)
            {
                var separator = separators[s];
                var owner = separator.Get("hd2mimntProfile").Text();
                if (owner.Length > 0 && !owner.Equals(group.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var memberIds = separator.Get("modGuids").Strings();
                var originalOrder = Math.Clamp(separator.Get("displayIndex").Int() - s, 0, ordered.Count);
                var order = ordered.Take(originalOrder).Count(id => profile.Mods.Any(mod => mod.SourceModId.Equals(id, StringComparison.OrdinalIgnoreCase)));
                if (memberIds.Count > 0)
                {
                    var positions = profile.Mods.Select((mod, position) => (mod, position)).Where(item => memberIds.Contains(item.mod.SourceModId, StringComparer.OrdinalIgnoreCase)).Select(item => item.position).ToList();
                    if (positions.Count > 0) order = positions.Min();
                }
                profile.Separators.Add(new(separator.Get("name").Text("分隔符"), order, !separator.Get("isExpanded").Bool(true),
                    MigrationJson.Color(separator.Get("color").Text()), separator.Get("hd2mimntMemberCount").ValueKind == JsonValueKind.Number
                        ? separator.Get("hd2mimntMemberCount").Int() : memberIds.Count > 0 ? memberIds.Count(id => profile.Mods.Any(mod => mod.SourceModId.Equals(id, StringComparison.OrdinalIgnoreCase))) : null));
            }
            if (!damaged) plan.Profiles.Add(profile);
        }
    }

    private static SavedState ReadStateRow(SqliteDataReader reader, int start)
        => new(reader.GetBoolean(start), reader.IsDBNull(start + 1) ? "[]" : reader.GetString(start + 1),
            reader.IsDBNull(start + 2) ? "[]" : reader.GetString(start + 2), reader.GetInt32(start + 3));

    private static void ApplyOptionOrders(JsonElement settings, ExternalMigrationPlan plan)
    {
        foreach (var mod in plan.Mods)
        {
            var optionOrder = settings.Get("OptionOrders").Get(mod.SourceId);
            var subOrders = settings.Get("SubOptionOrders").Get(mod.SourceId);
            if (optionOrder.ValueKind != JsonValueKind.Array && subOrders.ValueKind != JsonValueKind.Object) continue;
            var originalOptions = mod.Manifest.Options;
            var count = mod.Manifest.ManifestVersion == ManifestVersion.Legacy ? mod.Manifest.LegacyOptions?.Count ?? 0 : originalOptions?.Count ?? 0;
            var order = Permutation(optionOrder, count);
            foreach (var profile in plan.Profiles)
            {
                for (var i = 0; i < profile.Mods.Count; i++)
                {
                    var member = profile.Mods[i];
                    if (!member.SourceModId.Equals(mod.SourceId, StringComparison.OrdinalIgnoreCase)) continue;
                    var state = ProfileModOptionResolver.ResolveEffective(mod.Manifest, member.Options.ToJson());
                    if (mod.Manifest.ManifestVersion == ManifestVersion.Legacy)
                        state.LegacySelectedIndex = order.IndexOf(state.LegacySelectedIndex ?? 0);
                    else
                    {
                        var toggles = state.OptionToggles!;
                        var selections = state.OptionSelections!;
                        state.OptionToggles = order.Select(index => toggles[index]).ToList();
                        state.OptionSelections = order.Select(index => Permutation(subOrders.Get(index.ToString()), originalOptions![index].SubOptions?.Count ?? 0)
                            .IndexOf(selections[index]) is var selected && selected >= 0 ? selected : 0).ToList();
                    }
                    profile.Mods[i] = member with { Options = state };
                }
            }
            if (mod.Manifest.ManifestVersion == ManifestVersion.Legacy)
                mod.Manifest.LegacyOptions = order.Select(index => mod.Manifest.LegacyOptions![index]).ToList();
            else if (originalOptions != null)
            {
                for (var i = 0; i < originalOptions.Count; i++)
                    if (originalOptions[i].SubOptions is { } subs)
                        originalOptions[i].SubOptions = Permutation(subOrders.Get(i.ToString()), subs.Count).Select(index => subs[index]).ToList();
                mod.Manifest.Options = order.Select(index => originalOptions[index]).ToList();
            }
        }
    }

    private static void ApplyDeploymentOrder(JsonElement settings, ExternalMigrationPlan plan)
    {
        if (!settings.Get("UseDeploymentOrder").Bool()) return;
        var order = settings.Get("DeploymentOrderGuids").Strings().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var profile in plan.Profiles.ToList())
        {
            var original = profile.Mods.ToList();
            var byId = original.ToDictionary(item => item.SourceModId, StringComparer.OrdinalIgnoreCase);
            var sorted = order.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            sorted.AddRange(original.Where(item => !sorted.Contains(item)));
            var separators = profile.Separators.OrderBy(item => item.Order).ToList();
            var remapped = new List<ExternalMigrationSeparator>();
            var damaged = false;
            for (var i = 0; i < separators.Count; i++)
            {
                var separator = separators[i];
                var start = Math.Clamp(separator.Order, 0, original.Count);
                var end = Math.Clamp(separator.MemberCount.HasValue ? start + separator.MemberCount.Value
                    : i + 1 < separators.Count ? separators[i + 1].Order : original.Count, start, original.Count);
                var positions = original.Skip(start).Take(end - start).Select(item => sorted.IndexOf(item)).Order().ToList();
                if (positions.Count > 0 && positions.Last() - positions.First() + 1 != positions.Count)
                {
                    plan.Issues.Add(new(profile.Name, "自定义部署排序使分隔符成员不连续，目标配置无法同时表达此排序和分组；请在源管理器调整后重试。"));
                    damaged = true;
                    break;
                }
                remapped.Add(separator with { Order = positions.Count > 0 ? positions[0] : Math.Min(start, sorted.Count), MemberCount = positions.Count });
            }
            if (damaged) { plan.Profiles.Remove(profile); continue; }
            profile.Mods.Clear();
            profile.Mods.AddRange(sorted);
            profile.Separators.Clear();
            profile.Separators.AddRange(remapped.OrderBy(item => item.Order));
        }
    }

    private static List<int> Permutation(JsonElement value, int count)
    {
        var order = value.Items().Select(item => item.Int(-1)).ToList();
        if (value.ValueKind != JsonValueKind.Array) return Enumerable.Range(0, count).ToList();
        if (order.Count != count || order.Distinct().Count() != count || order.Any(index => index < 0 || index >= count))
            throw new InvalidDataException("HD2MM 选项排序与清单不匹配，无法保留选择状态。");
        return order;
    }
}
