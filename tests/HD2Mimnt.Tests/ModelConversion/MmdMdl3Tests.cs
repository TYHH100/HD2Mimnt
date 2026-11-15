using System.Buffers.Binary;
using System.Text;
using HD2Mimnt.Infrastructure.ModelConversion;
using HD2Mimnt.Infrastructure.ModelConversion.Mmd;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

/// <summary>MDL3 曲包（紧凑关键帧记录 + MS-ADPCM 音轨）与合集选曲。</summary>
public class MmdMdl3Tests
{
    private const ulong ClipId = 0x3333333333333333;

    [Fact]
    public void Inspect_Mdl3Pack_DecodesCompactKeyframesAndAudio()
    {
        var root = CreateRoot();
        try
        {
            BuildMdl3Mod(root);
            var (plan, inspection) = MmdAdapter.Inspect(root, null, null, null, null, CancellationToken.None);
            Assert.Equal("ready", inspection.Status);
            var track = Assert.Single(inspection.Tracks);
            Assert.Equal(30, track.Fps);
            Assert.Equal(31, track.Frames);
            Assert.True(track.Music);
            Assert.True(track.Camera);
            Assert.Equal(new[] { "3333333333333333" }, track.OriginalSlots);
            Assert.NotNull(plan.Spec.Mdl3Audio);

            var pose = MmdMotion.Decode(plan.Entries.First(entry =>
                entry.Key.Type == IasResourceTypes.Animation).Value.Toc, 30);
            Assert.Equal(31, MmdMotion.CheckPose(pose));
            var bone1Frame16 = 16 + (16 * MmdMotion.BoneCount + 1) * MmdMotion.PoseRecordSize;
            Assert.Equal(512, BinaryPrimitives.ReadInt16LittleEndian(pose.AsSpan(bone1Frame16)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Convert_Mdl3Pack_ResamplesAudioTo48kAndKeepsCameraRows()
    {
        var root = CreateRoot();
        var output = root + "-out";
        try
        {
            BuildMdl3Mod(root);
            var (plan, _) = MmdAdapter.Inspect(root, null, null, null, null, CancellationToken.None);
            var result = MmdAdapter.Convert(plan, output, null, CancellationToken.None);
            Assert.Equal(31, result.Frames);

            var entries = IasArchive.ReadEntries(
                File.ReadAllBytes(Path.Combine(output, MmdAdapter.ProductPatch)),
                File.ReadAllBytes(Path.Combine(output, MmdAdapter.ProductPatch + ".gpu_resources")),
                File.ReadAllBytes(Path.Combine(output, MmdAdapter.ProductPatch + ".stream")));
            var (registration, blobs) = MmdBundle.Unpack(entries);
            Assert.Equal(31, MmdBundle.Check(registration, blobs));
            Assert.Equal(new[] { "camera", "wav", "pose" }, blobs.Select(blob => blob.Kind).ToArray());

            var wav = blobs.Single(blob => blob.Kind == "wav").Data;
            Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)));
            Assert.Equal(16, (int)BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
            Assert.Equal(2, (int)BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));
            Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));

            var camera = blobs.Single(blob => blob.Kind == "camera").Data;
            var lines = Encoding.ASCII.GetString(camera).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(32, lines.Length);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Inspect_Collection_ListsMmdModulesAndExcludesRuntime()
    {
        var root = CreateRoot();
        try
        {
            BuildLegacyModule(Path.Combine(root, "song_a"), "歌曲A 替换 表情",
                "aaaaaaaa-1111-2222-3333-444444444444", "mods/songa", 0x4444444444444441);
            BuildLegacyModule(Path.Combine(root, "song_b"), "歌曲B 替换 表情",
                "bbbbbbbb-1111-2222-3333-444444444444", "mods/songb", 0x4444444444444442);
            BuildRuntimeModule(Path.Combine(root, "runtime"));

            var (pending, inspection) = MmdAdapter.Inspect(root, null, null, null, null, CancellationToken.None);
            Assert.Equal("needs_selection", inspection.Status);
            Assert.Equal(2, inspection.SourceOptions.Count);
            Assert.DoesNotContain(inspection.SourceOptions, option => option.Name.Contains("runtime"));
            Assert.Contains(inspection.Warnings, warning => warning.Contains("多个曲包"));

            var songB = inspection.SourceOptions.Single(option => option.Name == "歌曲B 替换 表情");
            var (plan, ready) = MmdAdapter.Inspect(root, songB.Key, null, null, null, CancellationToken.None);
            Assert.Equal("ready", ready.Status);
            Assert.Equal("歌曲B", ready.Name);
            Assert.Equal(31, Assert.Single(ready.Tracks).Frames);

            var output = root + "-out";
            try
            {
                MmdAdapter.Convert(plan, output, null, CancellationToken.None);
                var entries = IasArchive.ReadEntries(
                    File.ReadAllBytes(Path.Combine(output, MmdAdapter.ProductPatch)),
                    File.ReadAllBytes(Path.Combine(output, MmdAdapter.ProductPatch + ".gpu_resources")),
                    File.ReadAllBytes(Path.Combine(output, MmdAdapter.ProductPatch + ".stream")));
                var (registration, blobs) = MmdBundle.Unpack(entries);
                Assert.Equal(31, MmdBundle.Check(registration, blobs));
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

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-MDL3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void BuildMdl3Mod(string root)
    {
        File.WriteAllText(Path.Combine(root, "manifest.json"), """
            {
              "Name": "MDL3 合成曲",
              "Guid": "cccccccc-1111-2222-3333-444444444444",
              "Author": "tester",
              "Description": "mdl3"
            }
            """, new UTF8Encoding(false));
        var registration = """
            return {
              ["id"]="mdl3test",
              ["label"]="MDL3 合成曲",
              ["duration"]=1.0,
              ["stage_offset"]={0,0,0},
              ["resources"]={"3333333333333333"},
              ["path"]="mods/mdl3test",
            }
            """;
        var baked = string.Join('\n', Enumerable.Repeat("0,0,0,0,0,0,0,1", 31));
        var camera = $$"""
            return function(PoseMath)
            local FPS, COUNT = 30, 31
            local CUTS = {[0]=true}
            local BAKED = {
            {{baked}}
            }
            return {}
            """;
        var audio = $$"""
            -- Test2: 合成 MS-ADPCM
            return {
            format='{{MmdAdpcmFixtures.FormatBase64()}}',
            bytes={{MmdAdpcmFixtures.Data().Length}},
            sha256='{{MmdAdpcmFixtures.Sha256()}}',
            data=table.concat({'{{MmdAdpcmFixtures.DataBase64()}}'})
            }
            """;
        var entries = new Dictionary<(ulong Type, ulong Id), IasResource>
        {
            [(IasResourceTypes.Animation, ClipId)] = new(BuildCompactClip(1.0f), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64("mods/mdl3test/registration"))] =
                new(MmdAdpcmFixtures.Envelope(registration), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64("mods/mdl3test/camera"))] =
                new(MmdAdpcmFixtures.Envelope(camera), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64("mods/mdl3test/audio"))] =
                new(MmdAdpcmFixtures.Envelope(audio), [], []),
        };
        WritePatch(root, "1111111111111111.patch_0", entries);
    }

    /// <summary>与经典片段相同的头部/初始姿势，关键帧记录省略 4 字节通道头。</summary>
    private static byte[] BuildCompactClip(float duration)
    {
        const int bones = 90;
        var size = 60 + bones * 40 + (10 + 16) + (10 + 12) + 2;
        var raw = new byte[size];
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

        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(offset), 5);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(offset + 2), 0);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 6), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 10), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 14), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 18), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 22), 1f);
        offset += 26;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(offset), 4);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(offset + 2), 1);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 6), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 10), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 14), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 18), 0f);
        offset += 22;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(offset), 3);
        return raw;
    }

    private static void BuildLegacyModule(string folder, string name, string guid, string module, ulong clipId)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), $$"""
            {
              "Name": "{{name}}",
              "Guid": "{{guid}}",
              "Author": "tester",
              "Description": "legacy"
            }
            """, new UTF8Encoding(false));
        var addon = Encoding.UTF8.GetBytes($$"""
            -- HD2-Addon: {{module}}
            local a = checked_require('{{module}}/camera_runtime')
            local b = checked_require('{{module}}/audio_runtime')
            HD2DanceBus.register(HD2DanceKey, {"{{clipId}}"}, "test")
            return M
            """);
        var baked = string.Join('\n', Enumerable.Repeat("0,0,0,0,0,0,0,1", 31));
        var camera = Encoding.UTF8.GetBytes($$"""
            local FPS, COUNT = 30, 31
            local CUTS = {[0]=true}
            local BAKED = {
            {{baked}}
            }
            return {}
            """);
        var wav = BuildWav();
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
            [(IasResourceTypes.Animation, clipId)] = new(BuildLegacyClip(1.0f), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64(module))] = new(MmdAdpcmFixtures.Envelope(Encoding.UTF8.GetString(addon)), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64(module + "/camera_runtime"))] = new(MmdAdpcmFixtures.Envelope(Encoding.UTF8.GetString(camera)), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64(module + "/audio_runtime"))] = new(MmdAdpcmFixtures.Envelope(Encoding.UTF8.GetString(runtime)), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64(module + "/audio_index"))] = new(MmdAdpcmFixtures.Envelope(Encoding.UTF8.GetString(index)), [], []),
            [(IasResourceTypes.Lua, IasHash.Murmur64(module + "/audio_blob"))] = new(MmdAdpcmFixtures.Envelope(Encoding.UTF8.GetString(blob)), [], []),
        };
        WritePatch(folder, "2222222222222222.patch_0", entries);
    }

    private static void BuildRuntimeModule(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), """
            {
              "Name": "共用运行时",
              "Guid": "dddddddd-1111-2222-3333-444444444444",
              "Author": "tester",
              "Description": "runtime"
            }
            """, new UTF8Encoding(false));
        var entries = new Dictionary<(ulong Type, ulong Id), IasResource>
        {
            [(IasResourceTypes.Lua, IasHash.Murmur64("mods/runtime"))] =
                new(MmdAdpcmFixtures.Envelope("local M = {}\nreturn M\n"), [], []),
        };
        WritePatch(folder, "5555555555555555.patch_0", entries);
    }

    private static void WritePatch(string folder, string name,
        IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> entries)
    {
        var buffers = IasArchive.Write(entries);
        File.WriteAllBytes(Path.Combine(folder, name), buffers.Toc);
        File.WriteAllBytes(Path.Combine(folder, name + ".gpu_resources"), buffers.Gpu);
        File.WriteAllBytes(Path.Combine(folder, name + ".stream"), buffers.Stream);
    }

    /// <summary>经典关键帧记录（带 4 字节通道头）的最小片段，用于合集里的普通曲包。</summary>
    private static byte[] BuildLegacyClip(float duration)
    {
        const int bones = 90;
        var size = 60 + bones * 40 + 2;
        var raw = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), bones);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(8), duration);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(12), (uint)size);
        var offset = 60;
        for (var bone = 0; bone < bones; bone++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset), 1f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 4), 2f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 8), 3f);
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(offset + 16), 1f);
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
