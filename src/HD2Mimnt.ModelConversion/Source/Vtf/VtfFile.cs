using System.Numerics;

namespace HD2Mimnt.ModelConversion.Source.Vtf;

public sealed record VtfResource(uint Tag, byte Flags, uint Data);
public sealed record VtfImage(int Mip, int Frame, int Face, int Slice, int Width, int Height, int Offset, int Length);

public sealed class VtfFile
{
    private VtfFile(byte[] data) => RawData = data;
    public ReadOnlyMemory<byte> RawData { get; }
    public int MinorVersion { get; private init; }
    public int Width { get; private init; }
    public int Height { get; private init; }
    public int Depth { get; private init; }
    public int Frames { get; private init; }
    public ushort FirstFrame { get; private init; }
    public uint Flags { get; private init; }
    public int Format { get; private init; }
    public int MipCount { get; private init; }
    public Vector3 Reflectivity { get; private init; }
    public float BumpScale { get; private init; }
    public IReadOnlyList<VtfResource> Resources { get; private init; } = [];
    public IReadOnlyList<VtfImage> Images { get; private set; } = [];
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    public bool HasImageLayout => Images.Count != 0;
    public bool IsCubeMap => (Flags & 0x4000) != 0;

    public ReadOnlyMemory<byte> GetImageData(VtfImage image)
    {
        if (!Images.Contains(image)) throw new ArgumentException("Image is not part of this VTF", nameof(image));
        return RawData.Slice(image.Offset, image.Length);
    }

    public static VtfFile Parse(byte[] data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var block = new BinaryBlock(data, "VTF");
        if (block.Length < 64 || !block.Slice(0, 4, "signature").SequenceEqual("VTF\0"u8))
            throw new SourceFormatException("Invalid VTF header");
        var minor = block.I32(8);
        if (block.I32(4) != 7 || minor is < 1 or > 4)
            throw new SourceFormatException("Only VTF 7.1–7.4 headers are currently supported");
        var headerSize = block.I32(12);
        var minimumHeader = minor >= 3 ? 80 : minor >= 2 ? 65 : 64;
        block.Check(0, headerSize, "header");
        if (headerSize < minimumHeader) throw new SourceFormatException("VTF header is too short for its version");
        var resources = new List<VtfResource>();
        if (minor >= 3)
        {
            var count = block.I32(68);
            if (count < 0 || count > 32 || 80L + count * 8 > headerSize)
                throw new SourceFormatException("Invalid VTF resource table");
            var tags = new HashSet<uint>();
            for (var i = 0; i < count; i++)
            {
                var offset = 80 + i * 8;
                var tag = block.U32(offset) & 0xFFFFFF;
                var flags = block.U8(offset + 3);
                var value = block.U32(offset + 4);
                if (!tags.Add(tag)) throw new SourceFormatException("Duplicate VTF resource tag");
                if ((flags & 2) == 0 && (value < headerSize || value >= block.Length))
                    throw new SourceFormatException("VTF resource offset is outside payload");
                resources.Add(new VtfResource(tag, flags, value));
            }
        }
        var texture = new VtfFile(data)
        {
            MinorVersion = minor, Width = block.U16(16), Height = block.U16(18), Flags = block.U32(20),
            Frames = block.U16(24), FirstFrame = block.U16(26), Reflectivity = block.Vec3(32), BumpScale = block.F32(48),
            Format = block.I32(52), MipCount = block.U8(56), Depth = minor >= 2 ? block.U16(63) : 1,
            Resources = resources.AsReadOnly()
        };
        if (texture.Width == 0 || texture.Height == 0 || texture.Depth == 0 || texture.Frames == 0 ||
            texture.MipCount < 1 || texture.MipCount > 1 + (int)Math.Log2(Math.Max(texture.Width, Math.Max(texture.Height, texture.Depth))))
            throw new SourceFormatException("Invalid VTF dimensions, frames or mip count");
        var lowWidth = block.U8(61);
        var lowHeight = block.U8(62);
        var lowFormat = block.I32(57);
        long imageStart;
        if (minor >= 3)
        {
            var high = resources.SingleOrDefault(r => r.Tag == 0x30);
            if (high is null || high.Flags != 0) throw new SourceFormatException("VTF high-resolution image resource is missing or invalid");
            imageStart = high.Data;
        }
        else
        {
            var lowSize = lowWidth == 0 && lowHeight == 0 ? 0 : ImageSize(lowFormat, lowWidth, lowHeight);
            if (lowSize is null) throw new SourceFormatException($"Unsupported VTF thumbnail format {lowFormat}");
            imageStart = headerSize + lowSize.Value;
        }
        if (texture.IsCubeMap || texture.Depth != 1)
        {
            texture.Warnings = ["Cube/volume layout has not been validated; complete original VTF bytes are retained, without guessing face/slice ranges."];
            return texture;
        }
        if (ImageSize(texture.Format, 1, 1) is null)
        {
            texture.Warnings = [$"VTF format {texture.Format} is retained as raw data; its image layout is not supported."];
            return texture;
        }
        var images = new List<VtfImage>();
        var cursor = imageStart;
        for (var mip = texture.MipCount - 1; mip >= 0; mip--)
        {
            var width = Math.Max(1, texture.Width >> mip);
            var height = Math.Max(1, texture.Height >> mip);
            var size = ImageSize(texture.Format, width, height)!.Value;
            for (var frame = 0; frame < texture.Frames; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                block.Check(cursor, size, $"mip {mip}, frame {frame}");
                images.Add(new VtfImage(mip, frame, 0, 0, width, height, (int)cursor, (int)size));
                cursor += size;
            }
        }
        if (resources.Any(r => (r.Flags & 2) == 0 && r.Tag != 0x30 && r.Data >= imageStart && r.Data < cursor))
            throw new SourceFormatException("VTF image range overlaps another resource");
        texture.Images = images.AsReadOnly();
        return texture;
    }

    private static long? ImageSize(int format, int width, int height) => format switch
    {
        13 or 20 => ((width + 3L) / 4) * ((height + 3L) / 4) * 8,
        14 or 15 => ((width + 3L) / 4) * ((height + 3L) / 4) * 16,
        0 or 1 or 11 or 12 or 16 or 23 or 26 or 27 => (long)width * height * 4,
        2 or 3 or 9 or 10 => (long)width * height * 3,
        4 or 6 or 17 or 18 or 19 or 21 or 22 => (long)width * height * 2,
        5 or 8 => (long)width * height,
        24 or 25 => (long)width * height * 8,
        28 => (long)width * height * 12,
        29 => (long)width * height * 16,
        _ => null
    };
}
