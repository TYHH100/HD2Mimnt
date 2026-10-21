using System.Text;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;

namespace HD2Mimnt.Tests.Services;

internal static class SoundFixFixture
{
    internal const ulong BankType = 6006249203084351385UL;
    internal const ulong DependencyType = 12624162998411505776UL;
    internal static byte[] Bank(params (uint Id, byte[] Data)[] media)
    {
        using var bank = new MemoryStream();
        using var didx = new MemoryStream();
        using var data = new MemoryStream();
        using var hirc = new MemoryStream();
        using var writer = new BinaryWriter(hirc, Encoding.UTF8, true);
        writer.Write(media.Length);
        foreach (var (id, payload) in media)
        {
            using var index = new BinaryWriter(didx, Encoding.UTF8, true);
            index.Write(id); index.Write((uint)data.Length); index.Write(payload.Length); data.Write(payload);
            writer.Write((byte)2); writer.Write(21); writer.Write(1000u + id);
            writer.Write(262145u); writer.Write((byte)0); writer.Write(id); writer.Write(0u); writer.Write(payload.Length);
        }
        void Chunk(string tag, byte[] payload)
        {
            using var chunk = new BinaryWriter(bank, Encoding.UTF8, true);
            chunk.Write(Encoding.ASCII.GetBytes(tag)); chunk.Write(payload.Length); chunk.Write(payload);
        }
        Chunk("BKHD", new byte[16]); Chunk("DIDX", didx.ToArray()); Chunk("DATA", data.ToArray()); Chunk("HIRC", hirc.ToArray());
        return bank.ToArray();
    }

    internal static Resource Sound(ulong id, byte[] bank, byte[]? stream = null)
    {
        var envelope = new byte[16 + bank.Length];
        Put32(envelope, 0, 0x78762fd8); Put32(envelope, 4, (uint)bank.Length); Put64(envelope, 8, id);
        bank.CopyTo(envelope, 16);
        return new(id, BankType, envelope, Stream: stream);
    }

    internal static Resource Name(ulong id, string name)
    {
        var text = Encoding.UTF8.GetBytes(name + "\0");
        var data = new byte[8 + text.Length]; Put32(data, 4, (uint)text.Length); text.CopyTo(data, 8);
        return new(id, DependencyType, data);
    }

    internal static byte[] ResourceData(byte[] patch, ulong id, ulong type = BankType)
    {
        var count = BitConverter.ToUInt32(patch, 8);
        var start = 72 + (int)BitConverter.ToUInt32(patch, 4) * 32;
        for (var i = 0; i < count; i++)
        {
            var offset = start + i * 80;
            if (BitConverter.ToUInt64(patch, offset) != id || BitConverter.ToUInt64(patch, offset + 8) != type) continue;
            var dataOffset = (int)BitConverter.ToUInt64(patch, offset + 16);
            return patch.AsSpan(dataOffset, (int)BitConverter.ToUInt32(patch, offset + 56)).ToArray();
        }
        throw new InvalidDataException("Resource not found");
    }

    internal static Dictionary<string, byte[]> Chunks(byte[] bank)
    {
        var result = new Dictionary<string, byte[]>();
        for (var offset = 0; offset < bank.Length;)
        {
            var size = BitConverter.ToInt32(bank, offset + 4);
            result.Add(Encoding.ASCII.GetString(bank, offset, 4), bank.AsSpan(offset + 8, size).ToArray()); offset += size + 8;
        }
        return result;
    }

    internal sealed class Reader : IGameSoundBankReader
    {
        public Dictionary<ulong, SoundBankReference> Banks { get; } = [];
        public bool WaitForCancellation { get; set; }
        public async Task<IReadOnlyDictionary<ulong, SoundBankReference>> ReadSoundBanksAsync(string directory,
            IReadOnlyCollection<ulong> ids, CancellationToken cancellationToken = default)
        {
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return ids.ToDictionary(id => id, id => Banks.GetValueOrDefault(id) ?? new(null, "missing reference"));
        }
    }
}
