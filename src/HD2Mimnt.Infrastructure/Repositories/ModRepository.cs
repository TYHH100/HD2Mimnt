using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// Mod Repository 实现
/// </summary>
public class ModRepository : IModRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public ModRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Mod?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Mods WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToMod(reader);
        }

        return null;
    }

    public async Task<IEnumerable<Mod>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        var distinctIds = ids.Distinct().ToArray();
        if (distinctIds.Length == 0) return Array.Empty<Mod>();

        var mods = new List<Mod>(distinctIds.Length);
        foreach (var chunk in distinctIds.Chunk(500))
        {
            using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
            using var command = connection.CreateCommand();
            var parameters = new List<string>(chunk.Length);
            for (var index = 0; index < chunk.Length; index++)
            {
                var parameterName = $"@Id{index}";
                parameters.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, chunk[index].ToString());
            }

            command.CommandText = $"SELECT * FROM Mods WHERE Id IN ({string.Join(",", parameters)})";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                mods.Add(MapToMod(reader));
            }
        }

        return mods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IEnumerable<Mod>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Mods ORDER BY Name";

        var mods = new List<Mod>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mods.Add(MapToMod(reader));
        }

        return mods;
    }

    /// <summary>
    /// 判重候选：内容身份命中的行，以及身份尚未持久化、需要补算指纹的旧行。
    /// 每个条件都用单独的索引查找再合并，混合 OR 会让 SQLite 放弃索引退回全表扫描。
    /// </summary>
    public async Task<IEnumerable<Mod>> FindContentCandidatesAsync(string hash, string contentFingerprint,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        var branches = new List<string>();
        if (!string.IsNullOrWhiteSpace(hash))
        {
            branches.Add("SELECT * FROM Mods WHERE Hash = @Hash");
            command.Parameters.AddWithValue("@Hash", hash);
        }
        if (!string.IsNullOrWhiteSpace(contentFingerprint))
        {
            branches.Add("SELECT * FROM Mods WHERE ContentFingerprint = @Fingerprint");
            command.Parameters.AddWithValue("@Fingerprint", contentFingerprint);
        }
        // 旧库行没有持久化身份，SQL 无法按值命中，必须补算后才能判重；已迁移的库里这些分支各返回零行。
        branches.Add("SELECT * FROM Mods WHERE Hash IS NULL");
        branches.Add("SELECT * FROM Mods WHERE Hash = ''");
        branches.Add("SELECT * FROM Mods WHERE ContentFingerprint IS NULL");
        branches.Add("SELECT * FROM Mods WHERE ContentFingerprint = ''");
        command.CommandText = string.Join(" UNION ALL ", branches);

        var mods = new List<Mod>();
        var seen = new HashSet<Guid>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var mod = MapToMod(reader);
            if (seen.Add(mod.Id)) mods.Add(mod);
        }

        return mods;
    }

    /// <summary>清单 GUID 直接存在 ManifestJson 列，交给 SQLite JSON 函数过滤，避免逐行反序列化清单。</summary>
    public async Task<IEnumerable<Mod>> FindByManifestGuidAsync(Guid manifestGuid, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT * FROM Mods
            WHERE ManifestJson IS NOT NULL AND ManifestJson <> '' AND json_valid(ManifestJson)
              AND json_extract(ManifestJson, '$.Guid') = @Guid COLLATE NOCASE
            ORDER BY ImportedAt";
        command.Parameters.AddWithValue("@Guid", manifestGuid.ToString());

        var mods = new List<Mod>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mods.Add(MapToMod(reader));
        }

        return mods;
    }

    public async Task<IEnumerable<Mod>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        // 使用 FTS5 全文搜索索引以提高性能
        command.CommandText = @"
            SELECT m.* FROM Mods m
            INNER JOIN ModsFTS fts ON m.rowid = fts.rowid
            WHERE ModsFTS MATCH @Keyword
            ORDER BY m.Name";
        command.Parameters.AddWithValue("@Keyword", keyword);

        var mods = new List<Mod>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mods.Add(MapToMod(reader));
        }

        return mods;
    }

    public async Task<IEnumerable<Mod>> GetByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Mods WHERE Tags LIKE @Tag ORDER BY Name";
        command.Parameters.AddWithValue("@Tag", $"%{tag}%");

        var mods = new List<Mod>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mods.Add(MapToMod(reader));
        }

        return mods;
    }

    public async Task<IEnumerable<Mod>> GetByCategoryAsync(ModCategory category, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Mods WHERE Category = @Category ORDER BY Name";
        command.Parameters.AddWithValue("@Category", (int)category);

        var mods = new List<Mod>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mods.Add(MapToMod(reader));
        }

        return mods;
    }

    public async Task<IEnumerable<Mod>> GetByStatusAsync(ModStatus status, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Mods WHERE Status = @Status ORDER BY Name";
        command.Parameters.AddWithValue("@Status", (int)status);

        var mods = new List<Mod>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mods.Add(MapToMod(reader));
        }

        return mods;
    }

    public async Task<long> GetTotalSizeAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(Size), 0) FROM Mods";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task<int> GetCountAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Mods";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task AddAsync(Mod mod, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Mods (Id, Name, Version, Author, Description, Path, Category, Tags,
                            WorkflowType, ImportedAt, ModifiedAt, Size, Hash, ContentFingerprint, ThumbnailPath, Source, Status, Notice,
                            ManifestJson, ContainsScripts, NexusModId, ResourceTypeIds, BslRole, BslDetectionVersion,
                            PlatformId, PlatformModId, PlatformVersion, LastUpdateCheck, PlatformLinkJson)
            VALUES (@Id, @Name, @Version, @Author, @Description, @Path, @Category, @Tags,
                    @WorkflowType, @ImportedAt, @ModifiedAt, @Size, @Hash, @ContentFingerprint, @ThumbnailPath, @Source, @Status, @Notice,
                    @ManifestJson, @ContainsScripts, @NexusModId, @ResourceTypeIds, @BslRole, @BslDetectionVersion,
                    @PlatformId, @PlatformModId, @PlatformVersion, @LastUpdateCheck, @PlatformLinkJson)";

        AddModParameters(command, mod);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(Mod mod, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Mods
            SET Name = @Name, Version = @Version, Author = @Author, Description = @Description,
                Path = @Path, Category = @Category, Tags = @Tags, WorkflowType = @WorkflowType,
                ModifiedAt = @ModifiedAt, Size = @Size, Hash = @Hash, ContentFingerprint = @ContentFingerprint, ThumbnailPath = @ThumbnailPath,
                Source = @Source, Status = @Status, Notice = @Notice, ManifestJson = @ManifestJson,
                ContainsScripts = @ContainsScripts, NexusModId = @NexusModId, ResourceTypeIds = @ResourceTypeIds,
                BslRole = @BslRole, BslDetectionVersion = @BslDetectionVersion,
                PlatformId = @PlatformId, PlatformModId = @PlatformModId, PlatformVersion = @PlatformVersion,
                LastUpdateCheck = @LastUpdateCheck, PlatformLinkJson = @PlatformLinkJson
            WHERE Id = @Id";

        AddModParameters(command, mod);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetNoticeAsync(Guid id, ModNoticeKind notice, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Mods SET Notice = @Notice WHERE Id = @Id";
        command.Parameters.AddWithValue("@Notice", (int)notice);
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetContentFingerprintAsync(Guid id, string fingerprint, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Mods SET ContentFingerprint = @ContentFingerprint WHERE Id = @Id";
        command.Parameters.AddWithValue("@ContentFingerprint", fingerprint);
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Mods WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Mods WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result) > 0;
    }

    internal static Mod MapToMod(SqliteDataReader reader)
    {
        var tagsJson = reader.GetString(reader.GetOrdinal("Tags"));
        var tags = string.IsNullOrEmpty(tagsJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(tagsJson) ?? new List<string>();

        var manifestJson = reader.IsDBNull(reader.GetOrdinal("ManifestJson"))
            ? null
            : reader.GetString(reader.GetOrdinal("ManifestJson"));

        var manifest = string.IsNullOrEmpty(manifestJson)
            ? null
            : JsonSerializer.Deserialize<ModManifest>(manifestJson);

        return new Mod
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Version = reader.GetString(reader.GetOrdinal("Version")),
            Author = reader.IsDBNull(reader.GetOrdinal("Author")) ? string.Empty : reader.GetString(reader.GetOrdinal("Author")),
            Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? string.Empty : reader.GetString(reader.GetOrdinal("Description")),
            Path = reader.GetString(reader.GetOrdinal("Path")),
            Category = (ModCategory)reader.GetInt32(reader.GetOrdinal("Category")),
            BslRole = reader.IsDBNull(reader.GetOrdinal("BslRole")) ? null : (BslModRole)reader.GetInt32(reader.GetOrdinal("BslRole")),
            BslDetectionVersion = reader.GetInt32(reader.GetOrdinal("BslDetectionVersion")),
            ResourceTypeIds = reader.IsDBNull(reader.GetOrdinal("ResourceTypeIds")) ? null
                : (JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("ResourceTypeIds"))) ?? new())
                    .Select(id => Convert.ToUInt64(id, 16)).Distinct().Order().ToList(),
            Tags = tags,
            WorkflowType = reader.IsDBNull(reader.GetOrdinal("WorkflowType")) ? string.Empty : reader.GetString(reader.GetOrdinal("WorkflowType")),
            ImportedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("ImportedAt"))),
            ModifiedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("ModifiedAt"))),
            Size = reader.GetInt64(reader.GetOrdinal("Size")),
            Hash = reader.IsDBNull(reader.GetOrdinal("Hash")) ? string.Empty : reader.GetString(reader.GetOrdinal("Hash")),
            ContentFingerprint = ReadNullableString(reader, "ContentFingerprint") ?? string.Empty,
            ThumbnailPath = reader.IsDBNull(reader.GetOrdinal("ThumbnailPath")) ? string.Empty : reader.GetString(reader.GetOrdinal("ThumbnailPath")),
            Source = (ModSource)reader.GetInt32(reader.GetOrdinal("Source")),
            Status = (ModStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            Notice = (ModNoticeKind)reader.GetInt32(reader.GetOrdinal("Notice")),
            Manifest = manifest,
            ContainsScripts = reader.GetInt32(reader.GetOrdinal("ContainsScripts")) == 1,
            NexusModId = reader.IsDBNull(reader.GetOrdinal("NexusModId")) ? null : reader.GetInt32(reader.GetOrdinal("NexusModId")),
            PlatformId = ReadNullableString(reader, "PlatformId"),
            PlatformModId = ReadNullableString(reader, "PlatformModId"),
            PlatformVersion = ReadNullableString(reader, "PlatformVersion"),
            LastUpdateCheck = ReadNullableString(reader, "LastUpdateCheck") is { } time
                ? DateTime.Parse(time, null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
            PlatformLink = ReadNullableString(reader, "PlatformLinkJson") is { } link
                ? JsonSerializer.Deserialize<ModPlatformLink>(link) : null
        };
    }

    private static string? ReadNullableString(SqliteDataReader reader, string column)
        => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

    private static void AddModParameters(SqliteCommand command, Mod mod)
    {
        command.Parameters.AddWithValue("@Id", mod.Id.ToString());
        command.Parameters.AddWithValue("@PlatformId", (object?)mod.PlatformId ?? DBNull.Value);
        command.Parameters.AddWithValue("@PlatformModId", (object?)mod.PlatformModId ?? DBNull.Value);
        command.Parameters.AddWithValue("@PlatformVersion", (object?)mod.PlatformVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("@LastUpdateCheck", (object?)mod.LastUpdateCheck?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("@PlatformLinkJson", mod.PlatformLink is { } link
            ? JsonSerializer.Serialize(link) : DBNull.Value);
        command.Parameters.AddWithValue("@Name", mod.Name);
        command.Parameters.AddWithValue("@Version", mod.Version);
        command.Parameters.AddWithValue("@Author", mod.Author);
        command.Parameters.AddWithValue("@Description", mod.Description);
        command.Parameters.AddWithValue("@Path", mod.Path);
        command.Parameters.AddWithValue("@Category", (int)mod.Category);
        command.Parameters.AddWithValue("@BslRole", mod.BslRole.HasValue ? (int)mod.BslRole.Value : DBNull.Value);
        command.Parameters.AddWithValue("@BslDetectionVersion", mod.BslDetectionVersion);
        command.Parameters.AddWithValue("@Tags", JsonSerializer.Serialize(mod.Tags));
        command.Parameters.AddWithValue("@ResourceTypeIds", mod.ResourceTypeIds is null ? DBNull.Value
            : JsonSerializer.Serialize(mod.ResourceTypeIds.Distinct().Order().Select(id => id.ToString("X16"))));
        command.Parameters.AddWithValue("@WorkflowType", mod.WorkflowType);
        command.Parameters.AddWithValue("@ImportedAt", mod.ImportedAt.ToString("O"));
        command.Parameters.AddWithValue("@ModifiedAt", mod.ModifiedAt.ToString("O"));
        command.Parameters.AddWithValue("@Size", mod.Size);
        command.Parameters.AddWithValue("@Hash", mod.Hash);
        command.Parameters.AddWithValue("@ContentFingerprint",
            string.IsNullOrWhiteSpace(mod.ContentFingerprint) ? DBNull.Value : mod.ContentFingerprint);
        command.Parameters.AddWithValue("@ThumbnailPath", mod.ThumbnailPath);
        command.Parameters.AddWithValue("@Source", (int)mod.Source);
        command.Parameters.AddWithValue("@Status", (int)mod.Status);
        command.Parameters.AddWithValue("@Notice", (int)mod.Notice);
        command.Parameters.AddWithValue("@ManifestJson", mod.Manifest != null ? JsonSerializer.Serialize(mod.Manifest) : DBNull.Value);
        command.Parameters.AddWithValue("@ContainsScripts", mod.ContainsScripts ? 1 : 0);
        command.Parameters.AddWithValue("@NexusModId", mod.NexusModId.HasValue ? mod.NexusModId.Value : DBNull.Value);
    }
}
