using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;

namespace HD2Mimnt.Tests.Services;

internal static class CompatibilityFixture
{
    internal const ulong UnitType = 0xe0a48d0be9a7453f;
    internal const ulong MaterialType = 0xeac0b497876adedf;
    internal const uint CurrentVersion = 0xa4cd36;
    internal sealed record Resource(ulong Id, ulong Type, byte[] Data, byte[]? Gpu = null, byte[]? Stream = null);
    internal static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    internal static void Put64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);

    internal static byte[] Unit(uint version = CurrentVersion, int lodSize = 16, byte lodByte = 3, bool legacyLayout = false)
    {
        var list = 0x78 + lodSize;
        var data = new byte[legacyLayout ? list + 8 + 328 + 8 : list + 16];
        Put32(data, 0x2c, version);
        Put32(data, 0x30, 0x78);
        Put32(data, 0x34, (uint)list);
        Put32(data, 0x60, (uint)data.Length - 8);
        data.AsSpan(0x78, lodSize).Fill(lodByte);
        if (legacyLayout)
        {
            Put32(data, 0x5c, (uint)list);
            Put32(data, list, 1);
            Put32(data, list + 4, 8);
            Put32(data, list + 8 + 8 + 4, 17);
        }
        return data;
    }

    internal static Resource GpuUnit(bool invalidIndex = false, bool invalidPosition = false, bool unknownFormat = false, bool unnormalizedWeights = false)
    {
        var list = 0x88;
        var info = list + 16;
        var unit = new byte[info + 0x1b0 + 8];
        Put32(unit, 0x2c, CurrentVersion);
        Put32(unit, 0x30, 0x78);
        Put32(unit, 0x34, (uint)list);
        Put32(unit, 0x5c, (uint)list);
        Put32(unit, 0x60, (uint)unit.Length - 8);
        Put32(unit, list, 1);
        Put32(unit, list + 4, 16);
        Put32(unit, info + 8, unnormalizedWeights ? 7u : 0u);
        Put32(unit, info + 12, unnormalizedWeights ? 35u : unknownFormat ? 900u : 2u);
        Put64(unit, info + 0x148, 1);
        Put32(unit, info + 0x160, 1);
        Put32(unit, info + 0x164, unnormalizedWeights ? 8u : 12u);
        Put32(unit, info + 0x188, 3);
        Put32(unit, info + 0x1a4, unnormalizedWeights ? 8u : 12u);
        Put32(unit, info + 0x1a8, unnormalizedWeights ? 8u : 12u);
        Put32(unit, info + 0x1ac, 6);
        var gpu = new byte[unnormalizedWeights ? 14 : 18];
        if (unnormalizedWeights)
            for (var i = 0; i < 4; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(gpu.AsSpan(i * 2), BitConverter.HalfToUInt16Bits((Half)0.2f));
        if (invalidIndex) gpu[12] = 2;
        if (invalidPosition) Put32(gpu, 0, 0x7fc00000);
        return new Resource(1, UnitType, unit, gpu);
    }

    internal static byte[] Material(bool emissive = false)
    {
        uint[] semantics = emissive ? [0x1D57DCF3, 0xCA6F2CF1, 0x848BA63B]
            : [0x7CA0D044, 0xC985395A, 0xA72CB013, 0x479FB1EF, 0xDF3EE984, 0xCAED6CD6, 0xD2F99D38,
                0xE7BD9019, 0xD47DB28B, 0xFF2C91CC, 0x736A0029, 0xF8E31D7B, 0xA59F5E11];
        var data = new byte[emissive ? 512 : 320];
        Put64(data, 0x18, emissive ? 0xD3701FC725106C09 : 0x54AE9CE1A8FAFE8B);
        Put32(data, 0x40, (uint)semantics.Length);
        for (var i = 0; i < semantics.Length; i++) Put32(data, 0x88 + i * 4, semantics[i]);
        if (emissive)
        {
            (uint Id, uint Offset)[] variables = [(0xA3351311, 0), (0x43695F7B, 4), (0x64AAB07B, 8), (0x6FD0B9E7, 12),
                (0x60E7D2A1, 16), (0x4A7CD0EF, 20), (0x4A6796C6, 24), (0xBD16A396, 28), (0x32C02400, 56),
                (0xC012EFE1, 36), (0xA83F44CD, 40), (0x6DDBAE8F, 44), (0x4B564F57, 48), (0x9ED04DA2, 52)];
            Put32(data, 0x68, (uint)variables.Length);
            Put32(data, 0x78, 60);
            var descriptor = 0x88 + 12 + 24;
            for (var i = 0; i < variables.Length; i++)
            {
                Put32(data, descriptor + i * 20 + 8, variables[i].Id);
                Put32(data, descriptor + i * 20 + 12, variables[i].Offset);
                Put32(data, descriptor + variables.Length * 20 + (int)variables[i].Offset, (uint)(100 + i));
            }
        }
        return data;
    }

    internal static (byte[] Main, byte[] Gpu, byte[] Stream) PatchData(params Resource[] resources)
    {
        var types = resources.GroupBy(r => r.Type).ToArray();
        var tableEnd = 72 + types.Length * 32 + resources.Length * 80;
        var data = new byte[tableEnd + resources.Sum(r => r.Data.Length)];
        var gpu = new MemoryStream();
        var stream = new MemoryStream();
        Put32(data, 0, 0xf0000011);
        Put32(data, 4, (uint)types.Length);
        Put32(data, 8, (uint)resources.Length);
        for (var i = 0; i < types.Length; i++)
        {
            Put64(data, 72 + i * 32 + 8, types[i].Key);
            Put64(data, 72 + i * 32 + 16, (ulong)types[i].Count());
            Put32(data, 72 + i * 32 + 24, 16);
            Put32(data, 72 + i * 32 + 28, 16);
        }
        var payload = tableEnd;
        for (var i = 0; i < resources.Length; i++)
        {
            var resource = resources[i];
            var offset = 72 + types.Length * 32 + i * 80;
            Put64(data, offset, resource.Id);
            Put64(data, offset + 8, resource.Type);
            Put64(data, offset + 16, (ulong)payload);
            Put64(data, offset + 24, (ulong)stream.Position);
            Put64(data, offset + 32, (ulong)gpu.Position);
            Put32(data, offset + 56, (uint)resource.Data.Length);
            Put32(data, offset + 60, (uint)(resource.Stream?.Length ?? 0));
            Put32(data, offset + 64, (uint)(resource.Gpu?.Length ?? 0));
            Put32(data, offset + 76, (uint)i + 1);
            resource.Data.CopyTo(data, payload);
            payload += resource.Data.Length;
            if (resource.Gpu is not null) gpu.Write(resource.Gpu);
            if (resource.Stream is not null) stream.Write(resource.Stream);
        }
        return (data, gpu.ToArray(), stream.ToArray());
    }

    internal static string WritePatch(string root, string name = "abcdeffedcba9876.patch_0", bool companions = true, params Resource[] resources)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        var data = PatchData(resources);
        File.WriteAllBytes(path, data.Main);
        if (companions && data.Gpu.Length > 0) File.WriteAllBytes(path + ".gpu_resources", data.Gpu);
        if (companions && data.Stream.Length > 0) File.WriteAllBytes(path + ".stream", data.Stream);
        return path;
    }

    internal static void WriteGame(string root, params Resource[] resources)
    {
        var directory = Path.Combine(root, "data");
        Directory.CreateDirectory(directory);
        var packages = new List<(string Name, uint Offset)>();
        var chunks = new List<(ulong Offset, byte[] Data)>();
        for (var i = 0; i < resources.Length; i++)
        {
            var patch = PatchData(resources[i]);
            var offset = (uint)(0x1000 + i * 0x100000);
            var name = $"base_{i}";
            packages.Add((name, offset));
            chunks.Add((offset, patch.Main));
            if (patch.Gpu.Length > 0) { packages.Add((name + ".gpu_resources", offset + 0x40000)); chunks.Add((offset + 0x40000, patch.Gpu)); }
            if (patch.Stream.Length > 0) { packages.Add((name + ".stream", offset + 0x80000)); chunks.Add((offset + 0x80000, patch.Stream)); }
        }
        var nameStart = 24 + packages.Count * 24;
        var itemStart = nameStart + packages.Sum(p => Encoding.UTF8.GetByteCount(p.Name) + 1);
        var index = new byte[itemStart + packages.Count * 16];
        Put32(index, 12, 1);
        Put32(index, 16, (uint)packages.Count);
        var namePosition = nameStart;
        for (var i = 0; i < packages.Count; i++)
        {
            Put32(index, 24 + i * 24 + 8, (uint)namePosition);
            Put32(index, 24 + i * 24 + 12, 1);
            Put32(index, 24 + i * 24 + 16, (uint)(itemStart + i * 16));
            var name = Encoding.UTF8.GetBytes(packages[i].Name + "\0");
            name.CopyTo(index, namePosition);
            namePosition += name.Length;
            Put32(index, itemStart + i * 16 + 8, packages[i].Offset);
        }
        WriteDsar(Path.Combine(directory, "bundles.nxa"), [(0, index)]);
        WriteDsar(Path.Combine(directory, "bundles.00.nxa"), chunks.OrderBy(c => c.Offset).ToArray());
    }

    private static void WriteDsar(string path, IReadOnlyList<(ulong Offset, byte[] Data)> chunks)
    {
        var encoded = chunks.Select(chunk =>
        {
            var buffer = new byte[LZ4Codec.MaximumOutputSize(chunk.Data.Length)];
            return buffer[..LZ4Codec.Encode(chunk.Data, buffer)];
        }).ToArray();
        var position = 32 + chunks.Count * 32;
        var data = new byte[position + encoded.Sum(c => c.Length)];
        Put32(data, 0, 0x52415344);
        Put32(data, 8, (uint)chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var entry = 32 + i * 32;
            Put64(data, entry, chunks[i].Offset);
            Put64(data, entry + 8, (ulong)position);
            Put32(data, entry + 16, (uint)chunks[i].Data.Length);
            Put32(data, entry + 20, (uint)encoded[i].Length);
            data[entry + 24] = 3;
            data[entry + 25] = 2;
            encoded[i].CopyTo(data, position);
            position += encoded[i].Length;
        }
        File.WriteAllBytes(path, data);
    }
}
