namespace HD2Mimnt.ModelConversion.Source.Gma;

/// <summary>IEEE 802.3 CRC-32 (the zlib/gmad convention).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var crc = 0xFFFFFFFFu;
        while (!data.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(data.Length, 64 * 1024);
            foreach (var value in data[..count])
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            data = data[count..];
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
