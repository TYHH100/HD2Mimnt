using System.Buffers.Binary;

namespace HD2Mimnt.Infrastructure.ModelConversion.Mmd;

/// <summary>
/// 上游 <c>mmd_motion</c>：把 90 骨骼 Stingray 动作片段解码成逐帧可寻址的 HD2POSE1 姿势数据。
/// 输出布局（16 字节头 + 每帧 90×7 个 int16）与上游逐字节一致，改动会直接改变成品哈希。
/// </summary>
public static class MmdMotion
{
    public const int BoneCount = 90;
    public const int PoseHeaderSize = 16;
    public const int PoseRecordSize = 14;
    public static readonly byte[] PoseMagic = "HD2POSE1"u8.ToArray();

    /// <summary>上游 <c>check_pose</c>：校验姿势数据并返回帧数。</summary>
    public static int CheckPose(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < PoseHeaderSize || !blob[..8].SequenceEqual(PoseMagic))
            throw new InvalidDataException("骨骼动作帧数或大小无效。");
        var frames = BinaryPrimitives.ReadUInt32LittleEndian(blob[8..]);
        var bones = BinaryPrimitives.ReadUInt32LittleEndian(blob[12..]);
        if (bones != BoneCount || frames < 2 || frames > 27001
            || blob.Length != PoseHeaderSize + (long)frames * bones * PoseRecordSize)
            throw new InvalidDataException("骨骼动作帧数或大小无效。");
        return (int)frames;
    }

    public static byte[] Decode(ReadOnlySpan<byte> raw, int fps)
    {
        if (raw.Length < 26) throw new InvalidDataException("不支持此动作骨架或事件表");
        var bones = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        var duration = BinaryPrimitives.ReadSingleLittleEndian(raw[8..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]);
        var events = BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]);
        var extra = BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]);
        if (bones != BoneCount || size != raw.Length || !(duration > 0) || duration > 900 || events != 0 || extra != 0)
            throw new InvalidDataException("不支持此动作骨架或事件表");
        var frames = (int)Math.Round((double)duration * fps, MidpointRounding.ToEven) + 1;
        if (frames < 2 || frames > 27001) throw new InvalidDataException("动作超出可播放帧数");
        if (raw.Length < 60) throw new InvalidDataException("动作头截断");

        var flags = raw.Slice(26, 34);
        var reader = new Reader(raw, 60);
        var channels = new List<(double Time, double[] Value)>[BoneCount][];
        for (var bone = 0; bone < BoneCount; bone++)
        {
            var bits = new bool[3];
            for (var index = 0; index < 3; index++)
            {
                var bit = 3 * bone + index;
                bits[index] = ((flags[bit / 8] >> (bit % 8)) & 1) != 0;
            }

            var position = reader.Vector(bits[0]);
            var quaternion = bits[1] ? Quat(reader.ReadUInt32()) : reader.ReadRaw(4);
            var scale = reader.Vector(bits[2]);
            if (scale.Any(value => Math.Abs(value - 1) > 0.003))
                throw new InvalidDataException("动作含未支持的缩放通道");
            channels[bone] =
            [
                new List<(double, double[])> { (0.0, position) },
                new List<(double, double[])> { (0.0, quaternion) }
            ];
        }

        var cursor = reader.Position;
        var compact = IsCompactTimeline(raw, cursor);
        while (true)
        {
            Require(raw.Length >= cursor + 2, "动作关键帧截断");
            var word = BinaryPrimitives.ReadUInt16LittleEndian(raw[cursor..]);
            if (word == 3)
            {
                cursor += 2;
                break;
            }

            int bone;
            double time;
            int channel;
            double[] value;
            if (compact)
            {
                // MDL3 导出工具省略了 4 字节通道头：记录从 subtype 直接开始。
                Require(raw.Length >= cursor + 10, "动作关键帧截断");
                bone = BinaryPrimitives.ReadInt32LittleEndian(raw[(cursor + 2)..]);
                time = BinaryPrimitives.ReadSingleLittleEndian(raw[(cursor + 6)..]);
                cursor += 10;
                if (word == 2) continue;
                if (word is not (4 or 5 or 6)) throw new InvalidDataException("未知的动作通道 " + word);
                channel = word - 4;
                var count = channel == 1 ? 4 : 3;
                Require(raw.Length >= cursor + 4 * count, "动作关键帧截断");
                value = new double[count];
                for (var index = 0; index < count; index++)
                    value[index] = BinaryPrimitives.ReadSingleLittleEndian(raw[(cursor + 4 * index)..]);
                cursor += 4 * count;
            }
            else
            {
                Require(raw.Length >= cursor + 4, "动作关键帧截断");
                byte a = raw[cursor], b = raw[cursor + 1], c = raw[cursor + 2], d = raw[cursor + 3];
                var kind = b >> 6;
                cursor += 4;
                if (kind != 0)
                {
                    bone = (a >> 4) | ((b & 63) << 4);
                    time = ((a & 15) << 16 | d << 8 | c) / 1000.0;
                    channel = kind switch { 3 => 1, 2 => 0, _ => 2 };
                    if (kind == 3)
                    {
                        Require(raw.Length >= cursor + 4, "动作关键帧截断");
                        value = Quat(BinaryPrimitives.ReadUInt32LittleEndian(raw[cursor..]));
                        cursor += 4;
                    }
                    else
                    {
                        Require(raw.Length >= cursor + 6, "动作关键帧截断");
                        value = CompressedVector(raw, cursor);
                        cursor += 6;
                    }
                }
                else
                {
                    Require(raw.Length >= cursor + 6, "动作关键帧截断");
                    var subtype = BinaryPrimitives.ReadUInt16LittleEndian(raw[cursor..]);
                    bone = BinaryPrimitives.ReadInt32LittleEndian(raw[(cursor + 2)..]);
                    time = BinaryPrimitives.ReadSingleLittleEndian(raw[(cursor + 6)..]);
                    cursor += 10;
                    if (subtype == 2) continue;
                    if (subtype is not (4 or 5 or 6)) throw new InvalidDataException("未知的动作通道 " + subtype);
                    channel = subtype - 4;
                    var count = channel == 1 ? 4 : 3;
                    Require(raw.Length >= cursor + 4 * count, "动作关键帧截断");
                    value = new double[count];
                    for (var index = 0; index < count; index++)
                        value[index] = BinaryPrimitives.ReadSingleLittleEndian(raw[(cursor + 4 * index)..]);
                    cursor += 4 * count;
                }
            }

            if (bone < 0 || bone >= BoneCount || !double.IsFinite(time) || time < -0.001 || time > duration + 0.002)
                throw new InvalidDataException("动作骨索引或时间越界");
            if (channel == 2)
            {
                if (value.Any(item => Math.Abs(item - 1) > 0.003))
                    throw new InvalidDataException("舞蹈有非单位缩放");
                continue;
            }

            var curve = channels[bone][channel];
            var frame = time * fps;
            var nearest = Math.Round(frame, MidpointRounding.ToEven);
            if (Math.Abs(frame - nearest) < 0.04) frame = nearest;
            if (frame < curve[^1].Time) throw new InvalidDataException("动作关键帧时间逆序");
            if (frame == curve[^1].Time) curve[^1] = (frame, value);
            else curve.Add((frame, value));
        }

        var blob = new byte[PoseHeaderSize + (long)frames * BoneCount * PoseRecordSize];
        PoseMagic.CopyTo(blob, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), (uint)frames);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(12), BoneCount);
        for (var bone = 0; bone < BoneCount; bone++)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                var curve = channels[bone][channel];
                var k = 0;
                var factor = channel == 1 ? 32767.0 : 1024.0;
                for (var f = 0; f < frames; f++)
                {
                    while (k + 1 < curve.Count && curve[k + 1].Time <= f) k++;
                    var lo = curve[k].Time;
                    var value = curve[k].Value;
                    if (lo < f && k + 1 < curve.Count)
                    {
                        var (hi, other) = curve[k + 1];
                        var alpha = (f - lo) / (hi - lo);
                        var sign = channel == 1 && Dot(value, other) < 0 ? -1.0 : 1.0;
                        value = value.Select((item, index) => item + (sign * other[index] - item) * alpha).ToArray();
                    }
                    if (channel == 1)
                    {
                        var length = Math.Sqrt(value.Sum(item => item * item));
                        if (!(length > 0.9 && length < 1.1)) throw new InvalidDataException("动作四元数无效");
                        value = value.Select(item => item / length).ToArray();
                    }

                    var position = PoseHeaderSize + ((long)f * BoneCount + bone) * PoseRecordSize + 6 * channel;
                    for (var index = 0; index < value.Length; index++)
                    {
                        var quantized = (short)Math.Round(value[index] * factor, MidpointRounding.ToEven);
                        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan((int)(position + 2 * index)), quantized);
                    }
                }
            }
        }
        return blob;
    }

    /// <summary>检测关键帧记录是否省略了通道头（MDL3 工具导出的变体），两种布局不能混用。</summary>
    private static bool IsCompactTimeline(ReadOnlySpan<byte> raw, int at)
    {
        if (at + 10 > raw.Length) return false;
        var word = BinaryPrimitives.ReadUInt16LittleEndian(raw[at..]);
        if (word is not (2 or 4 or 5 or 6) || raw[at + 1] >> 6 != 0) return false;
        return BinaryPrimitives.ReadUInt16LittleEndian(raw[(at + 4)..]) is not (2 or 4 or 5 or 6);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static double Dot(double[] left, double[] right)
    {
        var sum = 0.0;
        for (var index = 0; index < left.Length; index++) sum += left[index] * right[index];
        return sum;
    }

    /// <summary>压缩四元数：2 位最大分量 + 3 个 10 位分量。</summary>
    private static double[] Quat(uint packed)
    {
        var largest = (int)(packed & 3);
        var value = new double[4];
        for (var index = 0; index < 3; index++)
            value[(largest + index + 1) % 4] = (((int)((packed >> (2 + 10 * index)) & 1023)) - 512) * 0.75 / 512;
        value[largest] = Math.Sqrt(Math.Max(0.0, 1 - value.Sum(item => item * item)));
        return value;
    }

    private static double[] CompressedVector(ReadOnlySpan<byte> raw, int offset)
    {
        var result = new double[3];
        for (var index = 0; index < 3; index++)
        {
            var quantized = BinaryPrimitives.ReadUInt16LittleEndian(raw[(offset + 2 * index)..]);
            result[index] = (quantized - 32767) * 10.0 / 32767;
        }
        return result;
    }

    /// <summary>动作头之后按顺序读取每根骨骼的初始平移/旋转/缩放。</summary>
    private ref struct Reader(ReadOnlySpan<byte> raw, int position)
    {
        private readonly ReadOnlySpan<byte> _raw = raw;
        public int Position = position;

        public uint ReadUInt32()
        {
            Require(_raw.Length >= Position + 4, "动作骨架截断");
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_raw[Position..]);
            Position += 4;
            return value;
        }

        public double[] Vector(bool compressed)
        {
            if (!compressed) return ReadRaw(3);
            Require(_raw.Length >= Position + 6, "动作骨架截断");
            var value = CompressedVector(_raw, Position);
            Position += 6;
            return value;
        }

        public double[] ReadRaw(int count)
        {
            Require(_raw.Length >= Position + 4 * count, "动作骨架截断");
            var value = new double[count];
            for (var index = 0; index < count; index++)
                value[index] = BinaryPrimitives.ReadSingleLittleEndian(_raw[(Position + 4 * index)..]);
            Position += 4 * count;
            return value;
        }
    }
}
