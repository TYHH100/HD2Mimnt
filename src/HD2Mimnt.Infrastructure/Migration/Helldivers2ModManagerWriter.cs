using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;
using Microsoft.Data.Sqlite;
using HD2Mimnt.Infrastructure.Analysis;

namespace HD2Mimnt.Infrastructure.Migration;

public sealed class Helldivers2ModManagerWriter : IExternalManagerWriter
{
    public ExternalManagerKind Manager => ExternalManagerKind.Helldivers2ModManager;
    private static string SettingsPath(ExternalMigrationSource target) => Path.Combine(target.InstallationPath ?? ExternalExportSupport.DataRoot(target), "settings.json");
    private static string NativeId(ExternalMigrationMod mod) => BslModDetector.KnownRole(mod.Manifest.Guid) != null ? mod.Manifest.Guid.ToString() : ExternalExportSupport.ModId(mod);

    public async Task InspectAsync(ExternalMigrationPlan plan, CancellationToken cancellationToken = default)
    {
        var settings = await ExternalExportSupport.ReadObjectAsync(SettingsPath(plan.Source), cancellationToken);
        var storage = settings["StorageDirectory"]?.GetValue<string>();
        var root = ExternalExportSupport.DataRoot(plan.Source);
        storage = string.IsNullOrWhiteSpace(storage) ? root : Path.GetFullPath(storage, plan.Source.InstallationPath ?? root);
        plan.TargetModsPath = Path.GetFullPath(plan.Source.ModsPath ?? Path.Combine(storage, "Mods"));
        if (plan.Source.ModsPath != null && Directory.Exists(Path.Combine(plan.TargetModsPath, "Mods"))) plan.TargetModsPath = Path.Combine(plan.TargetModsPath, "Mods");
        plan.TargetDeploymentOrder = settings["DeployBottomToTop"]?.GetValue<bool>() == true ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom;
        if (settings["UseDeploymentOrder"]?.GetValue<bool>() == true)
            plan.Issues.Add(new("HD2MM", "目标正在使用独立部署排序，迁回时将在该排序末尾追加导出配置的顺序。"));
        var db = Path.Combine(root, "mod_manager.db");
        if (File.Exists(db + "-wal") && new FileInfo(db + "-wal").Length > 0)
            plan.Issues.Add(new("HD2MM", "数据库仍有 WAL 日志，请正常退出源管理器使其完成保存后再迁回。", true));
        await ExternalExportSupport.InspectFilesAsync(plan, [SettingsPath(plan.Source), db, db + "-wal"], cancellationToken);
        foreach (var duplicate in plan.Mods.GroupBy(NativeId).Where(group => group.Count() > 1))
            plan.Issues.Add(new(duplicate.Key, "HD2MM 无法同时保存相同固定加载器 GUID 的多个版本，请先合并重复版本。", true));
        var separated = plan.Profiles.Where(profile => profile.Separators.Count > 0 && !profile.IsLibraryLayout).ToList();
        if (separated.Count > 1)
        {
            var signatures = separated.Select(profile => JsonSerializer.Serialize(profile.Separators)).Distinct().Count();
            if (signatures > 1) plan.Issues.Add(new("HD2MM", "HD2MM 使用全局分隔符；各配置分隔符保留为成员绑定的全局条目，配置间显示位置由成员匹配。"));
        }
    }

    public async Task<ExternalMigrationResult> WriteAsync(ExternalMigrationPlan plan, IProgress<ExternalMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var token = cancellationToken;
        ExternalExportSupport.EnsureStopped(plan.Source);
        var root = ExternalExportSupport.DataRoot(plan.Source);
        var databasePath = Path.Combine(root, "mod_manager.db");
        var settings = await ExternalExportSupport.ReadObjectAsync(SettingsPath(plan.Source), token);
        await using var session = new ExternalExportSession(plan);
        var stagedDb = session.AddFile(databasePath);
        if (File.Exists(databasePath))
        {
            await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stagedDb, Pooling = false }.ConnectionString);
            await source.OpenAsync(token); await destination.OpenAsync(token);
            source.BackupDatabase(destination);
        }
        var nativeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var completed = 0; var total = plan.Mods.Count + plan.Profiles.Count;
        foreach (var mod in plan.Mods)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new(completed, total, "复制模组文件", mod.Manifest.Name));
            var id = NativeId(mod);
            nativeIds.Add(mod.SourceId, id);
            var target = Path.Combine(plan.TargetModsPath, id);
            var knownLoader = BslModDetector.KnownRole(mod.Manifest.Guid) != null;
            if (knownLoader && Directory.Exists(plan.TargetModsPath))
            {
                var matches = Directory.EnumerateDirectories(plan.TargetModsPath).Where(path => new HD2Mimnt.Core.Services.ManifestParser().ParseFromFile(Path.Combine(path, "manifest.json"))?.Guid == mod.Manifest.Guid).ToList();
                if (matches.Count > 1) throw new InvalidDataException("目标库包含重复固定加载器，请先整理后重试。");
                if (matches.Count == 1) target = matches[0];
            }
            var stage = await session.PrepareModAsync(mod, target, token, knownLoader);
            if (stage != null)
            {
                await ExternalExportSupport.SaveManifestAsync(mod, stage, token, Guid.Parse(id));
                await session.MarkModAsync(mod, stage, token);
            }
            session.Result.ModIds.Add(mod.SourceId, Guid.Parse(id));
            progress?.Report(new(++completed, total, "处理模组完成", mod.Manifest.Name));
        }
        settings["StorageDirectory"] = Directory.GetParent(plan.TargetModsPath)!.FullName;
        settings["DeployBottomToTop"] = plan.TargetDeploymentOrder == DeploymentOrder.BottomToTop;
        settings["Separators"] ??= new JsonArray(); settings["Tags"] ??= new JsonArray();
        var separators = settings["Separators"]!.AsArray();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stagedDb, Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(token);
            using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    PRAGMA journal_mode=DELETE;
                    CREATE TABLE IF NOT EXISTS enabled_mods(Guid TEXT PRIMARY KEY NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1,Toggled TEXT NOT NULL DEFAULT '[]',Selected TEXT NOT NULL DEFAULT '[]',GroupId TEXT,TagIds TEXT,SortOrder INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE IF NOT EXISTS mod_groups(Id TEXT PRIMARY KEY NOT NULL,Name TEXT NOT NULL,DisplayIndex INTEGER NOT NULL DEFAULT 0,ModGuids TEXT NOT NULL DEFAULT '[]',CreatedAtUtc TEXT NOT NULL DEFAULT '');
                    CREATE TABLE IF NOT EXISTS group_enabled_mods(GroupId TEXT NOT NULL,Guid TEXT NOT NULL,Enabled INTEGER NOT NULL DEFAULT 1,Toggled TEXT NOT NULL DEFAULT '[]',Selected TEXT NOT NULL DEFAULT '[]',SortOrder INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(GroupId,Guid));
                    """;
                await create.ExecuteNonQueryAsync(token);
            }
            using var transaction = connection.BeginTransaction();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction; query.CommandText = "SELECT Name FROM mod_groups";
                using var reader = await query.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) names.Add(reader.GetString(0));
            }
            var tags = settings["Tags"]!.AsArray();
            foreach (var mod in plan.Mods)
            {
                var tagIds = new List<string>();
                foreach (var tag in mod.Tags)
                {
                    var existing = tags.FirstOrDefault(item => item?["name"]?.GetValue<string>() == tag);
                    var tagId = existing?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
                    if (existing == null) tags.Add(new JsonObject { ["id"] = tagId, ["name"] = tag, ["color"] = "#808080" });
                    tagIds.Add(tagId);
                }
                await ExecuteAsync(connection, transaction, "INSERT OR IGNORE INTO enabled_mods(Guid,Enabled,Toggled,Selected,SortOrder,TagIds) VALUES(@id,0,'[]','[]',@order,@tags)", token,
                    ("@id", nativeIds[mod.SourceId]), ("@order", completed++), ("@tags", JsonSerializer.Serialize(tagIds)));
            }
            completed = plan.Mods.Count;
            foreach (var sourceProfile in plan.Profiles)
            {
                token.ThrowIfCancellationRequested();
                var profile = ExternalExportSupport.MapProfile(sourceProfile, plan);
                var id = ExternalExportSupport.ProfileId(sourceProfile, plan);
                using var check = connection.CreateCommand(); check.Transaction = transaction;
                check.CommandText = "SELECT COUNT(*) FROM mod_groups WHERE Id=@id"; check.Parameters.AddWithValue("@id", id);
                if (Convert.ToInt32(await check.ExecuteScalarAsync(token)) > 0) session.Result.ReusedProfiles++;
                else
                {
                    var name = ExternalExportSupport.UniqueName(profile.Name, names);
                    await ExecuteAsync(connection, transaction, "INSERT INTO mod_groups(Id,Name,DisplayIndex,ModGuids) VALUES(@id,@name,(SELECT COALESCE(MAX(DisplayIndex),-1)+1 FROM mod_groups),@members)", token,
                        ("@id", id), ("@name", name), ("@members", JsonSerializer.Serialize(profile.Mods.Select(member => nativeIds[member.SourceModId]))));
                    foreach (var (member, index) in profile.Mods.Select((member, index) => (member, index)))
                    {
                        var toggled = JsonSerializer.Serialize(member.Options.OptionToggles ?? []);
                        var selected = JsonSerializer.Serialize(member.Options.LegacySelectedIndex.HasValue ? new[] { member.Options.LegacySelectedIndex.Value }
                            : (IEnumerable<int>)(member.Options.OptionSelections ?? []));
                        await ExecuteAsync(connection, transaction, "INSERT INTO group_enabled_mods(GroupId,Guid,Enabled,Toggled,Selected,SortOrder) VALUES(@profile,@mod,@on,@toggles,@selected,@order)", token,
                            ("@profile", id), ("@mod", nativeIds[member.SourceModId]), ("@on", member.Enabled), ("@toggles", toggled), ("@selected", selected), ("@order", index));
                    }
                    foreach (var entry in ExternalExportSupport.Entries(profile).Where(entry => entry.Separator != null))
                    {
                        var separator = entry.Separator!;
                        var members = profile.Mods.Skip(separator.Order).Take(separator.MemberCount ?? 0).Select(member => nativeIds[member.SourceModId]).ToList();
                        separators.Add(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = separator.Name, ["displayIndex"] = separator.Order + separators.Count,
                            ["isExpanded"] = !separator.IsCollapsed, ["color"] = separator.Color, ["modGuids"] = JsonSerializer.SerializeToNode(members),
                            ["hd2mimntProfile"] = id, ["hd2mimntMemberCount"] = separator.MemberCount ?? 0 });
                    }
                    session.Result.ImportedProfiles++;
                }
                session.Result.ProfileIds.Add(sourceProfile.SourceId, Guid.Parse(id));
                if (sourceProfile.WasActive)
                {
                    session.Result.SourceActiveProfileId = Guid.Parse(id);
                    await ExecuteAsync(connection, transaction, "UPDATE mod_groups SET DisplayIndex=(SELECT COALESCE(MIN(DisplayIndex),0)-1 FROM mod_groups) WHERE Id=@id", token, ("@id", id));
                    foreach (var member in profile.Mods)
                        await ExecuteAsync(connection, transaction, "UPDATE enabled_mods SET Enabled=@on,Toggled=@toggles,Selected=@selected,GroupId=@group WHERE Guid=@id", token,
                            ("@on", member.Enabled), ("@toggles", JsonSerializer.Serialize(member.Options.OptionToggles ?? [])),
                            ("@selected", JsonSerializer.Serialize(member.Options.LegacySelectedIndex.HasValue ? new[] { member.Options.LegacySelectedIndex.Value } : (IEnumerable<int>)(member.Options.OptionSelections ?? []))),
                            ("@group", id), ("@id", nativeIds[member.SourceModId]));
                }
                progress?.Report(new(++completed, total, "准备配置完成", profile.Name));
            }
            await transaction.CommitAsync(token);
        }
        if (settings["UseDeploymentOrder"]?.GetValue<bool>() == true)
        {
            settings["DeploymentOrderGuids"] ??= new JsonArray();
            var order = settings["DeploymentOrderGuids"]!.AsArray();
            var preferred = plan.Profiles.FirstOrDefault(profile => profile.WasActive) ?? plan.Profiles.FirstOrDefault();
            var members = preferred == null ? plan.Mods.Select(mod => mod.SourceId) : ExternalExportSupport.MapProfile(preferred, plan).Mods.Select(member => member.SourceModId);
            foreach (var member in members)
                if (!order.Any(item => item?.GetValue<string>() == nativeIds[member])) order.Add(nativeIds[member]);
        }
        await File.WriteAllTextAsync(session.AddFile(SettingsPath(plan.Source)), settings.ToJsonString(ExternalExportSupport.JsonOptions), token);
        progress?.Report(new(completed, total, "保存迁移结果", ""));
        await session.CommitAsync(token);
        session.Result.Issues.AddRange(plan.Issues);
        return session.Result;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken token,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(token);
    }
}
