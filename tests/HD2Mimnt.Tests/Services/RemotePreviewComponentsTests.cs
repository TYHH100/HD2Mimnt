using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Tests.Services;

public sealed class RemotePreviewComponentsTests
{
    [Fact]
    public async Task Playback_ForwardsCommandsAndMirrorsWorkerEvents()
    {
        var session = new FakeSession
        {
            Responder = (_, _) => new PlaybackResultResponse { Success = true }
        };
        using var engine = new RemoteAudioPlaybackEngine(session);
        var entry = CreateEntry();

        var (success, error) = await engine.PlayAsync(entry, CancellationToken.None);
        Assert.True(success);
        Assert.Null(error);
        Assert.Same(entry, engine.CurrentEntry);
        Assert.Equal(AudioPlaybackState.Playing, engine.State);
        Assert.Contains(session.Requests, request => request.Method == PreviewMethods.PlaybackPlay);

        session.Raise(PreviewEventTypes.PlaybackState, new PlaybackStateEvent
        {
            State = AudioPlaybackState.Playing,
            PositionSeconds = 1.5,
            DurationSeconds = 9
        });
        Assert.Equal(1.5, engine.Position.TotalSeconds, 3);
        Assert.Equal(9, engine.Duration.TotalSeconds, 3);

        AudioEntry? ended = null;
        engine.PlaybackEnded += (endedEntry, _) => ended = endedEntry;
        session.Raise(PreviewEventTypes.PlaybackEnded, new PlaybackEndedEvent
        {
            SourceId = entry.SourceId,
            BackingFilePath = entry.BackingFilePath,
            DataOffset = entry.DataOffset
        });
        Assert.Same(entry, ended);
        Assert.Equal(AudioPlaybackState.Idle, engine.State);

        engine.Stop();
        Assert.Null(engine.CurrentEntry);
        Assert.Equal(AudioPlaybackState.Idle, engine.State);
        Assert.Contains(session.Posts, post => post.Method == PreviewMethods.PlaybackStop);
    }

    [Fact]
    public async Task ModelReader_ReadsPackAndDeletesWorkerPayloadFile()
    {
        var packPath = Path.Combine(Path.GetTempPath(), $"preview-pack-{Guid.NewGuid():N}.bin");
        var source = new ModelPreviewResult();
        source.Meshes.Add(new ModelPreviewMesh
        {
            PatchFile = "patch_0",
            UnitId = 7,
            StreamIndex = 1,
            Positions = [0f, 0f, 0f],
            TriangleIndices = [0, 0, 0]
        });
        using (var stream = File.Create(packPath)) ModelPreviewPack.Write(stream, source);

        var session = new FakeSession
        {
            Responder = (method, _) => method == PreviewMethods.LoadModel
                ? new ModelPreviewLoadResponse { PackPath = packPath }
                : throw new InvalidOperationException(method)
        };
        var reader = new RemoteModelPreviewReader(session);
        var result = await reader.PreviewModelAsync(new DirectoryInfo(Path.GetTempPath()), [],
            cancellationToken: CancellationToken.None);

        var mesh = Assert.Single(result.Meshes);
        Assert.Equal(7ul, mesh.UnitId);
        Assert.False(File.Exists(packPath));
    }

    [Fact]
    public async Task TextureReader_ReadsBgraPayloadAndDeletesFile()
    {
        var payloadPath = Path.Combine(Path.GetTempPath(), $"preview-tex-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(payloadPath, [1, 2, 3, 4]);
        var session = new FakeSession
        {
            Responder = (_, _) => new TexturePreviewResponse
            {
                Found = true,
                Width = 2,
                Height = 2,
                Description = "test",
                PayloadKind = "bgra",
                PayloadPath = payloadPath
            }
        };
        var reader = new RemoteModelPreviewReader(session);
        var texture = await reader.PreviewTextureAsync(new DirectoryInfo(Path.GetTempPath()),
            CreateTexture(), 4, CancellationToken.None);

        Assert.NotNull(texture);
        Assert.Equal([1, 2, 3, 4], texture!.BgraPixels);
        Assert.Null(texture.EncodedImageBytes);
        Assert.False(File.Exists(payloadPath));
    }

    [Fact]
    public void JsonPayloads_RoundTripAcrossProcessBoundary()
    {
        var texture = CreateTexture();
        var textureBack = JsonSerializer.Deserialize<TextureInspectionItem>(
            JsonSerializer.Serialize(texture))!;
        Assert.Equal(texture.TextureId, textureBack.TextureId);
        Assert.Equal(texture.PayloadKind, textureBack.PayloadKind);
        Assert.Equal(texture.Width, textureBack.Width);

        var entry = CreateEntry();
        var playBack = JsonSerializer.Deserialize<PlaybackPlayRequest>(
            JsonSerializer.Serialize(new PlaybackPlayRequest { Entry = entry }))!;
        Assert.Equal(entry.SourceId, playBack.Entry!.SourceId);
        Assert.Equal(entry.BackingFilePath, playBack.Entry.BackingFilePath);
        Assert.Equal(entry.DataOffset, playBack.Entry.DataOffset);

        var inventory = new AudioInventoryResult(
            [new AudioBankGroup("audio/p.patch_0", "bank", 0x99, [entry])], 1, null, 2);
        var inventoryBack = JsonSerializer.Deserialize<AudioInventoryResult>(
            JsonSerializer.Serialize(inventory))!;
        Assert.Equal(2, inventoryBack.UncomparedEntries);
        var group = Assert.Single(inventoryBack.Groups);
        Assert.Equal("bank", group.BankName);
        Assert.Equal(entry.SourceId, Assert.Single(group.Entries).SourceId);
    }

    private static AudioEntry CreateEntry() => new(
        0x1234, AudioEntryOrigin.BankMedia, "audio/p.patch_0", "bank", 0x99,
        Path.Combine(Path.GetTempPath(), "audio.patch_0"), 0, 1024, 2, 48000, AudioEntryIssue.None);

    private static TextureInspectionItem CreateTexture() => new()
    {
        PatchFile = "p",
        PatchPath = "p",
        PatchOrder = 0,
        TocEntryIndex = 0,
        TextureId = 1,
        MainOffset = 0,
        MainSize = 0,
        GpuOffset = 0,
        GpuSize = 0,
        StreamOffset = 0,
        StreamSize = 0,
        Width = 2,
        Height = 2,
        MipCount = 1,
        DxgiFormat = 71,
        PayloadKind = "DXGI",
        PayloadSource = "GPU"
    };

    private sealed class FakeSession : IPreviewWorkerSession
    {
        public event Action<PreviewEventPayload>? EventReceived;

        public List<(string Method, string? Payload)> Requests { get; } = [];
        public List<(string Method, string? Payload)> Posts { get; } = [];
        public required Func<string, object?, object?> Responder { get; init; }

        public void Enter() { }

        public Task ExitAsync() => Task.CompletedTask;

        public Task<TResponse?> RequestAsync<TResponse>(string method, object? payload,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((method, payload is null ? null : JsonSerializer.Serialize(payload)));
            return Task.FromResult((TResponse?)Responder(method, payload));
        }

        public void Post(string method, object? payload = null) =>
            Posts.Add((method, payload is null ? null : JsonSerializer.Serialize(payload)));

        public void Raise(string eventType, object payload) =>
            EventReceived?.Invoke(new PreviewEventPayload
            {
                EventType = eventType,
                Payload = JsonSerializer.Serialize(payload)
            });
    }
}
