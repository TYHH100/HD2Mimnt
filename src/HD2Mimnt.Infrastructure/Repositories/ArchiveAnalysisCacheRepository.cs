using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// Repository for archive analysis cache
/// </summary>
public class ArchiveAnalysisCacheRepository : IArchiveAnalysisCacheRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public ArchiveAnalysisCacheRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ArchiveAnalysisCache?> GetAsync(string filePath, long fileSize, DateTime lastModified, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT * FROM ArchiveAnalysisCache
            WHERE FilePath = @FilePath
              AND FileSize = @FileSize
              AND LastModified = @LastModified";

        command.Parameters.AddWithValue("@FilePath", filePath);
        command.Parameters.AddWithValue("@FileSize", fileSize);
        command.Parameters.AddWithValue("@LastModified", lastModified);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToCache(reader);
        }

        return null;
    }

    public async Task SaveAsync(ArchiveAnalysisCache cache, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        // Check if exists
        var existing = await GetAsync(cache.FilePath, cache.FileSize, cache.LastModified, cancellationToken);

        if (existing != null)
        {
            // Update
            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE ArchiveAnalysisCache
                SET ContentHash = @ContentHash,
                    ArchiveTreeJson = @ArchiveTreeJson,
                    DetectionResultsJson = @DetectionResultsJson,
                    LastAccessed = @LastAccessed,
                    AccessCount = @AccessCount
                WHERE Id = @Id";

            command.Parameters.AddWithValue("@Id", existing.Id.ToString());
            command.Parameters.AddWithValue("@ContentHash", cache.ContentHash);
            command.Parameters.AddWithValue("@ArchiveTreeJson", cache.ArchiveTreeJson);
            command.Parameters.AddWithValue("@DetectionResultsJson", cache.DetectionResultsJson);
            command.Parameters.AddWithValue("@LastAccessed", cache.LastAccessed);
            command.Parameters.AddWithValue("@AccessCount", cache.AccessCount);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            // Insert
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO ArchiveAnalysisCache (Id, FilePath, FileSize, LastModified, ContentHash, ArchiveTreeJson, DetectionResultsJson, CachedAt, LastAccessed, AccessCount)
                VALUES (@Id, @FilePath, @FileSize, @LastModified, @ContentHash, @ArchiveTreeJson, @DetectionResultsJson, @CachedAt, @LastAccessed, @AccessCount)";

            command.Parameters.AddWithValue("@Id", cache.Id.ToString());
            command.Parameters.AddWithValue("@FilePath", cache.FilePath);
            command.Parameters.AddWithValue("@FileSize", cache.FileSize);
            command.Parameters.AddWithValue("@LastModified", cache.LastModified);
            command.Parameters.AddWithValue("@ContentHash", cache.ContentHash);
            command.Parameters.AddWithValue("@ArchiveTreeJson", cache.ArchiveTreeJson);
            command.Parameters.AddWithValue("@DetectionResultsJson", cache.DetectionResultsJson);
            command.Parameters.AddWithValue("@CachedAt", cache.CachedAt);
            command.Parameters.AddWithValue("@LastAccessed", cache.LastAccessed);
            command.Parameters.AddWithValue("@AccessCount", cache.AccessCount);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpdateAccessAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE ArchiveAnalysisCache
            SET LastAccessed = @LastAccessed,
                AccessCount = AccessCount + 1
            WHERE Id = @Id";

        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@LastAccessed", DateTime.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteExpiredAsync(int expirationDays, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        var expirationDate = DateTime.UtcNow.AddDays(-expirationDays);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ArchiveAnalysisCache WHERE CachedAt < @ExpirationDate";
        command.Parameters.AddWithValue("@ExpirationDate", expirationDate);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ArchiveAnalysisCache";

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private ArchiveAnalysisCache MapToCache(SqliteDataReader reader)
    {
        return new ArchiveAnalysisCache
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            FilePath = reader.GetString(reader.GetOrdinal("FilePath")),
            FileSize = reader.GetInt64(reader.GetOrdinal("FileSize")),
            LastModified = reader.GetDateTime(reader.GetOrdinal("LastModified")),
            ContentHash = reader.GetString(reader.GetOrdinal("ContentHash")),
            ArchiveTreeJson = reader.GetString(reader.GetOrdinal("ArchiveTreeJson")),
            DetectionResultsJson = reader.GetString(reader.GetOrdinal("DetectionResultsJson")),
            CachedAt = reader.GetDateTime(reader.GetOrdinal("CachedAt")),
            LastAccessed = reader.GetDateTime(reader.GetOrdinal("LastAccessed")),
            AccessCount = reader.GetInt32(reader.GetOrdinal("AccessCount"))
        };
    }
}
