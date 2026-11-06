using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace HD2Mimnt.ModelConversion.Source;

/// <summary>Little-endian random-access reader over an in-memory file with explicit bounds checks.</summary>
public sealed class BinaryBlock
{
    private readonly byte[] _data;

    public BinaryBlock(byte[] data, string label)
    {
        _data = data;
        Label = label;
    }

    public string Label { get; }

    public int Length => _data.Length;

    public ReadOnlySpan<byte> Span => _data;

    public void Check(long offset, long size, string what)
    {
        if (offset < 0 || size < 0 || offset > _data.Length || size > _data.Length - offset)
        {
            throw new SourceFormatException(
                $"{Label}: {what} at offset {offset} with size {size} exceeds file length {_data.Length}");
        }
    }

    public ReadOnlySpan<byte> Slice(int offset, int size, string what)
    {
        Check(offset, size, what);
        return _data.AsSpan(offset, size);
    }

    public byte U8(int offset, string what = "uint8")
    {
        Check(offset, 1, what);
        return _data[offset];
    }

    public sbyte I8(int offset, string what = "int8") => unchecked((sbyte)U8(offset, what));

    public ushort U16(int offset, string what = "uint16")
    {
        Check(offset, 2, what);
        return BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset, 2));
    }

    public short I16(int offset, string what = "int16")
    {
        Check(offset, 2, what);
        return BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(offset, 2));
    }

    public int I32(int offset, string what = "int32")
    {
        Check(offset, 4, what);
        return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset, 4));
    }

    public uint U32(int offset, string what = "uint32")
    {
        Check(offset, 4, what);
        return BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset, 4));
    }

    public float F32(int offset, string what = "float")
    {
        Check(offset, 4, what);
        return BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(offset, 4));
    }

    public Vector2 Vec2(int offset, string what = "vector2") => new(F32(offset, what), F32(offset + 4, what));

    public Vector3 Vec3(int offset, string what = "vector3") =>
        new(F32(offset, what), F32(offset + 4, what), F32(offset + 8, what));

    public Vector4 Vec4(int offset, string what = "vector4") =>
        new(F32(offset, what), F32(offset + 4, what), F32(offset + 8, what), F32(offset + 12, what));

    public Quaternion Quat(int offset, string what = "quaternion") =>
        new(F32(offset, what), F32(offset + 4, what), F32(offset + 8, what), F32(offset + 12, what));

    public float[] Floats(int offset, int count, string what)
    {
        Check(offset, count * 4L, what);
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(offset + i * 4, 4));
        }

        return result;
    }

    public string CString(int offset, int limit = 4096)
    {
        Check(offset, 1, "string");
        if (limit <= 0)
        {
            throw new SourceFormatException($"{Label}: string limit must be positive");
        }

        var max = Math.Min(limit, _data.Length - offset);
        var end = Array.IndexOf(_data, (byte)0, offset, max);
        if (end < 0)
        {
            throw new SourceFormatException($"{Label}: string at offset {offset} is unterminated");
        }

        return Encoding.UTF8.GetString(_data, offset, end - offset);
    }

    public string FixedString(int offset, int size)
    {
        var field = Slice(offset, size, "fixed string");
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    /// <summary>A zero relative offset uses the fallback.</summary>
    public string RelativeCString(int structOffset, int relativeOffset, string fallback)
    {
        if (relativeOffset == 0)
        {
            return fallback;
        }

        var absolute = (long)structOffset + relativeOffset;
        if (absolute <= 0 || absolute >= _data.Length)
        {
            throw new SourceFormatException($"{Label}: string pointer {absolute} for {fallback} is outside the file");
        }

        var value = CString((int)absolute);
        return value.Length == 0 ? fallback : value;
    }
}
