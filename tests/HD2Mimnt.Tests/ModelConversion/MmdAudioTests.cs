using System.Buffers.Binary;
using HD2Mimnt.Infrastructure.ModelConversion.Mmd;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

/// <summary>MS ADPCM 解码与 32 kHz→48 kHz 重采样。</summary>
public class MmdAudioTests
{
    [Fact]
    public void Decode_MsAdpcmBlock_MatchesSpecifiedPredictor()
    {
        var decoded = MmdAudio.Decode(MmdAdpcmFixtures.Audio());
        Assert.Equal(new short[] { 0, 0, 0, 0, 16, 32, 0, 32 }, decoded);
    }

    [Fact]
    public void To48kWav_ResamplesToTargetRateAndWritesPcmHeader()
    {
        var wav = MmdAudio.To48kWav(MmdAdpcmFixtures.Audio());
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal(2, (int)BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(192_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(16, (int)BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(44 + 24, wav.Length);
    }
}
