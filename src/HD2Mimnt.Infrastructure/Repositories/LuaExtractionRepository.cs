using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>LuaExtractionResults 表仓储：短连接读写，读取时顺带清理模组已删除的孤儿行。</summary>
public sealed class LuaExtractionRepository(HD2MimntDbContext dbContext) : ILuaExtractionStore
{
    public async Task UpsertAsync(LuaExtractionResult result, CancellationToken cancellationToken = default)
    {
        using var connection = await dbContext.OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LuaExtractionResults (ModId, ExtractedAtUtc, SourceCount, BytecodeCount, ContentStamp)
            VALUES ($ModId, $ExtractedAtUtc, $SourceCount, $BytecodeCount, $ContentStamp)
            ON CONFLICT(ModId) DO UPDATE SET
                ExtractedAtUtc=excluded.ExtractedAtUtc, SourceCount=excluded.SourceCount,
                BytecodeCount=excluded.BytecodeCount, ContentStamp=excluded.ContentStamp
            """;
        command.Parameters.AddWithValue("$ModId", result.ModId.ToString());
        command.Parameters.AddWithValue("$ExtractedAtUtc", result.ExtractedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$SourceCount", result.SourceCount);
        command.Parameters.AddWithValue("$BytecodeCount", result.BytecodeCount);
        command.Parameters.AddWithValue("$ContentStamp", result.ContentStamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LuaExtractionResult>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await dbContext.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        // 先清理模组已删除的孤儿行再读取，返回结果不能包含即将被删除的行。
        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM LuaExtractionResults WHERE ModId NOT IN (SELECT Id FROM Mods)";
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        var results = new List<LuaExtractionResult>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT ModId, ExtractedAtUtc, SourceCount, BytecodeCount, ContentStamp FROM LuaExtractionResults";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new LuaExtractionResult
                {
                    ModId = Guid.Parse(reader.GetString(0)),
                    ExtractedAtUtc = reader.GetDateTime(1).ToUniversalTime(),
                    SourceCount = (int)reader.GetInt64(2),
                    BytecodeCount = (int)reader.GetInt64(3),
                    ContentStamp = reader.GetString(4)
                });
            }
        }
        transaction.Commit();
        return results;
    }
}
