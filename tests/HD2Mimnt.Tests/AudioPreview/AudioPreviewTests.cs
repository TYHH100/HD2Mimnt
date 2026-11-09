using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.AudioPreview;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;

namespace HD2Mimnt.Tests.AudioPreview;

public sealed class AudioPreviewTests : IDisposable
{
    private const ulong BankTypeId = 0x535A7BD3E650D799;
    private const ulong StreamTypeId = 0x504B55235D21440E;
    private const ulong DepTypeId = 0xAF32095C82F2B070;
    private const string PackageName = "9ba626afa44a3aa3";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-Audio-" + Guid.NewGuid().ToString("N"));

    public AudioPreviewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------- 合成数据 ----------

    /// <summary>合成 Wwise Vorbis WEM：RIFF/WAVE + fmt(0x42, fmtTag=0xFFFF) + data。</summary>
    internal static byte[] Wem(int sampleRate = 48000, int channels = 2, ushort formatTag = 0xFFFF,
        bool declareOversizedRiff = false, byte payloadSeed = 7, int total = 300)
    {
        var data = new byte[total];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4),
            declareOversizedRiff ? (uint)(total - 4) : (uint)(total - 8));
        "WAVE"u8.CopyTo(data.AsSpan(8));
        "fmt "u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0x42);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), (uint)sampleRate);
        "data"u8.CopyTo(data.AsSpan(86));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(90), (uint)(total - 94));
        for (var i = 94; i < total; i++) data[i] = (byte)(payloadSeed + i);
        return data;
    }

    /// <summary>BKHD + DIDX + DATA 的 bank 字节；DIDX 偏移相对 DATA 体起点。</summary>
    internal static byte[] Bank(params (uint Id, byte[] Wem)[] media)
    {
        using var body = new MemoryStream();
        var didx = new byte[media.Length * 12];
        uint offset = 0;
        for (var i = 0; i < media.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12), media[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12 + 4), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12 + 8), (uint)media[i].Wem.Length);
            offset += (uint)media[i].Wem.Length;
        }
        WriteChunk(body, "BKHD", new byte[16]);
        WriteChunk(body, "DIDX", didx);
        WriteChunk(body, "DATA", media.SelectMany(m => m.Wem).ToArray());
        return body.ToArray();
    }

    private static void WriteChunk(MemoryStream stream, string tag, byte[] bodyBytes)
    {
        stream.Write(Encoding.ASCII.GetBytes(tag));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)bodyBytes.Length);
        stream.Write(size);
        stream.Write(bodyBytes);
    }

    /// <summary>bank 资源带 16 字节前缀；dep 资源带 8 字节前缀 + 名字 + 零填充。</summary>
    private static Resource BankResource(ulong fileId, byte[] bank) => new(fileId, BankTypeId, Pad(bank, 16));
    private static Resource StreamResource(ulong fileId, byte[] wem) => new(fileId, StreamTypeId, Pad(wem, 0), Stream: wem);
    private static Resource DepResource(ulong fileId, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[8 + 64];
        nameBytes.CopyTo(data, 8);
        return new(fileId, DepTypeId, data);
    }
    private static byte[] Pad(byte[] inner, int prefix) => prefix == 0 ? inner : [.. new byte[prefix], .. inner];

    private async Task<AudioInventoryResult> InspectAsync(params Resource[] resources)
    {
        var path = WritePatch(_root, $"{PackageName}.patch_0", resources: resources);
        var reader = new AudioBankInspectionService(NullLogger<AudioBankInspectionService>.Instance);
        return await reader.InspectAsync(new DirectoryInfo(_root), [new FileInfo(path)], null);
    }

    // ---------- 巡检 ----------

    [Fact]
    public async Task Inspect_BankMediaEnumeratesEntriesWithDepName()
    {
        // WwiseDep 资源与 bank 共用 fileId（depNames 按 bank.FileId 查找）。
        var result = await InspectAsync(DepResource(1, "test_bank"), BankResource(1, Bank((1001, Wem()), (1002, Wem(sampleRate: 44100)))));
        Assert.Null(result.Error);
        var group = Assert.Single(result.Groups);
        Assert.Equal("test_bank", group.BankName);
        Assert.Equal(1UL, group.BankFileId);
        Assert.Equal(2, group.Entries.Count);
        Assert.All(group.Entries, entry =>
        {
            Assert.Equal(AudioEntryOrigin.BankMedia, entry.Origin);
            Assert.Equal(2, entry.Channels);
            Assert.True(entry.IsPlayable);
            Assert.Equal(Path.Combine(_root, $"{PackageName}.patch_0"), entry.BackingFilePath);
            Assert.Equal($"{PackageName}.patch_0", entry.PatchRelativePath);
        });
        Assert.Equal(48000, group.Entries[0].SampleRate);
        Assert.Equal(44100, group.Entries[1].SampleRate);
        Assert.All(group.Entries, entry => Assert.Null(entry.MatchesOriginal));
    }

    [Fact]
    public async Task Inspect_StreamEntriesMergeIntoSingleBankGroup()
    {
        var result = await InspectAsync(
            BankResource(1, Bank((1001, Wem()))),
            StreamResource(0xAA, Wem(payloadSeed: 9)),
            StreamResource(0xAB, Wem(payloadSeed: 10)));
        var group = Assert.Single(result.Groups);
        Assert.Equal(3, group.Entries.Count);
        var streams = group.Entries.Where(entry => entry.Origin == AudioEntryOrigin.StreamMedia).ToList();
        Assert.Equal(2, streams.Count);
        Assert.All(streams, entry =>
        {
            Assert.Equal(Path.Combine(_root, $"{PackageName}.patch_0.stream"), entry.BackingFilePath);
            // 并入 bank 组的流条目继承 bank 的 fileId；没有 WwiseDep 时 bank 名缺失。
            Assert.Equal(1UL, entry.BankFileId);
            Assert.Null(entry.BankName);
        });
    }

    [Fact]
    public async Task Inspect_MultipleBanksKeepStreamsInStandaloneGroup()
    {
        var result = await InspectAsync(
            BankResource(1, Bank((1001, Wem()))),
            BankResource(2, Bank((1002, Wem()))),
            StreamResource(0xAA, Wem()));
        // 两个 bank 各成一组，流媒体因无唯一 bank 归属而留在独立组。
        Assert.Equal(3, result.Groups.Count);
        Assert.Equal(1001UL, Assert.Single(result.Groups[0].Entries).SourceId);
        Assert.Equal(1002UL, Assert.Single(result.Groups[1].Entries).SourceId);
        Assert.Equal(0xAAUL, Assert.Single(result.Groups[2].Entries).SourceId);
        Assert.All(result.Groups, group => Assert.Single(group.Entries));
    }

    [Fact]
    public async Task Inspect_WemHeaderIssuesAreClassified()
    {
        var garbage = new byte[120];
        Array.Fill(garbage, (byte)0x33);
        var pcm = Wem(formatTag: 1);
        var truncated = Wem(declareOversizedRiff: true);
        var result = await InspectAsync(BankResource(1, Bank((1, garbage), (2, pcm), (3, truncated))));
        var group = Assert.Single(result.Groups);
        Assert.Equal(AudioEntryIssue.NotRiff, group.Entries[0].Issue);
        Assert.Equal(AudioEntryIssue.NotVorbis, group.Entries[1].Issue);
        Assert.Equal(AudioEntryIssue.Truncated, group.Entries[2].Issue);
        Assert.All(group.Entries, entry => Assert.False(entry.IsPlayable));
    }

    [Theory]
    [InlineData("9ba626afa44a3aa3.patch_23", "9ba626afa44a3aa3")]
    [InlineData("mod.patch_0", "mod")]
    [InlineData("9ba626afa44a3aa3", "9ba626afa44a3aa3")]
    public void GetPackageBaseName_StripsPatchSuffix(string input, string expected)
        => Assert.Equal(expected, AudioBankInspectionService.GetPackageBaseName(input));

    // ---------- 原版比对 ----------

    [Fact]
    public async Task Compare_BaselineAnswersDriveMatchesOriginal()
    {
        var wem = Wem();
        var modified = Wem(payloadSeed: 42);
        var baseline = new FakeBaseline
        {
            BankLookup = AudioBaselineLookup.Found,
            BankSize = (uint)wem.Length,
            BankHash = SHA256.HashData(wem),
        };

        // 尺寸+哈希一致 → 原版。
        Assert.True(Assert.Single(Assert.Single((await InspectWithBaseline(baseline, BankResource(1, Bank((1001, wem))))).Groups).Entries).MatchesOriginal);

        // 尺寸一致但内容不同 → 已替换。
        Assert.False(Assert.Single(Assert.Single((await InspectWithBaseline(baseline, BankResource(1, Bank((1001, modified))))).Groups).Entries).MatchesOriginal);

        // 哈希同步为修改后的内容 → 原版。
        baseline.BankHash = SHA256.HashData(modified);
        Assert.True(Assert.Single(Assert.Single((await InspectWithBaseline(baseline, BankResource(1, Bank((1001, modified))))).Groups).Entries).MatchesOriginal);

        // 哈希不同 → 已替换。
        baseline.BankHash = SHA256.HashData(wem);
        Assert.False(Assert.Single(Assert.Single((await InspectWithBaseline(baseline, BankResource(1, Bank((1001, modified))))).Groups).Entries).MatchesOriginal);

        // 哈希未知 → null（未比对）。
        baseline.BankHash = null;
        Assert.Null(Assert.Single(Assert.Single((await InspectWithBaseline(baseline, BankResource(1, Bank((1001, modified))))).Groups).Entries).MatchesOriginal);

        // 基线里没有该媒体（模组新增）→ 已替换。
        baseline.BankLookup = AudioBaselineLookup.ResourceMissing;
        Assert.False(Assert.Single(Assert.Single((await InspectWithBaseline(baseline, BankResource(1, Bank((1001, modified))))).Groups).Entries).MatchesOriginal);

        // 基线无法回答（预算耗尽等）→ null。
        baseline.BankLookup = AudioBaselineLookup.Unavailable;
        Assert.Null(Assert.Single(Assert.Single((await InspectWithBaseline(baseline, BankResource(1, Bank((1001, modified))))).Groups).Entries).MatchesOriginal);
    }

    [Fact]
    public async Task Compare_StreamMediaUsesStreamBaseline()
    {
        var wem = Wem(payloadSeed: 5);
        var baseline = new FakeBaseline { StreamLookup = AudioBaselineLookup.Found, StreamSize = (uint)wem.Length, StreamHash = SHA256.HashData(wem) };
        var result = await InspectWithBaseline(baseline, StreamResource(0xAA, wem));
        Assert.True(Assert.Single(Assert.Single(result.Groups).Entries).MatchesOriginal);
    }

    [Fact]
    public async Task Baseline_LegacyLayoutAnswersQueries()
    {
        var wem = Wem(payloadSeed: 3);
        var streamWem = Wem(payloadSeed: 4);
        var dataDir = Path.Combine(_root, "game", "data");
        Directory.CreateDirectory(dataDir);
        var packagePath = Path.Combine(dataDir, PackageName);
        var package = PatchData(BankResource(0x42, Bank((1001, wem))), StreamResource(0xAA, streamWem));
        await File.WriteAllBytesAsync(packagePath, package.Main);
        await File.WriteAllBytesAsync(packagePath + ".stream", package.Stream);

        using var baseline = GameAudioBaseline.TryLoad(Path.Combine(_root, "game"), PackageName, slimOpener: null)!;
        Assert.Equal(AudioBaselineLookup.Found, baseline.TryGetBankMediaSize(0x42, 1001, out var size));
        Assert.Equal((uint)wem.Length, size);
        Assert.Equal(SHA256.HashData(wem), baseline.TryGetBankMediaHash(0x42, 1001));
        Assert.Equal(AudioBaselineLookup.Found, baseline.TryGetStreamMediaSize(0xAA, out var streamSize));
        Assert.Equal((uint)streamWem.Length, streamSize);
        Assert.Equal(SHA256.HashData(streamWem), baseline.TryGetStreamMediaHash(0xAA));
        Assert.Equal(AudioBaselineLookup.ResourceMissing, baseline.TryGetBankMediaSize(0x42, 9999, out _));
    }

    [Fact]
    public async Task Baseline_SlimLayoutAnswersQueriesViaArchiveSession()
    {
        var wem = Wem(payloadSeed: 6);
        WriteGame(_root, index => index == 0 ? PackageName : $"base_{index}",
            BankResource(0x42, Bank((1001, wem))));
        var reader = new GameUnitReferenceReader();
        using var baseline = GameAudioBaseline.TryLoad(_root, PackageName,
            (directory, name, token) => reader.TryOpenAudioPackage(directory, name, token),
            NullLogger.Instance)!;
        Assert.NotNull(baseline);
        Assert.Equal(AudioBaselineLookup.Found, baseline.TryGetBankMediaSize(0x42, 1001, out var size));
        Assert.Equal((uint)wem.Length, size);
        Assert.Equal(SHA256.HashData(wem), baseline.TryGetBankMediaHash(0x42, 1001));
    }

    [Fact]
    public void BaselineProvider_MissingGameDataReturnsNullResolver()
    {
        var provider = new GameAudioBaselineProvider(new GameUnitReferenceReader());
        Assert.Null(provider.CreateResolver(null));
        Assert.Null(provider.CreateResolver(""));
        Assert.Null(provider.CreateResolver(Path.Combine(_root, "no-such-game")));
    }

    // ---------- AudioPreviewService ----------

    [Fact]
    public async Task Service_LoadAsyncCachesByPatchSetAndPassesResolver()
    {
        WritePatch(_root, $"{PackageName}.patch_0", resources: []);
        var mod = new Mod { Id = Guid.NewGuid(), Path = _root, Name = "Audio service test" };
        var mods = new Mock<IModRepository>();
        mods.Setup(repo => repo.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
        Func<string, IGameAudioBaseline?>? capturedResolver = null;
        var reader = new Mock<IAudioPreviewReader>();
        var calls = 0;
        reader.Setup(r => r.InspectAsync(It.IsAny<DirectoryInfo>(), It.IsAny<IReadOnlyList<FileInfo>>(),
                It.IsAny<Func<string, IGameAudioBaseline?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DirectoryInfo _, IReadOnlyList<FileInfo> files, Func<string, IGameAudioBaseline?>? resolver, CancellationToken _) =>
            {
                calls++;
                capturedResolver = resolver;
                return new AudioInventoryResult([], files.Count, null);
            });
        var engine = new Mock<IAudioPlaybackEngine>();
        var provider = new GameAudioBaselineProvider(new GameUnitReferenceReader());
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        using var service = new AudioPreviewService(mods.Object, new Mock<IProfileRepository>().Object,
            reader.Object, engine.Object, provider, settings);

        var first = await service.LoadAsync(mod.Id);
        var second = await service.LoadAsync(mod.Id);
        Assert.Equal(1, calls); // 同一 patch 集：LRU 缓存命中，巡检只跑一次。
        Assert.Equal(1, first.PatchCount);
        Assert.Same(first, second);
        Assert.Null(capturedResolver); // 未配置游戏目录时不做原版比对。
        Assert.Same(engine.Object, service.Playback);
    }

    // ---------- 播放引擎（无音频设备路径） ----------

    [Fact]
    public async Task Engine_InvalidMediaFailsGracefully()
    {
        using var engine = new AudioPlaybackEngine(NullLogger<AudioPlaybackEngine>.Instance);
        Assert.Equal(TimeSpan.Zero, engine.Position);
        Assert.Equal(TimeSpan.Zero, engine.Duration);
        Assert.Equal(0, engine.Seek(30));
        var entry = new AudioEntry(1, AudioEntryOrigin.BankMedia, "x.patch_0", null, 0,
            Path.Combine(_root, "missing.patch_0"), 0, 10, 2, 48000, AudioEntryIssue.None);
        var (success, error) = await engine.PlayAsync(entry, CancellationToken.None);
        Assert.False(success);
        Assert.NotNull(error);
        engine.SetVolume(0.5f);
        engine.Stop();
        Assert.Equal(AudioPlaybackState.Idle, engine.State);
        Assert.Null(engine.CurrentEntry);
    }

    private async Task<AudioInventoryResult> InspectWithBaseline(IGameAudioBaseline baseline, params Resource[] resources)
    {
        var path = WritePatch(_root, $"{PackageName}.patch_0", resources: resources);
        var reader = new AudioBankInspectionService(NullLogger<AudioBankInspectionService>.Instance);
        return await reader.InspectAsync(new DirectoryInfo(_root), [new FileInfo(path)], _ => baseline);
    }

    private sealed class FakeBaseline : IGameAudioBaseline
    {
        public AudioBaselineLookup BankLookup { get; set; } = AudioBaselineLookup.Unavailable;
        public AudioBaselineLookup StreamLookup { get; set; } = AudioBaselineLookup.Unavailable;
        public uint BankSize { get; set; }
        public uint StreamSize { get; set; }
        public byte[]? BankHash { get; set; }
        public byte[]? StreamHash { get; set; }

        public AudioBaselineLookup TryGetBankMediaSize(ulong bankFileId, uint sourceId, out uint size)
        {
            size = BankSize;
            return BankLookup;
        }

        public AudioBaselineLookup TryGetStreamMediaSize(ulong streamFileId, out uint size)
        {
            size = StreamSize;
            return StreamLookup;
        }

        public byte[]? TryGetBankMediaHash(ulong bankFileId, uint sourceId) => BankHash;

        public byte[]? TryGetStreamMediaHash(ulong streamFileId) => StreamHash;

        public void Dispose() { }
    }
}
