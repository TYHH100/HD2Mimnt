using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public static class IasMorphInputs
{
    private const int PreviewHeaderSize = 96;

    public static IReadOnlyList<IasMorphSourceProgram> Pair(IReadOnlyList<IasMorphSourceProgram> programs,
        IasCompanionData data, IReadOnlyDictionary<string, byte[]> members,
        IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> resources)
    {
        if (programs.Count == 0) return [];
        var projects = PreviewProjects(members);
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (name, raw) in members.Where(entry => entry.Key.EndsWith(".isolation.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Encoding.UTF8.GetString(raw).TrimStart('\uFEFF').Split('\n'))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith('#')) continue;
                var separator = text.IndexOf('=');
                IasBinary.Require(separator >= 0, "manifest expected key=value");
                values[text[..separator].Trim()] = text[(separator + 1)..].Trim();
            }
            if (values.GetValueOrDefault("isolation_version") != "1"
                || !values.TryGetValue("archive", out var archive)
                || !values.TryGetValue("isolation_id", out var isolationId)) continue;
            if (!owners.TryGetValue(NormalizeArchive(archive), out var ownerSet)) owners[NormalizeArchive(archive)] = ownerSet = new(StringComparer.Ordinal);
            ownerSet.Add(isolationId);
        }
        var archives = data.Metadata.Members.Select(member => NormalizeArchive(member.Archive))
            .ToHashSet(StringComparer.Ordinal);
        var unique = new Dictionary<(string Project, string Archive), IasMorphSourceProgram>();
        foreach (var program in programs)
        {
            var document = program.Document;
            var identity = (document.ProjectId, document.ArchiveId);
            if (unique.TryGetValue(identity, out var previous))
            {
                IasBinary.Require(previous.Sha256 == program.Sha256,
                    "同一导出项目/Archive 的形态键数据不一致：" + previous.Name + "；" + program.Name);
                continue;
            }
            IasBinary.Require(archives.Contains(document.ArchiveId),
                "形态键 Archive 与所选骨架配套不一致：" + program.Name);
            if (projects.TryGetValue(document.ArchiveId, out var projectIds))
                IasBinary.Require(projectIds.Contains(document.ProjectId),
                    "形态键项目身份与所选骨架配套不一致：" + program.Name);
            var ownerHash = Convert.ToHexString(Blake2s(Encoding.UTF8.GetBytes(document.ProjectId + "\0" + document.ArchiveId), 12)).ToLowerInvariant();
            var owner = "owned_" + ownerHash;
            if (owners.TryGetValue(document.ArchiveId, out var ownerIds))
                IasBinary.Require(ownerIds.Contains(owner), "形态键项目身份与所选独立清单不一致：" + program.Name);
            IasBinary.Require(projects.ContainsKey(document.ArchiveId) || owners.ContainsKey(document.ArchiveId),
                "骨架配套缺少可核对的导出项目归属：" + program.Name);
            IasBinary.Require(document.Meshes.Count > 0, "形态键数据没有模型消费者：" + program.Name);
            foreach (var binding in document.Meshes)
            {
                IasBinary.Require(data.RawProfiles.ContainsKey(binding.UnitId),
                    "形态键消费者不在所选骨架配套中：" + binding.UnitId + "；" + program.Name);
                IasBinary.Require(resources.TryGetValue((IasResourceTypes.Unit, ulong.Parse(binding.UnitId, NumberStyles.HexNumber)), out var resource),
                    "形态键消费者不在本次模型中：" + binding.UnitId + "；" + program.Name);
                IasBinary.Require(IasHash.ResourceFingerprint(resource) == binding.UnitSha256,
                    "形态键模型摘要不匹配，请重新导出同一版模型与配套：" + binding.UnitId + "；" + program.Name);
            }
            unique[identity] = program;
        }
        return unique.Values.OrderBy(program => program.Name, StringComparer.Ordinal).ToList();
    }

    private static Dictionary<string, HashSet<string>> PreviewProjects(IReadOnlyDictionary<string, byte[]> members)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (name, raw) in members.Where(entry => entry.Key.EndsWith(".hd2preview", StringComparison.OrdinalIgnoreCase)))
        {
            IasBinary.Require(raw.Length >= PreviewHeaderSize, "形态配套预览头被截断：" + name);
            var magic = raw[..8];
            var version = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
            var total = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(20));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(24));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(28));
            var archive = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(88));
            if (!magic.SequenceEqual("HD2PRV1\0"u8) || version != 1 || size != PreviewHeaderSize || total != raw.Length
                || flags != 0 || IasBinary.Crc32(new ReadOnlySpan<byte>(raw, checked((int)size), raw.Length - checked((int)size))) != crc)
                throw new InvalidDataException("形态配套项目校验失败：" + name);
            _ = count;
            var projectField = new ReadOnlySpan<byte>(raw, 32, 56);
            var separator = projectField.IndexOf((byte)0);
            IasBinary.Require(separator > 0, "形态配套项目身份无效：" + name);
            var project = Encoding.ASCII.GetString(projectField[..separator]);
            IasBinary.Require(project.Length == 32 && project.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "形态配套项目身份无效：" + name);
            if (!result.TryGetValue($"{archive:x16}", out var projectSet)) result[$"{archive:x16}"] = projectSet = new(StringComparer.Ordinal);
            projectSet.Add(project);
        }
        return result;
    }

    internal static byte[] Blake2s(ReadOnlySpan<byte> input, int digestSize)
    {
        var iv = new uint[] { 0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19 };
        var sigma = new byte[][]
        {
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
            [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
            [11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4],
            [7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8],
            [9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13],
            [2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9],
            [12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11],
            [13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10],
            [6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5],
            [10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0]
        };
        var h = (uint[])iv.Clone();
        h[0] ^= 0x01010000 ^ (uint)digestSize;
        var blocks = Math.Max(1, (input.Length + 63) / 64);
        for (var block = 0; block < blocks; block++)
        {
            var data = new byte[64];
            var start = block * 64;
            var count = Math.Min(64, input.Length - start);
            if (count > 0) input.Slice(start, count).CopyTo(data);
            var words = new uint[16];
            for (var index = 0; index < 16; index++) words[index] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4));
            var counter = (ulong)(start + count);
            var last = block == blocks - 1;
            var v = new uint[16];
            h.CopyTo(v, 0);
            iv.CopyTo(v, 8);
            v[12] ^= (uint)counter;
            v[13] ^= (uint)(counter >> 32);
            if (last) v[14] = ~v[14];
            for (var round = 0; round < 10; round++)
            {
                Mix(v, 0, 4, 8, 12, words[sigma[round][0]], words[sigma[round][1]]);
                Mix(v, 1, 5, 9, 13, words[sigma[round][2]], words[sigma[round][3]]);
                Mix(v, 2, 6, 10, 14, words[sigma[round][4]], words[sigma[round][5]]);
                Mix(v, 3, 7, 11, 15, words[sigma[round][6]], words[sigma[round][7]]);
                Mix(v, 0, 5, 10, 15, words[sigma[round][8]], words[sigma[round][9]]);
                Mix(v, 1, 6, 11, 12, words[sigma[round][10]], words[sigma[round][11]]);
                Mix(v, 2, 7, 8, 13, words[sigma[round][12]], words[sigma[round][13]]);
                Mix(v, 3, 4, 9, 14, words[sigma[round][14]], words[sigma[round][15]]);
            }
            for (var index = 0; index < 8; index++) h[index] ^= v[index] ^ v[index + 8];
        }
        var result = new byte[digestSize];
        for (var index = 0; index < result.Length; index++) result[index] = (byte)(h[index / 4] >> (8 * (index % 4)));
        return result;
    }

    private static void Mix(uint[] v, int a, int b, int c, int d, uint x, uint y)
    {
        v[a] = unchecked(v[a] + v[b] + x);
        v[d] = RotateRight(v[d] ^ v[a], 16);
        v[c] += v[d];
        v[b] = RotateRight(v[b] ^ v[c], 12);
        v[a] = unchecked(v[a] + v[b] + y);
        v[d] = RotateRight(v[d] ^ v[a], 8);
        v[c] += v[d];
        v[b] = RotateRight(v[b] ^ v[c], 7);
    }

    private static uint RotateRight(uint value, int count) => (value >> count) | (value << (32 - count));

    private static string NormalizeArchive(string archive)
    {
        var value = archive.Trim().ToLowerInvariant();
        if (value.StartsWith("0x", StringComparison.Ordinal)) value = value[2..];
        IasBinary.Require(value.Length is > 0 and <= 16 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "manifest resource ID");
        return value.PadLeft(16, '0');
    }
}
