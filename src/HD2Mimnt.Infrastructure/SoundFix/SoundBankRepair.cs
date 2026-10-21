// 改编自用户提供的「潜潜音效修复软件」v1.0 源码。
// 原作者：https://space.bilibili.com/157681854
// 原声明与许可状态见 ThirdParty/HD2SoundFix/；未将上游标为本项目许可证。
#nullable disable
using System.Text;
namespace HD2Mimnt.Infrastructure.SoundFix;

internal static class SoundBankRepair
{
    internal const ulong BankType = 6006249203084351385UL;

    internal static byte[] RebuildPackage(Package package, IReadOnlyDictionary<ulong, byte[]> banks)
    {
        using var output = new MemoryStream();
        output.Write(package.Raw);
        for (int i = 0; i < package.Entries.Count; i++)
        {
            var entry = package.Entries[i];
            if (entry.TypeId != BankType || !banks.TryGetValue(entry.FileId, out var bank)) continue;
            while ((output.Length & 15) != 0) output.WriteByte(0);
            long offset = output.Position;
            var original = package.TocData(entry);
            if (original.Length < 16) throw new InvalidDataException("音库 envelope 不完整。");
            byte[] header = original[..16];
            WriteU32(header, 4, (uint)bank.Length);
            output.Write(header);
            output.Write(bank);
            long end = output.Position;
            output.Position = 72 + package.Types.Count * 32L + i * 80L + 16;
            WriteU64(output, (ulong)offset);
            output.Position += 32;
            WriteU32(output, (uint)(16 + bank.Length));
            output.Position = end;
        }
        var result = output.ToArray();
        var check = Package.Load(result, package.Path);
        foreach (var entry in check.Entries.Where(e => e.TypeId == BankType && banks.ContainsKey(e.FileId)))
            if (!check.TocData(entry).AsSpan(16).SequenceEqual(banks[entry.FileId])) throw new InvalidDataException("补丁重建复检失败。");
        return result;
    }
    internal static FixPlan Evaluate(BankRef r, byte[] officialBank)
    {
        if (string.IsNullOrEmpty(r.BankName)) return null;
        if (r.BankName.EndsWith("/Init", StringComparison.OrdinalIgnoreCase) ||
            r.BankName.EndsWith("/game_init", StringComparison.OrdinalIgnoreCase)) return null;

        byte[] modBank = r.Package.TocData(r.Entry);
        if (modBank == null || modBank.Length < 16) throw new InvalidDataException("音库 envelope 不完整。");
        modBank = Slice(modBank, 16, modBank.Length - 16);

        List<BankChunk> vb, mb;
        try { vb = ParseBank(officialBank); mb = ParseBank(modBank); }
        catch (Exception ex) { throw new InvalidDataException("音库解析失败。", ex); }

        BankChunk vh = Find(vb, "HIRC"), mh = Find(mb, "HIRC");
        if (vh == null || mh == null) throw new InvalidDataException("音库缺少 HIRC。");

        Dictionary<uint, HircEntry> vHirc, mHirc;
        try { vHirc = ParseHirc(vh.Data); mHirc = ParseHirc(mh.Data); }
        catch (Exception ex) { throw new InvalidDataException("音库解析失败。", ex); }

        Dictionary<uint, byte[]> vMedia = BankMedia(vb);
        Dictionary<uint, byte[]> mMedia = BankMedia(mb);

        int missingEntries = 0, missingMedia = 0;
        foreach (uint id in vHirc.Keys) if (!mHirc.ContainsKey(id)) missingEntries++;
        foreach (uint id in vMedia.Keys) if (!mMedia.ContainsKey(id)) missingMedia++;

        if (missingEntries == 0 && missingMedia == 0) return null; // 没缺东西

        int onlyModEntries = 0, onlyModMedia = 0;
        foreach (uint id in mHirc.Keys) if (!vHirc.ContainsKey(id)) onlyModEntries++;
        foreach (uint id in mMedia.Keys) if (!vMedia.ContainsKey(id)) onlyModMedia++;

        bool ratioSafe = onlyModEntries <= Math.Max(20, vHirc.Count / 20)
                      && onlyModMedia <= Math.Max(10, vMedia.Count / 10);
        // 武器/操作类音库即使带少量自定义条目也修（这是本工具的主要用途）；
        // 其他音库（语音、终端、敌人）若带自定义条目，默认跳过，避免丢语音。
        bool safe = ratioSafe && (onlyModEntries == 0 || IsWeaponish(r.BankName));

        return new FixPlan
        {
            Ref = r,
            BankName = r.BankName,
            PatchPath = r.PatchPath,
            OfficialBank = officialBank,
            ModBank = modBank,
            VHirc = vHirc,
            MHirc = mHirc,
            VMedia = vMedia,
            MMedia = mMedia,
            MissingEntries = missingEntries,
            MissingMedia = missingMedia,
            OnlyModEntries = onlyModEntries,
            OnlyModMedia = onlyModMedia,
            Safe = safe
        };
    }

    static bool IsWeaponish(string bankName)
    {
        if (string.IsNullOrEmpty(bankName)) return false;
        string n = bankName.ToLowerInvariant();
        string[] keys = new string[] { "weapon", "/wep_", "pistol", "rifle", "shotgun", "launcher", "melee", "foley", "gun", "bomb", "cannon" };
        foreach (string k in keys) if (n.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    internal static byte[] RebuildBank(FixPlan p)
    {
        // 1) 合并媒体：官方为底，Mod 替换过的按 ID 搬回
        Dictionary<uint, byte[]> merged = new Dictionary<uint, byte[]>(p.VMedia);
        Dictionary<uint, int> changed = new Dictionary<uint, int>();
        foreach (KeyValuePair<uint, byte[]> kv in p.MMedia)
        {
            if (!merged.ContainsKey(kv.Key)) continue;
            if (!BytesEqual(merged[kv.Key], kv.Value))
            {
                merged[kv.Key] = kv.Value;
                changed[kv.Key] = kv.Value.Length;
            }
        }

        // 2) HIRC 用官方，替换过的音源同步 mem_size
        byte[] hirc = (byte[])p.VHircRaw.Clone();
        PatchMemSizes(hirc, changed);

        // 3) 生成 bank
        byte[] newBank = BuildBank(p.OfficialBank, merged, hirc);

        // 4) 校验
        VerifyBank(newBank, merged, changed);

        return newBank;
    }

    static void VerifyBank(byte[] bank, Dictionary<uint, byte[]> media, Dictionary<uint, int> changed)
    {
        List<BankChunk> chunks = ParseBank(bank);
        BankChunk h = Find(chunks, "HIRC");
        Dictionary<uint, HircEntry> ids = ParseHirc(h.Data);
        int mediaCount = BankMedia(chunks).Count;
        if (mediaCount != media.Count)
            throw new Exception("校验失败：媒体数量不一致 " + mediaCount + "/" + media.Count);

        Dictionary<uint, byte[]> mm = BankMedia(chunks);
        foreach (HircEntry e in ids.Values)
        {
            if (e.Type != 2) continue;
            int body = e.Offset + 9;
            uint plugin = U32(h.Data, body);
            if (plugin != 262145) continue;
            byte stype = h.Data[body + 4];
            uint sid = U32(h.Data, body + 5);
            if (stype != 0) continue;
            byte[] data;
            if (!mm.TryGetValue(sid, out data)) throw new Exception("校验失败：音源无媒体 " + sid.ToString("X8"));
            uint mem = U32(h.Data, body + 13);
            if (mem != data.Length) throw new Exception("校验失败：mem_size 不一致 " + sid.ToString("X8"));
        }
    }

    static byte[] BuildBank(byte[] officialBank, Dictionary<uint, byte[]> merged, byte[] patchedHirc)
    {
        List<BankChunk> chunks = ParseBank(officialBank);
        MemoryStream ms = new MemoryStream();
        BankChunk bkhd = Find(chunks, "BKHD");
        if (bkhd == null) throw new Exception("官方 bank 缺少 BKHD");
        WriteChunk(ms, "BKHD", bkhd.Data);

        List<uint> order = merged.Keys.ToList();
        order.Sort();
        MemoryStream didx = new MemoryStream();
        MemoryStream data = new MemoryStream();
        for (int i = 0; i < order.Count; i++)
        {
            byte[] payload = merged[order[i]];
            WriteU32(didx, order[i]);
            WriteU32(didx, (uint)data.Length);
            WriteU32(didx, (uint)payload.Length);
            data.Write(payload, 0, payload.Length);
            if (i != order.Count - 1)
            {
                long pad = (-payload.Length) & 15;
                for (int k = 0; k < pad; k++) data.WriteByte(0);
            }
        }
        WriteChunk(ms, "DIDX", didx.ToArray());
        WriteChunk(ms, "DATA", data.ToArray());
        WriteChunk(ms, "HIRC", patchedHirc);
        foreach (BankChunk c in chunks)
        {
            if (c.Tag == "BKHD" || c.Tag == "DIDX" || c.Tag == "DATA" || c.Tag == "HIRC") continue;
            WriteChunk(ms, c.Tag, c.Data);
        }
        return ms.ToArray();
    }

    static Dictionary<uint, byte[]> BankMedia(List<BankChunk> chunks)
    {
        BankChunk didx = Find(chunks, "DIDX");
        BankChunk data = Find(chunks, "DATA");
        Dictionary<uint, byte[]> map = new Dictionary<uint, byte[]>();
        if (didx == null || data == null) return map;
        if (didx.Data.Length % 12 != 0) throw new InvalidDataException("DIDX 尺寸无效。");
        int n = didx.Data.Length / 12;
        for (int i = 0; i < n; i++)
        {
            uint id = U32(didx.Data, i * 12);
            uint off = U32(didx.Data, i * 12 + 4);
            uint size = U32(didx.Data, i * 12 + 8);
            if ((long)off + size > data.Data.Length) throw new InvalidDataException("DIDX 媒体范围越界。");
            byte[] slice = new byte[size];
            Buffer.BlockCopy(data.Data, (int)off, slice, 0, (int)size);
            if (!map.TryAdd(id, slice)) throw new InvalidDataException("DIDX 存在重复媒体 ID。");
        }
        return map;
    }

    static Dictionary<uint, HircEntry> ParseHirc(byte[] hirc)
    {
        if (hirc.Length < 4) throw new InvalidDataException("HIRC 头不完整。");
        var result = new Dictionary<uint, HircEntry>();
        uint count = U32(hirc, 0);
        int offset = 4;
        for (uint i = 0; i < count; i++)
        {
            if (hirc.Length - offset < 9) throw new InvalidDataException("HIRC 条目不完整。");
            uint size = U32(hirc, offset + 1);
            if (size < 4 || size > hirc.Length - offset - 5) throw new InvalidDataException("HIRC 条目越界。");
            var entry = new HircEntry { Offset = offset, Type = hirc[offset], Size = size, Id = U32(hirc, offset + 5) };
            if (entry.Type == 2 && size < 21) throw new InvalidDataException("HIRC 音源条目不完整。");
            if (!result.TryAdd(entry.Id, entry)) throw new InvalidDataException("HIRC 存在重复 ID。");
            offset += 5 + (int)size;
        }
        return result;
    }

    static void PatchMemSizes(byte[] hirc, Dictionary<uint, int> sizes)
    {
        uint count = U32(hirc, 0);
        int off = 4;
        for (int i = 0; i < count; i++)
        {
            byte type = hirc[off];
            uint size = U32(hirc, off + 1);
            int body = off + 9;
            if (type == 2 && (int)size - 4 >= 17)
            {
                uint plugin = U32(hirc, body);
                if (plugin != 262145) { off += 5 + (int)size; continue; }
                byte stype = hirc[body + 4];
                uint sid = U32(hirc, body + 5);
                int len;
                if (stype == 0 && sizes.TryGetValue(sid, out len))
                    WriteU32(hirc, body + 13, (uint)len);
            }
            off += 5 + (int)size;
        }
    }

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static byte[] Slice(byte[] b, int off, int len)
    {
        if (off < 0 || len <= 0) return new byte[0];
        if (off + len > b.Length) len = b.Length - off;
        byte[] r = new byte[len];
        Buffer.BlockCopy(b, off, r, 0, len);
        return r;
    }

    static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }
    static ulong U64(byte[] b, int o) { return BitConverter.ToUInt64(b, o); }
    static void WriteU32(byte[] b, int o, uint v) { byte[] t = BitConverter.GetBytes(v); Buffer.BlockCopy(t, 0, b, o, 4); }
    static void WriteU64(byte[] b, int o, ulong v) { byte[] t = BitConverter.GetBytes(v); Buffer.BlockCopy(t, 0, b, o, 8); }
    static void WriteU32(Stream s, uint v) { byte[] t = BitConverter.GetBytes(v); s.Write(t, 0, 4); }
    static void WriteU64(Stream s, ulong v) { byte[] t = BitConverter.GetBytes(v); s.Write(t, 0, 8); }
    static void WriteChunk(MemoryStream ms, string tag, byte[] data)
    {
        byte[] t = Encoding.ASCII.GetBytes(tag);
        ms.Write(t, 0, 4);
        WriteU32(ms, (uint)data.Length);
        ms.Write(data, 0, data.Length);
    }
    static BankChunk Find(List<BankChunk> chunks, string tag)
    {
        foreach (BankChunk c in chunks) if (c.Tag == tag) return c;
        return null;
    }

    static List<BankChunk> ParseBank(byte[] bank)
    {
        var chunks = new List<BankChunk>();
        int offset = 0;
        while (offset < bank.Length)
        {
            if (bank.AsSpan(offset).IndexOfAnyExcept((byte)0) < 0) break;
            if (bank.Length - offset < 8) throw new InvalidDataException("音库块头不完整。");
            uint size = U32(bank, offset + 4);
            if (size > bank.Length - offset - 8) throw new InvalidDataException("音库块越界。");
            chunks.Add(new BankChunk { Tag = Encoding.ASCII.GetString(bank, offset, 4), Data = Slice(bank, offset + 8, (int)size) });
            offset += 8 + (int)size;
        }
        if (chunks.Count(c => c.Tag == "HIRC") != 1 || chunks.Count(c => c.Tag == "BKHD") != 1)
            throw new InvalidDataException("音库缺少唯一的 BKHD/HIRC。");
        return chunks;
    }
}

internal class BankChunk
{
    public string Tag;
    public byte[] Data;
}

internal class HircEntry
{
    public int Offset;
    public byte Type;
    public uint Size;
    public uint Id;
}

internal class TypeHeader
{
    public ulong TypeId;
}

internal class TocEntry
{
    public ulong FileId, TypeId, TocDataOffset, StreamFileOffset, GpuOffset, Unk1, Unk2;
    public uint TocDataSize, StreamSize, GpuSize, Unk3, Unk4, Index;
}

internal class Package
{
    public uint Magic, NumTypes, NumFiles, Unknown;
    public byte[] Unk4;
    public List<TypeHeader> Types = new List<TypeHeader>();
    public List<TocEntry> Entries = new List<TocEntry>();
    public byte[] Raw;
    public string Path;

    public static Package Load(byte[] raw, string path)
    {
        if (raw.Length < 72) throw new InvalidDataException("补丁头不完整。");
        Package p = new Package();
        p.Raw = raw;
        p.Path = path;
        p.Magic = BitConverter.ToUInt32(raw, 0);
        if (p.Magic != 0xF0000011u) throw new Exception("不是 HD2 补丁包");
        p.NumTypes = BitConverter.ToUInt32(raw, 4);
        p.NumFiles = BitConverter.ToUInt32(raw, 8);
        p.Unknown = BitConverter.ToUInt32(raw, 12);
        p.Unk4 = new byte[56];
        Buffer.BlockCopy(raw, 16, p.Unk4, 0, 56);
        if (72UL + 32UL * p.NumTypes + 80UL * p.NumFiles > (ulong)raw.Length) throw new InvalidDataException("补丁索引不完整。");
        int off = 72;
        for (int i = 0; i < p.NumTypes; i++)
        {
            TypeHeader th = new TypeHeader();
            th.TypeId = BitConverter.ToUInt64(raw, off + 8);
            p.Types.Add(th);
            off += 32;
        }
        int tocStart = off;
        for (int i = 0; i < p.NumFiles; i++)
        {
            int o = tocStart + i * 80;
            TocEntry e = new TocEntry();
            e.FileId = BitConverter.ToUInt64(raw, o);
            e.TypeId = BitConverter.ToUInt64(raw, o + 8);
            e.TocDataOffset = BitConverter.ToUInt64(raw, o + 16);
            e.StreamFileOffset = BitConverter.ToUInt64(raw, o + 24);
            e.GpuOffset = BitConverter.ToUInt64(raw, o + 32);
            e.Unk1 = BitConverter.ToUInt64(raw, o + 40);
            e.Unk2 = BitConverter.ToUInt64(raw, o + 48);
            e.TocDataSize = BitConverter.ToUInt32(raw, o + 56);
            e.StreamSize = BitConverter.ToUInt32(raw, o + 60);
            e.GpuSize = BitConverter.ToUInt32(raw, o + 64);
            e.Unk3 = BitConverter.ToUInt32(raw, o + 68);
            e.Unk4 = BitConverter.ToUInt32(raw, o + 72);
            e.Index = BitConverter.ToUInt32(raw, o + 76);
            if (e.TocDataOffset > (ulong)raw.Length || e.TocDataSize > (ulong)raw.Length - e.TocDataOffset) throw new InvalidDataException("补丁载荷范围越界。");
            p.Entries.Add(e);
        }
        return p;
    }

    public byte[] TocData(TocEntry e)
    {
        long off = (long)e.TocDataOffset;
        long len = e.TocDataSize;
        if (off < 0 || off + len > Raw.Length) throw new InvalidDataException("音库资源范围越界。");
        byte[] r = new byte[len];
        Buffer.BlockCopy(Raw, (int)off, r, 0, (int)len);
        return r;
    }

    public string DepName(ulong fileId)
    {
        foreach (TocEntry e in Entries)
        {
            if (e.TypeId == 12624162998411505776UL && e.FileId == fileId)
            {
                long off = (long)e.TocDataOffset;
                if (off + 8 > Raw.Length) return null;
                uint size = BitConverter.ToUInt32(Raw, (int)off + 4);
                if (off + 8 + size > Raw.Length) return null;
                return Encoding.UTF8.GetString(Raw, (int)off + 8, (int)size).TrimEnd('\0');
            }
        }
        return null;
    }
}

internal class BankRef
{
    public string PatchPath;
    public Package Package;
    public TocEntry Entry;
    public string BankName;
}

internal class FixPlan
{
    public BankRef Ref;
    public string BankName;
    public string PatchPath;
    public byte[] OfficialBank;
    public byte[] ModBank;
    public Dictionary<uint, HircEntry> VHirc;
    public Dictionary<uint, HircEntry> MHirc;
    public Dictionary<uint, byte[]> VMedia;
    public Dictionary<uint, byte[]> MMedia;
    public byte[] VHircRaw { get { return FindHirc(OfficialBank); } }
    public int MissingEntries;
    public int MissingMedia;
    public int OnlyModEntries;
    public int OnlyModMedia;
    public bool Safe;

    static byte[] FindHirc(byte[] bank)
    {
        int off = 0;
        while (off + 8 <= bank.Length)
        {
            string tag = Encoding.ASCII.GetString(bank, off, 4);
            uint size = BitConverter.ToUInt32(bank, off + 4);
            if (tag == "HIRC") { byte[] r = new byte[size]; Buffer.BlockCopy(bank, off + 8, r, 0, (int)size); return r; }
            off += 8 + (int)size;
        }
        throw new Exception("找不到 HIRC");
    }
}

