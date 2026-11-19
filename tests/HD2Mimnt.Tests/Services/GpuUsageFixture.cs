using System.Buffers.Binary;

namespace HD2Mimnt.Tests.Services;

internal static class GpuUsageFixture
{
    internal const ulong Texture = 0xcd4238c6a0c69e32;
    internal const ulong Unit = 0xe0a48d0be9a7453f;
    internal sealed record Resource(ulong Id, ulong Type, uint Gpu, uint Stream = 0, byte[]? Main = null);

    internal static byte[] Dds(uint format = 98, uint width = 4, uint height = 4, uint mips = 3,
        uint layers = 1, bool cube = false, uint depth = 0)
    {
        var header = new byte[148];
        "DDS "u8.CopyTo(header);
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset), value);
        Write(4, 124); Write(12, height); Write(16, width); Write(24, depth); Write(28, mips);
        Write(76, 32); Write(80, 4); Write(84, 0x30315844);
        Write(128, format); Write(132, depth > 0 ? 4u : 3u); Write(136, cube ? 4u : 0u); Write(140, layers);
        return header;
    }

    internal static byte[] TextureMain(byte[]? dds = null, int inlineBytes = 0)
    {
        dds ??= Dds();
        var main = new byte[0xC0 + dds.Length + inlineBytes];
        dds.CopyTo(main, 0xC0);
        return main;
    }

    internal static void WritePatch(string path, params Resource[] resources)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0xF0000011u); writer.Write(1u); writer.Write((uint)resources.Length);
        writer.Write(new byte[60 + 32]);
        var mainOffset = 104UL + 80UL * (ulong)resources.Length;
        ulong gpuOffset = 0, streamOffset = 0;
        foreach (var resource in resources)
        {
            var main = resource.Main ?? new byte[48];
            writer.Write(resource.Id); writer.Write(resource.Type); writer.Write(mainOffset);
            writer.Write(streamOffset); writer.Write(gpuOffset); writer.Write(new byte[16]);
            writer.Write((uint)main.Length); writer.Write(resource.Stream); writer.Write(resource.Gpu); writer.Write(new byte[12]);
            mainOffset += (ulong)main.Length; gpuOffset += resource.Gpu; streamOffset += resource.Stream;
        }
        foreach (var resource in resources) writer.Write(resource.Main ?? new byte[48]);
        using var gpu = File.Create(path + ".gpu_resources");
        gpu.SetLength((long)gpuOffset);
        using var stream = File.Create(path + ".stream");
        stream.SetLength((long)streamOffset);
    }
}
