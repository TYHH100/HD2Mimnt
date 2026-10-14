using System.Text.Json;
using HD2Mimnt.Core.Diagnostics;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>短连接、串行写入，所有快照（含正常基准）合计最多保留最近 50 条。</summary>
public sealed class CrashHistoryRepository(string databasePath) : ICrashHistoryRepository
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());

    public async Task SaveAsync(CrashDeploymentSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var connection = Connection(); connection.Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO CrashDeploymentSnapshots(SessionId, StartedUtc, KnownGood, SnapshotJson)
                    VALUES ($id, $time, $good, $json)
                    ON CONFLICT(SessionId) DO UPDATE SET SnapshotJson=excluded.SnapshotJson;
                    DELETE FROM CrashDeploymentSnapshots WHERE SessionId NOT IN
                    (SELECT SessionId FROM CrashDeploymentSnapshots ORDER BY StartedUtc DESC, SessionId DESC LIMIT 50);
                    """;
                command.Parameters.AddWithValue("$id", snapshot.SessionId.ToString());
                command.Parameters.AddWithValue("$time", snapshot.StartedUtc.ToString("O"));
                command.Parameters.AddWithValue("$good", snapshot.KnownGood ? 1 : 0);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot));
                command.ExecuteNonQuery(); transaction.Commit();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<CrashDeploymentSnapshot>> GetRecentAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<CrashDeploymentSnapshot>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Connection(); connection.Open();
            using var command = connection.CreateCommand();
            // 同时收敛旧版本已经超过上限的记录，不必等下一次部署。
            command.CommandText = """
                DELETE FROM CrashDeploymentSnapshots WHERE SessionId NOT IN
                (SELECT SessionId FROM CrashDeploymentSnapshots ORDER BY StartedUtc DESC, SessionId DESC LIMIT 50);
                SELECT SnapshotJson, KnownGood FROM CrashDeploymentSnapshots ORDER BY StartedUtc DESC, SessionId DESC;
                """;
            using var reader = command.ExecuteReader();
            var result = new List<CrashDeploymentSnapshot>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = JsonSerializer.Deserialize<CrashDeploymentSnapshot>(reader.GetString(0));
                if (snapshot != null) result.Add(snapshot with { KnownGood = reader.GetBoolean(1) });
            }
            return result;
        }, cancellationToken);

    public async Task MarkKnownGoodAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var connection = Connection(); connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE CrashDeploymentSnapshots SET KnownGood=1 WHERE SessionId=$id AND json_extract(SnapshotJson, '$.Status')='Completed'";
                command.Parameters.AddWithValue("$id", sessionId.ToString());
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Only a completed deployment can be marked as working.");
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
