using System.Buffers.Binary;
using System.IO;
using System.Text;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.ModelPreview;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.AudioPreview;

/// <summary>
/// 巡检一组模组 patch 文件的音频内容（音频预览页数据源）。与模型预览相同的方式遍历 patch
/// TOC：每个 WWISE_BANK 资源切成 Wwise bank chunk，DIDX 媒体条目各生成一个
/// <see cref="AudioEntry"/>；WWISE_STREAM 资源各自成一个条目。巡检期间只做 WEM 头的
/// 128 字节有界探测 —— 媒体数据留在磁盘上，由播放引擎按需读取。全部 CPU/IO 工作在
/// Task.Run 中执行。
/// </summary>
public sealed class AudioBankInspectionService(ILogger<AudioBankInspectionService>? logger = null) : IAudioPreviewReader
{
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int MaxTypes = 1000;
    private const int MaxFiles = 100000;
    private const int MaxBanksPerPatch = 64;
    private const int MaxMediaEntriesPerPatch = 65536;
    private const int MaxDepNameBytes = 512;
    private const int WemHeaderProbeBytes = 128;
    private const int BankDataPrefixSize = 16;
    private const int MaxOriginalCompareBytes = 32 * 1024 * 1024;

    private const int BankDataTag = unchecked((int)0x41544144); // "DATA"
    private const int DidxTag = unchecked((int)0x58444944);     // "DIDX"
    private const int RiffTag = unchecked((int)0x46464952);     // "RIFF"
    private const int WaveTag = unchecked((int)0x45564157);     // "WAVE"
    private const int FmtTag = unchecked((int)0x20746D66);      // "fmt "
    private const ushort VorbisFormatTag = 0xFFFF;
    private const ushort OggVorbisType2FormatTag = 0x6771;
    private const ushort OggVorbisType3FormatTag = 0x674F;

    private readonly record struct BankChunkInfo(ulong FileId, long DataOffset, uint DataSize, List<AudioBankChunkReader.DidxMedia> Didx);
    private readonly record struct StreamEntryInfo(ulong FileId, long Offset, uint Size);

    public Task<AudioInventoryResult> InspectAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        Func<string, IGameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Inspect(modDirectory, patchFiles, baselineResolver, cancellationToken), cancellationToken);

    private AudioInventoryResult Inspect(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        Func<string, IGameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken)
    {
        var groups = new List<AudioBankGroup>();
        var patchCount = 0;
        var uncomparedEntries = 0;
        string? error = null;

        foreach (var patch in patchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                patch.Refresh();
                if (!patch.Exists || patch.Length < HeaderSize)
                    continue;
                if (TryInspectPatch(modDirectory, patch, baselineResolver, cancellationToken) is { PatchGroups: var groupList, Uncompared: var uncompared })
                {
                    groups.AddRange(groupList);
                    uncomparedEntries += uncompared;
                    patchCount++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Audio inspection failed for patch {Patch}", patch.FullName);
                error ??= ex.Message;
            }
        }

        return new AudioInventoryResult(groups, patchCount, error, uncomparedEntries);
    }

    private readonly record struct PatchInspection(List<AudioBankGroup> PatchGroups, int Uncompared);

    private PatchInspection? TryInspectPatch(
        DirectoryInfo modDirectory,
        FileInfo patch,
        Func<string, IGameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken)
    {
        var streamPath = patch.FullName + ".stream";
        using var streamFile = File.Exists(streamPath)
            ? new FileStream(streamPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.RandomAccess)
            : null;

        using var patchFile = new FileStream(
            patch.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.RandomAccess);

        Span<byte> header = stackalloc byte[HeaderSize];
        if (!AudioBankChunkReader.TryReadAt(patchFile, 0, header) || BinaryPrimitives.ReadInt32LittleEndian(header) != PatchHeaderMagic)
            return null;

        var numTypes = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        var numFiles = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (numTypes < 0 || numFiles < 0 || numTypes > MaxTypes || numFiles > MaxFiles)
            return null;

        var fileEntriesOffset = HeaderSize + (long)numTypes * TypeEntrySize;
        if (fileEntriesOffset + (long)numFiles * FileEntrySize > patchFile.Length)
            return null;

        Span<byte> entry = stackalloc byte[FileEntrySize];
        var bankChunks = new List<BankChunkInfo>();
        var depNames = new Dictionary<ulong, string>();
        var streamEntries = new List<StreamEntryInfo>();

        for (var i = 0; i < numFiles; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AudioBankChunkReader.TryReadAt(patchFile, fileEntriesOffset + i * FileEntrySize, entry))
                return null;

            var fileId = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var typeId = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var tocDataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var streamFileOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[24..]);
            var tocDataSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            var streamSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]);

            if (typeId == PatchResourceTypeIds.WwiseBank)
            {
                if (bankChunks.Count >= MaxBanksPerPatch)
                    continue;
                if (tocDataSize <= BankDataPrefixSize ||
                    tocDataOffset < 0 ||
                    tocDataOffset + tocDataSize > patchFile.Length)
                    continue;
                var chunks = AudioBankChunkReader.ReadFromStream(patchFile, tocDataOffset + BankDataPrefixSize, tocDataSize - BankDataPrefixSize);
                if (chunks is not null)
                    bankChunks.Add(new BankChunkInfo(fileId, chunks.Value.DataOffset, chunks.Value.DataSize, chunks.Value.Didx));
            }
            else if (typeId == PatchResourceTypeIds.WwiseStream)
            {
                if (streamEntries.Count >= MaxMediaEntriesPerPatch)
                    continue;
                if (streamFile is null || streamFileOffset < 0 || streamFileOffset + streamSize > streamFile.Length)
                    continue;
                streamEntries.Add(new StreamEntryInfo(fileId, streamFileOffset, streamSize));
            }
            else if (typeId == PatchResourceTypeIds.WwiseDep)
            {
                if (tocDataOffset < 0 || tocDataOffset + 8 + tocDataSize > patchFile.Length || tocDataSize > MaxDepNameBytes)
                    continue;
                var nameBuffer = new byte[Math.Min(tocDataSize, MaxDepNameBytes)];
                if (AudioBankChunkReader.TryReadAt(patchFile, tocDataOffset + 8, nameBuffer))
                {
                    var name = Encoding.UTF8.GetString(nameBuffer).TrimEnd('\0');
                    if (name.Length > 0)
                        depNames[fileId] = name;
                }
            }
        }

        if (bankChunks.Count == 0 && streamEntries.Count == 0)
            return null;

        // 有音频资源才解析游戏基线（避免为纯模型补丁无谓加载游戏包）。
        var baseline = baselineResolver?.Invoke(GetPackageBaseName(patch.Name));
        // 按偏移排序：游戏 .stream 伴生文件的比对读取近似顺序 IO（机械盘友好，避免随机寻道风暴）。
        streamEntries.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

        var relativePath = Path.GetRelativePath(modDirectory.FullName, patch.FullName);
        var result = new List<AudioBankGroup>();
        var mergedStreamIds = new HashSet<ulong>();
        foreach (var bank in bankChunks)
        {
            var entries = new List<AudioEntry>();
            if (bank.DataOffset >= 0)
            {
                foreach (var media in bank.Didx)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entries.Count >= MaxMediaEntriesPerPatch)
                        break;
                    var available = Math.Min((long)media.Size, bank.DataOffset + bank.DataSize - (bank.DataOffset + media.Offset));
                    if (available <= 0)
                        continue;
                    var dataOffset = bank.DataOffset + media.Offset;
                    var (channels, sampleRate, issue) = ProbeWemHeader(patchFile, dataOffset, available);
                    entries.Add(new AudioEntry(
                        media.Id,
                        AudioEntryOrigin.BankMedia,
                        relativePath,
                        depNames.GetValueOrDefault(bank.FileId),
                        bank.FileId,
                        patch.FullName,
                        dataOffset,
                        available,
                        channels,
                        sampleRate,
                        issue,
                        issue == AudioEntryIssue.None
                            ? CompareWithOriginal(baseline, patchFile, dataOffset, available, media.Id, bank.FileId)
                            : null));
                }
            }

            // 一个 patch 通常只含一个 bank 加上它全部的流式语音行；把这些流合并进 bank
            // 分组共用一个展示组。多 bank 的 patch 其流媒体保留在下方独立分组。
            var bankName = depNames.GetValueOrDefault(bank.FileId);
            if (bankChunks.Count == 1)
            {
                foreach (var streamEntry in streamEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    mergedStreamIds.Add(streamEntry.FileId);
                    var (channels, sampleRate, issue) = ProbeWemHeader(streamFile!, streamEntry.Offset, streamEntry.Size);
                    entries.Add(new AudioEntry(
                        streamEntry.FileId,
                        AudioEntryOrigin.StreamMedia,
                        relativePath,
                        bankName,
                        bank.FileId,
                        streamPath,
                        streamEntry.Offset,
                        streamEntry.Size,
                        channels,
                        sampleRate,
                        issue,
                        issue == AudioEntryIssue.None
                            ? CompareWithOriginal(baseline, streamFile!, streamEntry.Offset, streamEntry.Size, streamEntry.FileId, 0)
                            : null));
                }
            }

            if (entries.Count > 0)
                result.Add(new AudioBankGroup(relativePath, bankName, bank.FileId, entries));
        }

        if (streamEntries.Count > mergedStreamIds.Count)
        {
            var entries = new List<AudioEntry>();
            foreach (var streamEntry in streamEntries)
            {
                if (mergedStreamIds.Contains(streamEntry.FileId))
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                var (channels, sampleRate, issue) = ProbeWemHeader(streamFile!, streamEntry.Offset, streamEntry.Size);
                entries.Add(new AudioEntry(
                    streamEntry.FileId,
                    AudioEntryOrigin.StreamMedia,
                    relativePath,
                    null,
                    0,
                    streamPath,
                    streamEntry.Offset,
                    streamEntry.Size,
                    channels,
                    sampleRate,
                    issue,
                    issue == AudioEntryIssue.None
                        ? CompareWithOriginal(baseline, streamFile!, streamEntry.Offset, streamEntry.Size, streamEntry.FileId, 0)
                        : null));
            }
            if (entries.Count > 0)
                result.Add(new AudioBankGroup(relativePath, null, 0, entries));
        }

        var uncomparedCount = 0;
        if (baseline is not null)
        {
            foreach (var group in result)
                uncomparedCount += group.Entries.Count(static entry => entry.MatchesOriginal is null);
        }
        return new PatchInspection(result, uncomparedCount);
    }

    /// <summary>"9ba626afa44a3aa3.patch_23" → "9ba626afa44a3aa3"；已是游戏包名则原样返回。</summary>
    internal static string GetPackageBaseName(string patchFileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            patchFileName,
            @"^(?<base>[0-9A-Fa-f]{16})\.patch_\d+$");
        return match.Success ? match.Groups["base"].Value : Path.GetFileNameWithoutExtension(patchFileName);
    }

    /// <summary>与游戏原版比对；null = 未知（无基线/预算耗尽/读取失败），false = 已替换或新增，true = 原版。
    /// 先查原版尺寸：大小不同直接判定已替换（零 IO）——语音模组的被替换条目几乎都命中这条快速路径。</summary>
    private static bool? CompareWithOriginal(
        IGameAudioBaseline? baseline,
        FileStream source,
        long offset,
        long size,
        ulong sourceId,
        ulong bankFileId)
    {
        if (baseline is null || size <= 0 || size > MaxOriginalCompareBytes)
            return null;
        try
        {
            var lookup = bankFileId != 0
                ? baseline.TryGetBankMediaSize(bankFileId, (uint)sourceId, out var originalSize)
                : baseline.TryGetStreamMediaSize(sourceId, out originalSize);
            if (lookup == AudioBaselineLookup.ResourceMissing)
                return false;
            if (lookup != AudioBaselineLookup.Found)
                return null;
            if (originalSize != (ulong)size)
                return false;

            var originalHash = bankFileId != 0
                ? baseline.TryGetBankMediaHash(bankFileId, (uint)sourceId)
                : baseline.TryGetStreamMediaHash(sourceId);
            if (originalHash is null)
                return null;
            var modHash = HashFileSlice(source, offset, size);
            return modHash is not null && modHash.AsSpan().SequenceEqual(originalHash);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>流式哈希文件切片（不整块分配内存；条目上限 32MB，语音条目通常几十 KB）。</summary>
    private static byte[]? HashFileSlice(FileStream stream, long offset, long size)
    {
        if (offset < 0 || size <= 0 || offset > stream.Length - size)
            return null;
        var buffer = new byte[64 * 1024];
        stream.Position = offset;
        using var sha = System.Security.Cryptography.SHA256.Create();
        long remaining = size;
        while (remaining > 0)
        {
            var take = (int)Math.Min(buffer.Length, remaining);
            var read = 0;
            while (read < take)
            {
                var count = stream.Read(buffer, read, take - read);
                if (count <= 0)
                    return null;
                read += count;
            }
            sha.TransformBlock(buffer, 0, take, null, 0);
            remaining -= take;
        }
        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash!;
    }

    /// <summary>Bounded WEM header probe: verifies RIFF/WAVE + Vorbis fmt and reads channels/rate.</summary>
    private static (int Channels, int SampleRate, AudioEntryIssue Issue) ProbeWemHeader(FileStream backingFile, long offset, long available)
    {
        try
        {
            // Anything smaller than a minimal RIFF/WAVE+fmt header can never be a WEM.
            if (offset < 0 || available < 44 || offset >= backingFile.Length)
                return (0, 0, available < 44 ? AudioEntryIssue.NotRiff : AudioEntryIssue.ReadFailed);

            Span<byte> header = stackalloc byte[WemHeaderProbeBytes];
            var probeLength = (int)Math.Min(WemHeaderProbeBytes, Math.Min(available, backingFile.Length - offset));
            if (probeLength < 44 || !AudioBankChunkReader.TryReadAt(backingFile, offset, header[..probeLength]))
                return (0, 0, AudioEntryIssue.ReadFailed);

            if (BinaryPrimitives.ReadInt32LittleEndian(header) != RiffTag ||
                BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != WaveTag)
                return (0, 0, AudioEntryIssue.NotRiff);

            var declaredRiffSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (declaredRiffSize > available - 8)
                return (0, 0, AudioEntryIssue.Truncated);

            // find the fmt chunk (RIFF chunk walk, bounded by the probe)
            var position = 12;
            while (position + 8 <= probeLength)
            {
                var tag = BinaryPrimitives.ReadInt32LittleEndian(header[position..]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header[(position + 4)..]);
                var body = position + 8;
                if (tag == FmtTag)
                {
                    if (body + 16 > probeLength)
                        return (0, 0, AudioEntryIssue.NotVorbis);
                    var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(header[body..]);
                    var channels = BinaryPrimitives.ReadUInt16LittleEndian(header[(body + 2)..]);
                    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(header[(body + 4)..]);
                    if (formatTag is not (VorbisFormatTag or OggVorbisType2FormatTag or OggVorbisType3FormatTag))
                        return (0, 0, AudioEntryIssue.NotVorbis);
                    return (channels, (int)sampleRate, AudioEntryIssue.None);
                }

                if (body + size > probeLength)
                    break; // fmt chunk sits beyond our small probe; treat as unsupported rather than failing
                position = body + (int)size + (int)(size & 1);
            }

            return (0, 0, AudioEntryIssue.NotVorbis);
        }
        catch (Exception)
        {
            return (0, 0, AudioEntryIssue.ReadFailed);
        }
    }
}
