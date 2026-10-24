using System.Text.Json;
using HD2Mimnt.Core.Interop;

namespace HD2Mimnt.Infrastructure.Interop;

/// <summary>固定目录中的离线协议。旧主版本始终由各自适配器重新生成。</summary>
public sealed class OfflineInteropPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
    };
    private readonly IReadOnlyList<IOfflineInteropAdapter> _adapters;
    public string RootPath { get; }
    public string IndexPath => Path.Combine(RootPath, "index.json");

    public OfflineInteropPublisher(string bootstrapPath, IEnumerable<IOfflineInteropAdapter>? adapters = null)
    {
        RootPath = Path.Combine(Path.GetFullPath(bootstrapPath), "interop");
        var versions = (adapters ?? []).ToList();
        // 应用升级不能移除 v1，也不能让新版投影覆盖 v1 的契约。
        if (versions.Any(adapter => adapter.MajorVersion < 2))
            throw new ArgumentException("v1 为保留契约，新增适配器必须使用新的主版本。", nameof(adapters));
        versions.Insert(0, new OfflineInteropV1Adapter());
        if (versions.Select(adapter => adapter.MajorVersion).Distinct().Count() != versions.Count)
            throw new ArgumentException("接口主版本不能重复。", nameof(adapters));
        _adapters = versions.OrderBy(adapter => adapter.MajorVersion).ToArray();
    }

    public async Task PublishAsync(OfflineRuntimeV1 snapshot, CancellationToken cancellationToken = default)
    {
        // 先完成所有投影，格式转换失败时保留原有文件。
        var payloads = _adapters.Select(adapter => (Adapter: adapter,
            Bytes: JsonSerializer.SerializeToUtf8Bytes(adapter.Project(snapshot), JsonOptions))).ToArray();
        foreach (var (adapter, bytes) in payloads)
            await WriteAtomicAsync(Path.Combine(RootPath, $"v{adapter.MajorVersion}", "runtime.json"), bytes,
                cancellationToken).ConfigureAwait(false);
        var index = new
        {
            format = "hd2mimnt-interop-index", schemaVersion = 1, snapshot.SnapshotId, snapshot.GeneratedUtc,
            latestMajorVersion = _adapters.Max(adapter => adapter.MajorVersion), defaultMajorVersion = 1,
            versions = _adapters.Select(adapter => new
            {
                majorVersion = adapter.MajorVersion, minorVersion = adapter.MinorVersion,
                runtimePath = Path.Combine(RootPath, $"v{adapter.MajorVersion}", "runtime.json")
            })
        };
        await WriteAtomicAsync(IndexPath, JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
