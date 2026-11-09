using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>CompatibilityResults 表仓储：短连接读写，读取时顺带清理模组已删除的孤儿行。</summary>
public sealed class CompatibilityResultRepository(HD2MimntDbContext dbContext) : ICompatibilityResultStore
{
    public async Task UpsertAsync(IReadOnlyCollection<PersistedCompatibilityResult> results, CancellationToken cancellationToken = default)
    {
        if (results.Count == 0) return;
        using var connection = await dbContext.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO CompatibilityResults (ModId, Status, Reason, UnitCount, ComparedUnitCount, HasBlockingIssues, ReferenceSource, IssuesJson, CheckedAtUtc, ContentStamp)
                VALUES ($ModId, $Status, $Reason, $UnitCount, $ComparedUnitCount, $HasBlockingIssues, $ReferenceSource, $IssuesJson, $CheckedAtUtc, $ContentStamp)
                ON CONFLICT(ModId) DO UPDATE SET
                    Status=excluded.Status, Reason=excluded.Reason, UnitCount=excluded.UnitCount,
                    ComparedUnitCount=excluded.ComparedUnitCount, HasBlockingIssues=excluded.HasBlockingIssues,
                    ReferenceSource=excluded.ReferenceSource, IssuesJson=excluded.IssuesJson,
                    CheckedAtUtc=excluded.CheckedAtUtc, ContentStamp=excluded.ContentStamp
                """;
            command.Parameters.AddWithValue("$ModId", result.ModId.ToString());
            command.Parameters.AddWithValue("$Status", (int)result.Status);
            command.Parameters.AddWithValue("$Reason", (int)result.Reason);
            command.Parameters.AddWithValue("$UnitCount", result.UnitCount);
            command.Parameters.AddWithValue("$ComparedUnitCount", result.ComparedUnitCount);
            command.Parameters.AddWithValue("$HasBlockingIssues", result.HasBlockingIssues ? 1 : 0);
            command.Parameters.AddWithValue("$ReferenceSource", (object?)result.ReferenceSource ?? DBNull.Value);
            command.Parameters.AddWithValue("$IssuesJson", JsonSerializer.Serialize(result.Issues));
            command.Parameters.AddWithValue("$CheckedAtUtc", result.CheckedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$ContentStamp", result.ContentStamp);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<IReadOnlyList<PersistedCompatibilityResult>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await dbContext.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        // 先清理模组已删除的孤儿行再读取，返回结果不能包含即将被删除的行。
        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM CompatibilityResults WHERE ModId NOT IN (SELECT Id FROM Mods)";
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        var results = new List<PersistedCompatibilityResult>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT ModId, Status, Reason, UnitCount, ComparedUnitCount, HasBlockingIssues, ReferenceSource, IssuesJson, CheckedAtUtc, ContentStamp
                FROM CompatibilityResults
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new PersistedCompatibilityResult
                {
                    ModId = Guid.Parse(reader.GetString(0)),
                    Status = (ModCompatibilityStatus)reader.GetInt64(1),
                    Reason = (ModCompatibilityReason)reader.GetInt64(2),
                    UnitCount = (int)reader.GetInt64(3),
                    ComparedUnitCount = (int)reader.GetInt64(4),
                    HasBlockingIssues = reader.GetInt64(5) != 0,
                    ReferenceSource = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    Issues = JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [],
                    CheckedAtUtc = reader.GetDateTime(8).ToUniversalTime(),
                    ContentStamp = reader.GetString(9)
                });
            }
        }
        transaction.Commit();
        return results;
    }
}
