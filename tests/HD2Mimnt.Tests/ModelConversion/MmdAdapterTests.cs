using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Infrastructure.ModelConversion;
using HD2Mimnt.Infrastructure.ModelConversion.Mmd;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

/// <summary>
/// 合成样例与上游 <c>mmd_adapter.py</c> 的输出逐字节对照：
/// 同一份来源在 Python 与 C# 下生成的动作包、清单摘要必须一致。
/// 样例构造参数改动后需要重新生成上游期望值。
/// </summary>
public class MmdAdapterTests
{
    private const string UpstreamPatchSha = "41267cf5fe660a34194413f286a9e54f480bfd388335a771876b0ee592e48979";
    private const string UpstreamManifestSha = "2a688c49a14e424e2ddb25c76196f25f145d233e2b18fdf454fcd0595e842be5";
    private const string UpstreamTrackId = "mmd_14a709e78062987849eebf53";
    private const string UpstreamCameraSha = "7ae35df98e1e5b8bbde1f18ecc1a277cfce8f4ce5b1cb0da6e31226526bcf44b";
    private const string UpstreamWavSha = "d46d59b855724d85c6dec9ca64cadb6c6ff82ce261cf38ec6e79105a55a30257";
    private const string UpstreamPoseSha = "6d28b02ad60dee52292fab60f5f1f2bdda0951bdf4ea179c6e2582b73ac5ebb5";

    [Fact]
    public void Inspect_LegacyMmdMod_ReportsTrackAndMedia()
    {
        var root = CreateRoot();
        try
        {
            BuildLegacyMod(root);

            var (_, inspection) = MmdAdapter.Inspect(root, null, null, null, null, CancellationToken.None);

            Assert.Equal("ready", inspection.Status);
            Assert.Equal("测试曲包", inspection.Name);
            var track = Assert.Single(inspection.Tracks);
            Assert.Equal(UpstreamTrackId, track.Id);
            Assert.Equal(30, track.Fps);
            Assert.Equal(1.0, track.Duration);
            Assert.Equal(31, track.Frames);
            Assert.True(track.Music);
            Assert.True(track.Camera);
            Assert.Equal(new[] { "2222222222222222" }, track.OriginalSlots);
            Assert.Equal("loop", track.IdlePlayback);
            Assert.True(inspection.HasPreview);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Convert_MatchUpstreamHashes()
    {
        var root = CreateRoot();
        try
        {
            BuildLegacyMod(root);
            var output = Path.Combine(root, "..", Path.GetFileName(root) + "-out");
            try
            {
                var (plan, _) = MmdAdapter.Inspect(root, null, null, null, null, CancellationToken.None);
                var result = MmdAdapter.Convert(plan, output, null, CancellationToken.None);

                Assert.Equal("mmd_14a709e78062987849eebf53", result.Id);
                Assert.Equal("测试曲包", result.Title);
                Assert.Equal(31, result.Frames);
                Assert.Equal(5, result.Resources);
                Assert.Equal(UpstreamPatchSha, IasHash.Sha256Hex(File.ReadAllBytes(Path.Combine(output, MmdAdapter.ProductPatch))));
                Assert.Equal(UpstreamManifestSha, IasHash.Sha256Hex(File.ReadAllBytes(Path.Combine(output, "manifest.json"))));

                var entries = IasArchive.ReadEntries(
                    File.ReadAllBytes(Path.Combine(output, MmdAdapter.ProductPatch)),
                    File.ReadAllBytes(Path.Combine(output, MmdAdapter.ProductPatch + ".gpu_resources")),
                    File.ReadAllBytes(Path.Combine(output, MmdAdapter.ProductPatch + ".stream")));
                var (registration, blobs) = MmdBundle.Unpack(entries);
                Assert.Equal(UpstreamCameraSha, registration.Sha256["camera"]);
                Assert.Equal(UpstreamWavSha, registration.Sha256["wav"]);
                Assert.Equal(UpstreamPoseSha, registration.Sha256["pose"]);
                Assert.Equal(new[] { "camera", "wav", "pose" }, blobs.Select(blob => blob.Kind).ToArray());
            }
            finally
            {
                if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Inspect_AdaptedPack_RebuildsSamePose()
    {
        var root = CreateRoot();
        var output = Path.Combine(root, "..", Path.GetFileName(root) + "-out2");
        try
        {
            BuildLegacyMod(root);
            var (plan, _) = MmdAdapter.Inspect(root, null, null, null, null, CancellationToken.None);
            MmdAdapter.Convert(plan, output, null, CancellationToken.None);

            var (second, inspection) = MmdAdapter.Inspect(output, null, null, null, null, CancellationToken.None);
            Assert.Equal("ready", inspection.Status);
            Assert.Equal(UpstreamTrackId, inspection.Tracks[0].Id);
            var poseBlob = second.Spec.Blobs!.Single(blob => blob.Kind == "pose");
            Assert.Equal(UpstreamPoseSha, IasHash.Sha256Hex(poseBlob.Data));

            // 已适配包再次转换应与首次成品完全一致（同一登记行、动作与预览）。
            var rebuilt = Path.Combine(root, "..", Path.GetFileName(root) + "-out3");
            try
            {
                MmdAdapter.Convert(second, rebuilt, null, CancellationToken.None);
                Assert.Equal(UpstreamPatchSha,
                    IasHash.Sha256Hex(File.ReadAllBytes(Path.Combine(rebuilt, MmdAdapter.ProductPatch))));
            }
            finally
            {
                if (Directory.Exists(rebuilt)) Directory.Delete(rebuilt, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Inspect_ZipAndPatchEntrySources_MatchDirectory()
    {
        var root = CreateRoot();
        var zipPath = root + ".zip";
        var outDir = Path.Combine(root, "..", Path.GetFileName(root) + "-out4");
        try
        {
            BuildLegacyMod(root);
            System.IO.Compression.ZipFile.CreateFromDirectory(root, zipPath);

            var (zipPlan, zipInspection) = MmdAdapter.Inspect(zipPath, null, null, null, null, CancellationToken.None);
            Assert.Equal("ready", zipInspection.Status);
            Assert.Equal(UpstreamTrackId, zipInspection.Tracks[0].Id);
            var zipResult = MmdAdapter.Convert(zipPlan, outDir, null, CancellationToken.None);
            Assert.Equal(UpstreamPatchSha, IasHash.Sha256Hex(File.ReadAllBytes(Path.Combine(outDir, MmdAdapter.ProductPatch))));
            Assert.Equal(31, zipResult.Frames);

            var patch = Directory.EnumerateFiles(root, "1111111111111111.patch_0").Single();
            var (_, patchInspection) = MmdAdapter.Inspect(patch, null, null, null, null, CancellationToken.None);
            Assert.Equal("ready", patchInspection.Status);
            Assert.Equal(UpstreamTrackId, patchInspection.Tracks[0].Id);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WorkerPayloads_RoundTripThroughJson()
    {
        var inspection = new MmdInspection("mmd", "名", @"C:\x",
            [new MmdSourceOption("1", "动作", ["a.patch_0"])], "1", "loop", "ready",
            [new MmdInspectionTrack("id", "名", 30, 1.0, "loop", 31, true, true, ["1"], "角色库独立播放")], [], [], 0, [], true);
        var back = JsonSerializer.Deserialize<MmdInspection>(JsonSerializer.Serialize(inspection))!;
        Assert.Equal(inspection.IdlePlayback, back.IdlePlayback);
        Assert.Equal(inspection.SourceOptions[0].Patches, back.SourceOptions[0].Patches);
        Assert.Equal(inspection.Tracks[0].OriginalSlots, back.Tracks[0].OriginalSlots);

        var result = new MmdConvertResult(@"C:\out", "id", "名", "manual-adapted-v1", 30, "loop", 31, 1.0, ["2"], 5);
        var resultBack = JsonSerializer.Deserialize<MmdConvertResult>(JsonSerializer.Serialize(result))!;
        Assert.Equal(result.Resources, resultBack.Resources);
        Assert.Equal(result.OriginalSlots, resultBack.OriginalSlots);
    }

    [Fact]
    public void Inspect_MultipleManifests_Rejected()
    {
        var root = CreateRoot();
        try
        {
            BuildLegacyMod(root);
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.Copy(Path.Combine(root, "manifest.json"), Path.Combine(root, "nested", "manifest.json"));

            var error = Assert.Throws<InvalidDataException>(() =>
                MmdAdapter.Inspect(root, null, null, null, null, CancellationToken.None));
            Assert.Contains("多份模组清单", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Inspect_OptionDirectories_NeedSelection()
    {
        var root = CreateRoot();
        try
        {
            BuildLegacyMod(root, withOptions: true);

            var (_, inspection) = MmdAdapter.Inspect(root, null, null, null, null, CancellationToken.None);

            Assert.Equal("needs_selection", inspection.Status);
            Assert.Equal(2, inspection.SourceOptions.Count);
            Assert.Contains(inspection.Warnings, warning => warning.Contains("多个加载选项"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-MMD-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>构造一个只含 1 秒静止姿势的 90 骨骼 MMD 模组（结构与上游样例一致）。</summary>
    private static void BuildLegacyMod(string root, bool withOptions = false)
    {
        const string module = "mods/mmdtest";
        const ulong clipId = 0x2222222222222222;
        const int frames = 31;
        var icon = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        File.WriteAllBytes(Path.Combine(root, "icon.png"), icon);
        var manifest = withOptions
            ? $$"""
                {
                  "Name": "测试曲包 替换 表情",
                  "Guid": "11111111-2222-3333-4444-555555555555",
                  "Author": "tester",
                  "Description": "orig desc",
                  "IconPath": "icon.png",
                  "Version": 1,
                  "Options": [
                    { "Name": "动作A", "Include": ["a"] },
                    { "Name": "动作B", "Include": ["b"] }
                  ]
                }
                """
            : $$"""
                {
                  "Name": "测试曲包 替换 表情",
                  "Guid": "11111111-2222-3333-4444-555555555555",
                  "Author": "tester",
                  "Description": "orig desc",
                  "IconPath": "icon.png"
                }
                """;
        File.WriteAllText(Path.Combine(root, "manifest.json"), manifest, new UTF8Encoding(false));

        var wav = BuildWav();
        var addon = Encoding.UTF8.GetBytes($$"""
            -- HD2-Addon: {{module}}
            local a = checked_require('{{module}}/camera_runtime')
            local b = checked_require('{{module}}/audio_runtime')
            HD2DanceBus.register(HD2DanceKey, {"{{clipId}}"}, "test")
            return M
            """);
        var baked = string.Join('\n', Enumerable.Repeat("0,0,0,0,0,0,0,1", frames));
        var camera = Encoding.UTF8.GetBytes($$"""
            local FPS, COUNT = 30, {{frames}}
            local CUTS = {[0]=true,[10]=true}
            local BAKED = {
            {{baked}}
            }
            local stage_offset = config.stage_offset or {0,0,0}
            return {}
            """);
        var index = Encoding.UTF8.GetBytes($$"""
            local name='{{module}}/audio_blob'
            local chunk_count=1
            local size={{wav.Length}}
            local sha256='{{IasHash.Sha256Hex(wav)}}'
            return {}
            """);
        var blob = Encoding.ASCII.GetBytes("local data = {\"" + Convert.ToBase64String(wav) + "\"}\nreturn data\n");
        var runtime = Encoding.UTF8.GetBytes("local M = {}\nreturn M\n");

        var entries = new Dictionary<(ulong Type, ulong Id), IasResource>
        {
            [(IasResourceTypes.Animation, clipId)] = new(BuildClip(1.0f), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64(module))] = new(Lua(addon), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64(module + "/camera_runtime"))] = new(Lua(camera), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64(module + "/audio_runtime"))] = new(Lua(runtime), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64(module + "/audio_index"))] = new(Lua(index), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64(module + "/audio_blob"))] = new(Lua(blob), [], [])
        };
        var buffers = IasArchive.Write(entries);
        var folder = withOptions ? Path.Combine(root, "a") : root;
        Directory.CreateDirectory(folder);
        WritePatch(folder, buffers);
        if (withOptions)
        {
            var other = Path.Combine(root, "b");
            Directory.CreateDirectory(other);
            WritePatch(other, buffers);
        }
    }

    private static void WritePatch(string folder, IasArchiveBuffers buffers)
    {
        const string name = "1111111111111111.patch_0";
        File.WriteAllBytes(Path.Combine(folder, name), buffers.Toc);
        File.WriteAllBytes(Path.Combine(folder, name + ".gpu_resources"), buffers.Gpu);
        File.WriteAllBytes(Path.Combine(folder, name + ".stream"), buffers.Stream);
    }

    private static byte[] Lua(byte[] raw)
    {
        var result = new byte[8 + raw.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)raw.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        raw.CopyTo(result, 8);
        return result;
    }

    /// <summary>90 骨骼、无关键帧、所有初始通道为非压缩浮点的动作片段。</summary>
    private static byte[] BuildClip(float duration)
    {
        const int bones = 90;
        var size = 60 + bones * 40 + 2;
        var raw = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), bones);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(8), duration);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(12), (uint)size);
        var offset = 60;
        for (var bone = 0; bone < bones; bone++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 4), 2f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 8), 3f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 12), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 16), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 20), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 24), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 28), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 32), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 36), 1f);
            offset += 40;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(size - 2), 3);
        return raw;
    }

    private static byte[] BuildWav()
    {
        const int dataSize = 400;
        var wav = new byte[44 + dataSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(36 + dataSize));
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 48_000);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 192_000);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), dataSize);
        return wav;
    }
}
