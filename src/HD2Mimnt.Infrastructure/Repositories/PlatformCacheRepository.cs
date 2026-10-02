using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

public class PlatformCacheRepository : IPlatformCacheRepository
{
    private readonly HD2MimntDbContext _context;

    public PlatformCacheRepository(HD2MimntDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<PlatformCache?> GetAsync(string platformId, string platformModId, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PlatformCaches WHERE PlatformId = @PlatformId AND PlatformModId = @PlatformModId";
        command.Parameters.AddWithValue("@PlatformId", platformId);
        command.Parameters.AddWithValue("@PlatformModId", platformModId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return MapToCache(reader);
        }

        return null;
    }

    public async Task AddAsync(PlatformCache cache, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO PlatformCaches (Id, PlatformId, PlatformModId, CachedData, CachedAt, ExpiresAt, Etag)
            VALUES (@Id, @PlatformId, @PlatformModId, @CachedData, @CachedAt, @ExpiresAt, @Etag)";

        command.Parameters.AddWithValue("@Id", cache.Id.ToString());
        command.Parameters.AddWithValue("@PlatformId", cache.PlatformId);
        command.Parameters.AddWithValue("@PlatformModId", cache.PlatformModId);
        command.Parameters.AddWithValue("@CachedData", cache.CachedData);
        command.Parameters.AddWithValue("@CachedAt", cache.CachedAt.ToString("o"));
        command.Parameters.AddWithValue("@ExpiresAt", cache.ExpiresAt.ToString("o"));
        command.Parameters.AddWithValue("@Etag", cache.Etag);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(PlatformCache cache, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE PlatformCaches
            SET PlatformId = @PlatformId,
                PlatformModId = @PlatformModId,
                CachedData = @CachedData,
                CachedAt = @CachedAt,
                ExpiresAt = @ExpiresAt,
                Etag = @Etag
            WHERE Id = @Id";

        command.Parameters.AddWithValue("@Id", cache.Id.ToString());
        command.Parameters.AddWithValue("@PlatformId", cache.PlatformId);
        command.Parameters.AddWithValue("@PlatformModId", cache.PlatformModId);
        command.Parameters.AddWithValue("@CachedData", cache.CachedData);
        command.Parameters.AddWithValue("@CachedAt", cache.CachedAt.ToString("o"));
        command.Parameters.AddWithValue("@ExpiresAt", cache.ExpiresAt.ToString("o"));
        command.Parameters.AddWithValue("@Etag", cache.Etag);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PlatformCaches WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<PlatformCache>> GetExpiredAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PlatformCaches WHERE ExpiresAt < @Now";
        command.Parameters.AddWithValue("@Now", now.ToString("o"));

        var caches = new List<PlatformCache>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            caches.Add(MapToCache(reader));
        }

        return caches;
    }

    public async Task DeleteExpiredAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PlatformCaches WHERE ExpiresAt < @Now";
        command.Parameters.AddWithValue("@Now", now.ToString("o"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> GetCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PlatformCaches";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task ClearAsync(string? platformId = null, CancellationToken cancellationToken = default)
    {
        var connection = await _context.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        if (platformId == null)
        {
            command.CommandText = "DELETE FROM PlatformCaches";
        }
        else
        {
            command.CommandText = "DELETE FROM PlatformCaches WHERE PlatformId = @PlatformId";
            command.Parameters.AddWithValue("@PlatformId", platformId);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PlatformCache MapToCache(SqliteDataReader reader)
    {
        return new PlatformCache
        {
            Id = Guid.Parse(reader.GetString(0)),
            PlatformId = reader.GetString(1),
            PlatformModId = reader.GetString(2),
            CachedData = reader.GetString(3),
            CachedAt = DateTime.Parse(reader.GetString(4)),
            ExpiresAt = DateTime.Parse(reader.GetString(5)),
            Etag = reader.GetString(6)
        };
    }
}
