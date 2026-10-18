using System.Diagnostics;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.PerformanceTests;

// 同一套真实数据库样本供服务压力测试和 Headless UI 压力测试复用。
public static class LargeLibraryWorkload
{
    public const int ModCount = 10_000;

    public static async Task<Guid[]> SeedAsync(HD2MimntDbContext db, string root, string? imageTemplate = null,
        int resourcesPerMod = 0)
    {
        var connection = await db.GetConnectionAsync();
        var ids = Enumerable.Range(0, ModCount).Select(_ => Guid.NewGuid()).ToArray();
        Directory.CreateDirectory(Path.Combine(root, "images"));
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Mods (Id,Name,Version,Author,Description,Path,Category,Tags,ResourceTypeIds,
                    WorkflowType,ImportedAt,ModifiedAt,Size,ThumbnailPath,BslRole,BslDetectionVersion)
                VALUES ($id,$name,'1.2.3',$author,$description,$path,$category,'["armor","performance"]',
                    '["CD4238C6A0C69E32"]','Legacy',$date,$date,10485760,$image,0,$detection)
                """;
            foreach (var key in new[] { "$id", "$name", "$author", "$description", "$path", "$category", "$date", "$image", "$detection" })
                command.Parameters.AddWithValue(key, "");
            command.Prepare();
            for (var i = 0; i < ids.Length; i++)
            {
                var modPath = Path.Combine(root, "mods", i.ToString("D5"));
                Directory.CreateDirectory(modPath);
                if (resourcesPerMod > 0)
                {
                    using var writer = new BinaryWriter(File.Create(Path.Combine(modPath, "aaaaaaaaaaaaaaaa.patch_0")));
                    writer.Write(0xF0000011u); writer.Write(1u); writer.Write((uint)resourcesPerMod);
                    writer.Write(new byte[60 + 32]);
                    var entry = new byte[80];
                    for (var resource = 0; resource < resourcesPerMod; resource++)
                    {
                        var resourceId = resource < resourcesPerMod / 10
                            ? (ulong)(i / 2 * resourcesPerMod + resource)
                            : (1UL << 63) + (ulong)(i * resourcesPerMod + resource);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry, resourceId);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8), 0xcd4238c6a0c69e32UL);
                        writer.Write(entry);
                    }
                }
                var image = imageTemplate == null ? "" : Path.Combine(root, "images", i.ToString("D5") + ".png");
                if (imageTemplate != null) File.Copy(imageTemplate, image);
                command.Parameters["$id"].Value = ids[i].ToString();
                command.Parameters["$name"].Value = $"HD2Mimnt Test Perf {i:D5} 装甲纹理 {(i % 2 == 0 ? "Armor" : "Weapon")}";
                command.Parameters["$author"].Value = $"Author{i % 100:D3}";
                command.Parameters["$description"].Value = new string('测', 180) + $" performance sample {i}";
                command.Parameters["$path"].Value = modPath;
                command.Parameters["$category"].Value = i % 5;
                command.Parameters["$date"].Value = DateTime.UtcNow.ToString("O");
                command.Parameters["$image"].Value = image;
                command.Parameters["$detection"].Value = BslModDetector.DetectionVersion;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        var profiles = new ProfileRepository(db);
        for (var p = 0; p < 12; p++)
        {
            var profile = new Profile { Id = Guid.NewGuid(), Name = $"HD2Mimnt Test Perf Profile {p:D2}",
                IsActive = p == 0, IsDefault = p == 0, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow };
            profile.Mods = ids.Take(p == 0 ? ModCount : 1000).Select((id, i) => new ProfileMod
                { ProfileId = profile.Id, ModId = id, Enabled = (p == 0 && resourcesPerMod > 0) || i % 3 != 0, Order = i }).ToList();
            await profiles.AddAsync(profile);
        }
        return ids;
    }

    // 每个工作线程单独连接；不并发复用应用的单一连接，也不把准备语句的收益混入实际查询。
    public static async Task<object> FloodAsync(string databasePath, Guid[] ids, int perWorker = 25_000,
        CancellationToken continueUntil = default)
    {
        var timer = Stopwatch.StartNew();
        var workers = await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False;Default Timeout=5");
            connection.Open();
            var latencies = new List<double>(perWorker);
            var random = new Random(42 + worker);
            var rows = 0L;
            var writes = 0;
            for (var i = 0; i < perWorker || (continueUntil.CanBeCanceled && !continueUntil.IsCancellationRequested); i++)
            {
                var start = Stopwatch.GetTimestamp();
                using var command = connection.CreateCommand();
                command.CommandText = (i % 20) switch
                {
                    0 => "UPDATE Mods SET Size=Size WHERE Id=$id",
                    1 => "SELECT COUNT(*) FROM Mods WHERE Category=$category",
                    2 => "SELECT COUNT(*) FROM ModsFTS WHERE ModsFTS MATCH 'Armor'",
                    _ => "SELECT * FROM Mods WHERE Id=$id"
                };
                command.Parameters.AddWithValue("$id", ids[random.Next(ids.Length)].ToString());
                command.Parameters.AddWithValue("$category", i % 5);
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows++;
                if (i % 20 == 0) writes++;
                latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            return new { Worker = worker, Statements = latencies.Count, Writes = writes, Rows = rows, LatencyMs = Distribution(latencies) };
        })));
        return new { Statements = workers.Sum(x => x.Statements), Writes = workers.Sum(x => x.Writes),
            ElapsedMs = timer.Elapsed.TotalMilliseconds, StatementsPerSecond = workers.Sum(x => x.Statements) / timer.Elapsed.TotalSeconds, Workers = workers };
    }

    public static object Distribution(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return new { Count = 0, P50 = 0d, P95 = 0d, P99 = 0d, Max = 0d, Over50 = 0, Over100 = 0 };
        double At(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * p) - 1)];
        return new { Count = sorted.Length, P50 = At(.5), P95 = At(.95), P99 = At(.99), Max = sorted[^1],
            Over50 = sorted.Count(x => x > 50), Over100 = sorted.Count(x => x > 100) };
    }

    public static void Save(string name, object report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        var directory = Environment.GetEnvironmentVariable("HD2MIMNT_PERF_RESULTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), json);
    }
}
