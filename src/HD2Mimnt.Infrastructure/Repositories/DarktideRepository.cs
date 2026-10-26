using System.Text.Json;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

public sealed class DarktideRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly HD2MimntDbContext _context;

    public DarktideRepository(HD2MimntDbContext context) => _context = context;

    public async Task<IReadOnlyList<DarktideLibraryEntry>> GetLibraryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _context.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT InternalName, DisplayName, Version, Author, Description, ArchivePath, ArchiveOriginalName,
                   ArchiveSha256, ArchiveSize, DependenciesJson, ImportedAt, UpdatedAt
            FROM DarktideLibrary ORDER BY InternalName COLLATE NOCASE
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DarktideLibraryEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DarktideLibraryEntry(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetInt64(8), DeserializeList(reader.GetString(9)),
                DateTime.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    public async Task UpsertLibraryAsync(DarktideLibraryEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = await _context.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DarktideLibrary
                (InternalName, DisplayName, Version, Author, Description, ArchivePath, ArchiveOriginalName,
                 ArchiveSha256, ArchiveSize, DependenciesJson, ImportedAt, UpdatedAt)
            VALUES ($name, $display, $version, $author, $description, $archivePath, $originalName,
                    $sha256, $size, $dependencies, $importedAt, $updatedAt)
            ON CONFLICT(InternalName) DO UPDATE SET
                DisplayName = excluded.DisplayName, Version = excluded.Version, Author = excluded.Author,
                Description = excluded.Description, ArchivePath = excluded.ArchivePath,
                ArchiveOriginalName = excluded.ArchiveOriginalName, ArchiveSha256 = excluded.ArchiveSha256,
                ArchiveSize = excluded.ArchiveSize, DependenciesJson = excluded.DependenciesJson,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("$name", entry.InternalName);
        command.Parameters.AddWithValue("$display", entry.DisplayName);
        command.Parameters.AddWithValue("$version", entry.Version);
        command.Parameters.AddWithValue("$author", entry.Author);
        command.Parameters.AddWithValue("$description", entry.Description);
        command.Parameters.AddWithValue("$archivePath", entry.ArchivePath);
        command.Parameters.AddWithValue("$originalName", entry.ArchiveOriginalName);
        command.Parameters.AddWithValue("$sha256", entry.ArchiveSha256);
        command.Parameters.AddWithValue("$size", entry.ArchiveSize);
        command.Parameters.AddWithValue("$dependencies", JsonSerializer.Serialize(entry.RequiredDependencies, JsonOptions));
        command.Parameters.AddWithValue("$importedAt", entry.ImportedAt.ToString("o"));
        command.Parameters.AddWithValue("$updatedAt", entry.UpdatedAt.ToString("o"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteLibraryAsync(string internalName, CancellationToken cancellationToken = default)
    {
        await using var connection = await _context.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DarktideLibrary WHERE InternalName = $name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", internalName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DarktideProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _context.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name, CreatedAt, ModifiedAt, IsActive, EntriesJson FROM DarktideProfiles ORDER BY Name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DarktideProfile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DarktideProfile(reader.GetString(0),
                DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetInt32(3) != 0, DeserializeEntries(reader.GetString(4))));
        }
        return result;
    }

    public async Task UpsertProfileAsync(DarktideProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = await _context.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (profile.IsActive)
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "UPDATE DarktideProfiles SET IsActive = 0";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO DarktideProfiles (Name, CreatedAt, ModifiedAt, IsActive, EntriesJson)
            VALUES ($name, $created, $modified, $active, $entries)
            ON CONFLICT(Name) DO UPDATE SET ModifiedAt = excluded.ModifiedAt,
                IsActive = excluded.IsActive, EntriesJson = excluded.EntriesJson
            """;
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$created", profile.CreatedAt.ToString("o"));
        command.Parameters.AddWithValue("$modified", profile.ModifiedAt.ToString("o"));
        command.Parameters.AddWithValue("$active", profile.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$entries", JsonSerializer.Serialize(profile.Entries, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteProfileAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await _context.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DarktideProfiles WHERE Name = $name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DarktideModSeparator>> GetModSeparatorsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _context.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, [Order], MemberCount, IsCollapsed, Color
            FROM DarktideModSeparators ORDER BY [Order], Name COLLATE NOCASE
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DarktideModSeparator>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DarktideModSeparator(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetInt32(4) != 0,
                reader.GetString(5)));
        }
        return result;
    }

    /// <summary>全量替换：分隔符的 Order 与 MemberCount 互相依赖边界，逐条更新会留下中途不一致的分组。</summary>
    public async Task SaveModSeparatorsAsync(IReadOnlyList<DarktideModSeparator> separators,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _context.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM DarktideModSeparators";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var separator in separators)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO DarktideModSeparators (Id, Name, [Order], MemberCount, IsCollapsed, Color)
                VALUES ($id, $name, $order, $members, $collapsed, $color)
                """;
            command.Parameters.AddWithValue("$id", separator.Id.ToString());
            command.Parameters.AddWithValue("$name", separator.Name);
            command.Parameters.AddWithValue("$order", separator.Order);
            command.Parameters.AddWithValue("$members", (object?)separator.MemberCount ?? DBNull.Value);
            command.Parameters.AddWithValue("$collapsed", separator.IsCollapsed ? 1 : 0);
            command.Parameters.AddWithValue("$color", separator.Color);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static IReadOnlyList<string> DeserializeList(string json)
        => JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();

    private static IReadOnlyList<DarktideProfileEntry> DeserializeEntries(string json)
        => JsonSerializer.Deserialize<List<DarktideProfileEntry>>(json, JsonOptions) ?? new List<DarktideProfileEntry>();
}
