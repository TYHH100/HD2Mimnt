using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// 真实 HD2Mimnt.Worker 预览会话的端到端验证：主程序侧只经 IPC 拿到模型结果与音频清单。
/// 会话进程读取 HD2MIMNT_DATA_PATH，整组禁用并行。
/// </summary>
[Collection(WorkerProcessCollection.Name)]
public sealed class PreviewWorkerProcessIntegrationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntPreviewWorker-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousDataPath;

    public PreviewWorkerProcessIntegrationTests()
    {
        Directory.CreateDirectory(_root);
        _previousDataPath = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
        Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", _root);
    }

    [Fact]
    public async Task RealWorker_LoadsModelPackAndAudioInventory()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        using var session = new PreviewWorkerSession(
            workerExecutablePath: Path.Combine(AppContext.BaseDirectory, "HD2Mimnt.Worker.exe"),
            connectTimeout: TimeSpan.FromSeconds(30),
            shutdownGracePeriod: TimeSpan.FromSeconds(5));
        session.Enter();
        try
        {
            var modDirectory = Path.Combine(_root, "mods", "preview-mod");
            Directory.CreateDirectory(modDirectory);
            var patch = CompatibilityFixture.WritePatch(modDirectory, resources: [TriangleUnit(1, 0.25f)]);

            var modelReader = new RemoteModelPreviewReader(session);
            var model = await modelReader.PreviewModelAsync(new DirectoryInfo(modDirectory),
                [new FileInfo(patch)], cancellationToken: cancellation.Token);
            Assert.Null(model.Error);
            var mesh = Assert.Single(model.Meshes);
            Assert.Equal(1ul, mesh.UnitId);
            Assert.Equal(3, mesh.VertexCount);
            Assert.Equal(new float[] { 0.25f, 0, 0, 1.25f, 0, 0, 0.25f, 1, 0 }, mesh.Positions);

            var texture = CompatibilityFixture.WriteRgba8Texture(new DirectoryInfo(modDirectory), alpha: 255);
            var decoded = await modelReader.PreviewTextureAsync(new DirectoryInfo(modDirectory), texture,
                32 * 32, cancellation.Token);
            Assert.NotNull(decoded);
            Assert.Equal(32, decoded.Width);
            Assert.Equal(32, decoded.Height);
            Assert.Equal(32 * 32 * 4, decoded.BgraPixels!.Length);

            var audioReader = new RemoteAudioPreviewReader(session);
            var inventory = await audioReader.InspectAsync(new DirectoryInfo(modDirectory),
                [new FileInfo(patch)], baselineResolver: null, cancellation.Token);
            Assert.Empty(inventory.Groups);
            Assert.Null(inventory.Error);
        }
        finally
        {
            await session.ExitAsync();
        }
        Assert.False(session.IsRunning);
    }

    private static CompatibilityFixture.Resource TriangleUnit(ulong id, float offsetX)
    {
        var resource = CompatibilityFixture.GpuUnit();
        const int streamInfo = 0x98;
        const int vertexBytes = 3 * 3 * sizeof(float);
        CompatibilityFixture.Put32(resource.Data, streamInfo + 0x160, 3);
        CompatibilityFixture.Put32(resource.Data, streamInfo + 0x1a4, vertexBytes);
        CompatibilityFixture.Put32(resource.Data, streamInfo + 0x1a8, vertexBytes);
        var gpu = new byte[vertexBytes + 6];
        float[] positions = [offsetX, 0, 0, offsetX + 1, 0, 0, offsetX, 1, 0];
        for (var index = 0; index < positions.Length; index++)
            CompatibilityFixture.Put32(gpu, index * sizeof(float), BitConverter.SingleToUInt32Bits(positions[index]));
        new byte[] { 0, 0, 1, 0, 2, 0 }.CopyTo(gpu, vertexBytes);
        return resource with { Id = id, Gpu = gpu };
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", _previousDataPath);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
