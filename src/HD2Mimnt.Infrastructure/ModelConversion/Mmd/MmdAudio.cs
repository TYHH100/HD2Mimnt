using System.Buffers.Binary;

namespace HD2Mimnt.Infrastructure.ModelConversion.Mmd;

/// <summary>MDL3 数据包里的音轨：MS-ADPCM 编码载荷与 WAVEFORMATEX 头。</summary>
public sealed record MmdMdl3Audio(byte[] Data, byte[] Format, int Rate, int Channels);

/// <summary>
/// MDL3 音轨转换：MS-ADPCM（512 样本块）解码为 PCM16，再重采样为 HD2CL 数据包要求的 48 kHz。
/// 解码布局与 MS ADPCM 规范（ffmpeg adpcm_ms）一致：立体声头部按字段交错，
/// 数据区每字节高半字节为左声道、低半字节为右声道。
/// </summary>
public static class MmdAudio
{
    private static readonly int[] AdaptationTable =
        [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];

    public static byte[] To48kWav(MmdMdl3Audio audio)
    {
        if (audio.Channels != 2) throw new InvalidDataException("MDL3 音轨需要双声道。");
        var pcm = Decode(audio);
        var resampled = audio.Rate == 48_000
            ? pcm
            : Resample(pcm, audio.Rate, 48_000);
        return Wav(resampled);
    }

    /// <summary>按块解码为交错排列的 PCM16 样本。</summary>
    public static short[] Decode(MmdMdl3Audio audio)
    {
        var format = audio.Format;
        if (format.Length < 22) throw new InvalidDataException("MDL3 音轨格式头无效。");
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        if (tag != 2) throw new InvalidDataException("MDL3 音轨不是 MS-ADPCM。");
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12));
        if (channels != audio.Channels || rate != audio.Rate)
            throw new InvalidDataException("MDL3 音轨格式头与登记不一致。");
        if (format.Length < 22 + 4 * BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(20)))
            throw new InvalidDataException("MDL3 音轨系数表无效。");
        var samplesPerBlock = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(18));
        var coefficients = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(20));
        if (channels != 2 || samplesPerBlock < 2 || blockAlign < 7 * channels + 1
            || coefficients is 0 or > 7)
            throw new InvalidDataException("MDL3 音轨格式头无效。");
        var coefficient1 = new int[coefficients];
        var coefficient2 = new int[coefficients];
        for (var index = 0; index < coefficients; index++)
        {
            coefficient1[index] = BinaryPrimitives.ReadInt16LittleEndian(format.AsSpan(22 + 4 * index));
            coefficient2[index] = BinaryPrimitives.ReadInt16LittleEndian(format.AsSpan(24 + 4 * index));
        }

        if (audio.Data.Length == 0 || audio.Data.Length % blockAlign != 0)
            throw new InvalidDataException("MDL3 音轨块长度无效。");
        var blocks = audio.Data.Length / blockAlign;
        var result = new short[blocks * (long)samplesPerBlock * channels];
        var position = 0;
        for (var block = 0; block < blocks; block++)
        {
            var offset = block * blockAlign;
            var predictors = new int[2];
            var deltas = new int[2];
            var sample1 = new int[2];
            var sample2 = new int[2];
            for (var channel = 0; channel < 2; channel++)
            {
                predictors[channel] = audio.Data[offset + channel];
                if (predictors[channel] >= coefficients)
                    throw new InvalidDataException("MDL3 音轨预测系数越界。");
            }
            for (var channel = 0; channel < 2; channel++)
                deltas[channel] = BinaryPrimitives.ReadInt16LittleEndian(audio.Data.AsSpan(offset + 2 + 2 * channel));
            for (var channel = 0; channel < 2; channel++)
                sample1[channel] = BinaryPrimitives.ReadInt16LittleEndian(audio.Data.AsSpan(offset + 6 + 2 * channel));
            for (var channel = 0; channel < 2; channel++)
                sample2[channel] = BinaryPrimitives.ReadInt16LittleEndian(audio.Data.AsSpan(offset + 10 + 2 * channel));

            for (var channel = 0; channel < 2; channel++) result[position++] = (short)sample2[channel];
            for (var channel = 0; channel < 2; channel++) result[position++] = (short)sample1[channel];
            var bytes = samplesPerBlock - 2;
            var dataOffset = offset + 7 * 2;
            if (dataOffset + bytes > offset + blockAlign)
                throw new InvalidDataException("MDL3 音轨块长度无效。");
            for (var index = 0; index < bytes; index++)
            {
                var value = audio.Data[dataOffset + index];
                result[position++] = Expand(value >> 4, sample1, sample2, deltas, predictors,
                    coefficient1, coefficient2, 0);
                result[position++] = Expand(value & 0x0F, sample1, sample2, deltas, predictors,
                    coefficient1, coefficient2, 1);
            }
        }
        return result;
    }

    private static short Expand(int nibble, int[] sample1, int[] sample2, int[] deltas,
        int[] predictors, int[] coefficient1, int[] coefficient2, int channel)
    {
        var predictor = (sample1[channel] * coefficient1[predictors[channel]]
                         + sample2[channel] * coefficient2[predictors[channel]]) / 256;
        predictor += ((nibble & 0x08) != 0 ? nibble - 16 : nibble) * deltas[channel];
        sample2[channel] = sample1[channel];
        sample1[channel] = Math.Clamp(predictor, short.MinValue, short.MaxValue);
        deltas[channel] = (AdaptationTable[nibble] * deltas[channel]) >> 8;
        if (deltas[channel] < 16) deltas[channel] = 16;
        return (short)sample1[channel];
    }

    /// <summary>Catmull-Rom 三次插值重采样；输入输出都是交错 PCM16。</summary>
    public static short[] Resample(short[] source, int sourceRate, int targetRate)
    {
        var frames = source.Length / 2;
        if (frames == 0) return [];
        var targetFrames = (int)Math.Round((double)frames * targetRate / sourceRate);
        var result = new short[targetFrames * 2];
        var ratio = (double)sourceRate / targetRate;
        for (var frame = 0; frame < targetFrames; frame++)
        {
            var position = frame * ratio;
            var index = (int)Math.Floor(position);
            var fraction = position - index;
            for (var channel = 0; channel < 2; channel++)
            {
                var previous = Sample(source, frames, index - 1, channel);
                var current = Sample(source, frames, index, channel);
                var next = Sample(source, frames, index + 1, channel);
                var after = Sample(source, frames, index + 2, channel);
                var value = 0.5 * (2 * current
                                   + (-previous + next) * fraction
                                   + (2 * previous - 5 * current + 4 * next - after) * fraction * fraction
                                   + (-previous + 3 * current - 3 * next + after) * fraction * fraction * fraction);
                result[frame * 2 + channel] = (short)Math.Clamp(Math.Round(value, MidpointRounding.ToEven),
                    short.MinValue, short.MaxValue);
            }
        }
        return result;
    }

    private static double Sample(short[] source, int frames, int index, int channel)
    {
        var clamped = Math.Clamp(index, 0, frames - 1);
        return source[clamped * 2 + channel];
    }

    public static byte[] Wav(short[] pcm)
    {
        var dataLength = pcm.Length * 2;
        var wav = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(36 + dataLength));
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 48_000);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 192_000);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)dataLength);
        for (var index = 0; index < pcm.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + 2 * index), pcm[index]);
        return wav;
    }
}
