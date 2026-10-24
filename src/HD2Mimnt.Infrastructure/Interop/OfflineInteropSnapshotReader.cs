using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interop;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Interop;

/// <summary>使用独立只读连接和读事务，不在后台共享 UI Repository 的连接。</summary>
public sealed class OfflineInteropSnapshotReader(string databasePath) : IOfflineInteropSource
{
    public async Task<OfflineInteropData> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);

        async Task<List<T>> Read<T>(string sql, Func<SqliteDataReader, T> map)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            var rows = new List<T>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(map(reader));
            return rows;
        }

        var mods = await Read("SELECT * FROM Mods ORDER BY Id", ModRepository.MapToMod).ConfigureAwait(false);
        var profiles = await Read("SELECT * FROM Profiles ORDER BY IsDefault DESC, SortOrder, Id",
            ProfileRepository.MapToProfile).ConfigureAwait(false);
        var members = await Read("SELECT * FROM ProfileMods ORDER BY [Order], ModId", reader => new ProfileMod
        {
            ProfileId = Guid.Parse(reader.GetString(reader.GetOrdinal("ProfileId"))),
            ModId = Guid.Parse(reader.GetString(reader.GetOrdinal("ModId"))),
            Enabled = reader.GetBoolean(reader.GetOrdinal("Enabled")),
            Order = reader.GetInt32(reader.GetOrdinal("Order")),
            OptionsJson = reader.GetString(reader.GetOrdinal("OptionsJson"))
        }).ConfigureAwait(false);
        var byProfile = members.ToLookup(item => item.ProfileId);
        foreach (var profile in profiles) profile.Mods = byProfile[profile.Id].ToList();
        // 导出全部仍受追踪的会话，包括失败/取消后等待清理的残留文件。
        var deployments = await Read("SELECT * FROM DeploymentRecords ORDER BY StartTime, Id",
            DeploymentRepository.MapToDeploymentRecord).ConfigureAwait(false);
        var files = await Read("SELECT * FROM DeploymentFiles ORDER BY TargetPath",
            DeploymentRepository.MapToDeploymentFile).ConfigureAwait(false);
        var byDeployment = files.ToLookup(item => item.DeploymentId);
        foreach (var deployment in deployments) deployment.Files = byDeployment[deployment.Id].ToList();
        transaction.Commit();
        return new(mods, profiles, deployments);
    }
}
