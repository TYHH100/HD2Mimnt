using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HD2Mimnt.Infrastructure.ModelConversion.Mmd;

public sealed record MmdBlob(string Kind, byte[] Data);

public sealed record MmdRegistration(
    IasValue.Object Row,
    string Id,
    string? Title,
    int? Fps,
    double? Duration,
    IReadOnlyList<double>? Stage,
    IReadOnlyDictionary<string, string> Sha256,
    string IdlePlayback,
    bool HasPreview);

public sealed record MmdPacked(IasValue.Object Row, IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> Entries);

/// <summary>
/// 上游 <c>mmd_bundle</c>：独立 MMD 播放包的数据契约与打包/解包。
/// 登记行以单个 Lua 资源承载，动作/镜头/音轨/预览各自按 base64 存放，资源编号由路径 murmur64 决定。
/// </summary>
public static class MmdBundle
{
    public const string Mark = "-- HD2CL_MMD_V1 ";
    public const string Delivery = "manual-adapted-v1";
    public const int PreviewLimit = 8_388_608;
    public static readonly byte[] PreviewMark = Encoding.ASCII.GetBytes("-- HD2CL_PREVIEW_V1\nreturn [==[");

    private static readonly IReadOnlyDictionary<string, int> Limits = new Dictionary<string, int>
    {
        ["pose"] = 41_943_040,
        ["camera"] = 4_194_304,
        ["wav"] = 83_886_080
    };

    private static readonly Regex NamePattern = new("\\A[a-z][a-z0-9_]{0,39}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Hex64 = new("\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex CameraCuts = new("\\A(?:[0-9]+ ?)*\\z", RegexOptions.CultureInvariant);

    public static (ulong Type, ulong Id) Resource(string identity, string kind)
        => (IasResourceTypes.Lua, IasHash.Murmur64("mods/hd2cl/mmd/v1/" + identity + "/" + kind));

    public static byte[] WrapLua(byte[] raw)
    {
        var result = new byte[8 + raw.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)raw.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        raw.CopyTo(result, 8);
        return result;
    }

    /// <summary>上游 <c>source</c>：剥离 Lua 数据资源封装，校验容量。</summary>
    public static ReadOnlySpan<byte> UnwrapLua(ReadOnlySpan<byte> raw, int limit)
    {
        if (raw.Length < 8 || raw.Length > limit + 8
            || BinaryPrimitives.ReadUInt32LittleEndian(raw) != raw.Length - 8
            || BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]) != 2)
            throw new InvalidDataException("MMD Lua 数据资源封装无效或过大。");
        return raw[8..];
    }

    public static string ValidateIdle(string? value)
        => value is "loop" or "once"
            ? value
            : throw new InvalidDataException("待机播放方式必须为 loop（持续姿势）或 once（完整演出）。");

    public static MmdRegistration ParseRegistration(ReadOnlySpan<byte> toc, bool manual)
    {
        var code = UnwrapLua(toc, 16_384);
        if (!code.StartsWith(Encoding.ASCII.GetBytes(Mark)))
            throw new InvalidDataException("不是独立 MMD 数据包。");
        var body = code[Mark.Length..];
        var newline = body.IndexOf((byte)'\n');
        var line = newline >= 0 ? body[..newline] : body;
        if (MmdJson.Parse(line) is not IasValue.Object row)
            throw new InvalidDataException("MMD 曲包登记字段无效。");
        var registration = Describe(row);
        if (manual && MmdJson.Text(row, "delivery") != Delivery)
            throw new InvalidDataException("这是旧曲包/旧缓存资源，请先通过手动适配工具生成成品。");
        return registration;
    }

    /// <summary>只解析登记行字段（打包时复用自己的行，不需要 Mark）。</summary>
    public static MmdRegistration Describe(IasValue.Object row)
    {
        var sha = new Dictionary<string, string>(StringComparer.Ordinal);
        if (MmdJson.Get(row, "sha256") is IasValue.Object hash)
        {
            foreach (var (key, value) in hash.Entries)
            {
                if (value is not IasValue.Text text || !Hex64.IsMatch(text.Value))
                    throw new InvalidDataException("MMD 曲包登记字段无效。");
                sha[key] = text.Value;
            }
        }
        var id = MmdJson.Text(row, "id");
        if (MmdJson.Integer(row, "protocol") is not 1 || id is null || !NamePattern.IsMatch(id)
            || sha.Count < 1 || sha.Keys.Any(key => !Limits.ContainsKey(key)) || !sha.ContainsKey("pose"))
            throw new InvalidDataException("MMD 曲包登记字段无效。");
        var idle = ValidateIdle(MmdJson.Text(row, "idle_playback") ?? "loop");
        var stage = MmdJson.Get(row, "stage") is IasValue.Array array
            ? array.Items.Select(item => item switch
            {
                IasValue.Integer integer => (double)integer.Value,
                IasValue.Float value when double.IsFinite(value.Value) => value.Value,
                _ => double.NaN
            }).ToList()
            : null;
        return new MmdRegistration(row, id, MmdJson.Text(row, "title"),
            (int?)MmdJson.Integer(row, "fps"),
            MmdJson.Number(row, "duration"),
            stage, sha, idle, MmdJson.Get(row, "preview") is not null);
    }

    public static int Check(MmdRegistration row, IReadOnlyList<MmdBlob> blobs)
    {
        ValidateIdle(row.IdlePlayback);
        var kinds = blobs.Select(blob => blob.Kind).ToHashSet(StringComparer.Ordinal);
        if (kinds.Count != blobs.Count || !kinds.SetEquals(row.Sha256.Keys))
            throw new InvalidDataException("MMD 媒体声明不完整。");
        foreach (var blob in blobs)
        {
            if (blob.Data.Length > Limits[blob.Kind] || IasHash.Sha256Hex(blob.Data) != row.Sha256[blob.Kind])
                throw new InvalidDataException("MMD 媒体大小或摘要不匹配：" + blob.Kind);
        }

        var pose = blobs.First(blob => blob.Kind == "pose").Data;
        var frames = MmdMotion.CheckPose(pose);
        var title = row.Title;
        if (title is null || RuneLength(title) is not (>= 1 and <= 120)
            || row.Fps is not (>= 1 and <= 120)
            || row.Duration is not { } duration || !double.IsFinite(duration) || !(duration > 0) || duration > 900
            || row.Stage is not { Count: 3 } stage || stage.Any(value => !double.IsFinite(value) || Math.Abs(value) >= 100)
            || Math.Abs((frames - 1.0) / row.Fps.Value - duration) > 0.01)
            throw new InvalidDataException("MMD 标题、帧率、时长或舞台偏移无效。");

        var camera = blobs.FirstOrDefault(blob => blob.Kind == "camera")?.Data;
        if (camera is not null) CheckCamera(camera, frames);
        var wav = blobs.FirstOrDefault(blob => blob.Kind == "wav")?.Data;
        if (wav is not null) CheckWav(wav);
        return frames;
    }

    private static void CheckCamera(byte[] camera, int frames)
    {
        var lines = SplitLines(camera);
        if (lines.Count != frames + 1 || !CameraCuts.IsMatch(lines[0]))
            throw new InvalidDataException("MMD 镜头帧数或切点无效。");
        if (lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(cut => int.Parse(cut, CultureInfo.InvariantCulture) >= frames))
            throw new InvalidDataException("MMD 镜头切点越界。");
        foreach (var line in lines.Skip(1))
        {
            var values = line.Split(',');
            var parsed = new double[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                if (!double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[index])
                    || !double.IsFinite(parsed[index]))
                    throw new InvalidDataException("MMD 镜头姿态无效。");
            }
            if (parsed.Length != 8 || !(parsed[^1] > 0 && parsed[^1] < 180))
                throw new InvalidDataException("MMD 镜头姿态无效。");
        }
    }

    private static void CheckWav(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("MMD 音轨需要完整的 48 kHz 双声道 PCM。");
        var position = 12;
        int channels = 0, bits = 0, format = -1;
        long sampleRate = 0, dataSize = -1;
        while (position + 8 <= wav.Length)
        {
            var id = wav.AsSpan(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(position + 4));
            var body = position + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (body + 16 > wav.Length) break;
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14));
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataSize = size;
                break;
            }
            position = body + (int)size + (int)(size & 1);
        }
        if (format != 1 || channels != 2 || sampleRate != 48_000 || bits != 16 || dataSize <= 0 || dataSize % 4 != 0)
            throw new InvalidDataException("MMD 音轨需要完整的 48 kHz 双声道 PCM。");
    }

    public static IasValue.Object PreviewInfo(string identity, byte[] raw)
    {
        var mime = raw.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) ? "image/png"
            : raw.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) ? "image/jpeg"
            : raw.AsSpan().StartsWith("GIF87a"u8) || raw.AsSpan().StartsWith("GIF89a"u8) ? "image/gif"
            : raw.AsSpan().StartsWith("RIFF"u8) && raw.AsSpan(8, 4).SequenceEqual("WEBP"u8) ? "image/webp"
            : raw.AsSpan().StartsWith("BM"u8) ? "image/bmp"
            : throw new InvalidDataException("原图格式无法确认，请补充 PNG/JPEG/WebP/GIF/BMP 原图。");
        if (raw.Length is <= 0 or > PreviewLimit) throw new InvalidDataException("MMD 预览图超出支持的 8 MiB。");
        return (IasValue.Object)IasValue.Obj(
            ("version", IasValue.Of(1L)),
            ("module", IasValue.Of("mods/hd2cl/mmd/v1/" + identity + "/preview")),
            ("encoding", IasValue.Of("base64")),
            ("mime", IasValue.Of(mime)),
            ("bytes", IasValue.Of((long)raw.Length)),
            ("sha256", IasValue.Of(IasHash.Sha256Hex(raw))));
    }

    public static IReadOnlySet<(ulong Type, ulong Id)> ExpectedResources(MmdRegistration row)
    {
        var expected = new HashSet<(ulong, ulong)> { Resource(row.Id, "catalog") };
        foreach (var kind in row.Sha256.Keys) expected.Add(Resource(row.Id, kind));
        if (MmdJson.Get(row.Row, "preview") is { } preview)
        {
            if (preview is not IasValue.Object info
                || MmdJson.Integer(info, "version") != 1
                || MmdJson.Text(info, "encoding") != "base64"
                || MmdJson.Text(info, "module") != "mods/hd2cl/mmd/v1/" + row.Id + "/preview"
                || MmdJson.Integer(info, "bytes") is not { } bytes || bytes <= 0 || bytes > PreviewLimit
                || MmdJson.Text(info, "sha256") is not { } sha || !Hex64.IsMatch(sha))
                throw new InvalidDataException("MMD 预览图登记无效。");
            expected.Add(Resource(row.Id, "preview"));
        }
        return expected;
    }

    public static MmdPacked Pack(IasValue.Object row, IReadOnlyList<MmdBlob> blobs, byte[]? preview)
    {
        var identity = MmdJson.Text(row, "id")
                       ?? throw new InvalidDataException("MMD 曲包登记字段无效。");
        var hashes = new IasValue.Object(blobs
            .Select(blob => new KeyValuePair<string, IasValue>(blob.Kind, IasValue.Of(IasHash.Sha256Hex(blob.Data))))
            .ToList());
        var packed = MmdJson.With(row, "protocol", IasValue.Of(1L));
        packed = MmdJson.With(packed, "delivery", IasValue.Of(Delivery));
        packed = MmdJson.With(packed, "sha256", hashes);
        packed = MmdJson.With(packed, "idle_playback", IasValue.Of(ValidateIdle(MmdJson.Text(row, "idle_playback") ?? "loop")));
        packed = MmdJson.Without(packed, "preview");
        if (preview is not null) packed = MmdJson.With(packed, "preview", PreviewInfo(identity, preview));
        Check(Describe(packed), blobs);

        var code = new List<byte>(Encoding.ASCII.GetBytes(Mark));
        code.AddRange(Encoding.UTF8.GetBytes(IasLua.WriteJson(packed, ensureAscii: false, sortKeys: false, indent: 0)));
        code.AddRange("\nreturn false\n"u8.ToArray());
        var entries = new Dictionary<(ulong Type, ulong Id), IasResource>
        {
            [Resource(identity, "catalog")] = new(WrapLua(code.ToArray()), [], [])
        };
        foreach (var blob in blobs)
        {
            var encoded = "return [[" + Convert.ToBase64String(blob.Data) + "]]\n";
            entries[Resource(identity, blob.Kind)] = new(WrapLua(Encoding.ASCII.GetBytes(encoded)), [], []);
        }
        if (preview is not null)
        {
            var raw = PreviewMark.Concat(Encoding.ASCII.GetBytes(Convert.ToBase64String(preview)))
                .Concat(Encoding.ASCII.GetBytes("]==]\n")).ToArray();
            entries[Resource(identity, "preview")] = new(WrapLua(raw), [], []);
        }
        return new MmdPacked(packed, entries);
    }

    public static (MmdRegistration Registration, IReadOnlyList<MmdBlob> Blobs) Unpack(
        IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> entries, bool manual = true)
    {
        if (entries.Values.Any(value => value.Gpu.Length > 0 || value.Stream.Length > 0))
            throw new InvalidDataException("MMD 数据包带未声明的 GPU/stream 载荷。");
        var markers = entries
            .Where(entry => entry.Key.Type == IasResourceTypes.Lua
                && UnwrapLua(entry.Value.Toc, Math.Max(entry.Value.Toc.Length, 8)).StartsWith(Encoding.ASCII.GetBytes(Mark)))
            .ToList();
        if (markers.Count != 1) throw new InvalidDataException("独立 MMD 曲包登记不唯一。");
        var registration = ParseRegistration(markers[0].Value.Toc, manual);
        if (markers[0].Key != Resource(registration.Id, "catalog")
            || !entries.Keys.ToHashSet().SetEquals(ExpectedResources(registration)))
            throw new InvalidDataException("MMD 曲包包含未声明资源或私有标识不匹配。");

        var blobs = new List<MmdBlob>();
        foreach (var (kind, _) in registration.Sha256)
        {
            var entry = entries[Resource(registration.Id, kind)];
            var encoded = UnwrapLua(entry.Toc, Limits[kind] * 4 / 3 + 32);
            if (!encoded.StartsWith("return [["u8) || !encoded.EndsWith("]]\n"u8))
                throw new InvalidDataException("MMD 曲包编码无效。");
            blobs.Add(new MmdBlob(kind, DecodeBase64(encoded[9..^3])));
        }
        Check(registration, blobs);
        ReadPreview(entries, registration);
        return (registration, blobs);
    }

    public static byte[]? ReadPreview(IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> entries, MmdRegistration row)
    {
        if (!row.HasPreview) return null;
        ExpectedResources(row);
        var entry = entries[Resource(row.Id, "preview")];
        if (entry.Gpu.Length > 0 || entry.Stream.Length > 0)
            throw new InvalidDataException("MMD 预览数据包含未声明伴随载荷。");
        var code = UnwrapLua(entry.Toc, PreviewLimit * 4 / 3 + 128);
        if (!code.StartsWith(PreviewMark) || !code.EndsWith("]==]\n"u8))
            throw new InvalidDataException("MMD 预览图编码无效。");
        var raw = DecodeBase64(code[PreviewMark.Length..^5]);
        var declared = MmdJson.Get(row.Row, "preview")!;
        if (IasLua.WriteJson(PreviewInfo(row.Id, raw), false, false, 0)
            != IasLua.WriteJson(declared, false, false, 0))
            throw new InvalidDataException("MMD 预览图与原图登记不匹配。");
        return raw;
    }

    public static string RowJson(IasValue.Object row) => IasLua.WriteJson(row, ensureAscii: false, sortKeys: false, indent: 0);

    private static byte[] DecodeBase64(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length % 4 != 0)
            throw new InvalidDataException("MMD 曲包编码无效。");
        foreach (var value in encoded)
        {
            if (value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/' or (byte)'=')
                continue;
            throw new InvalidDataException("MMD 曲包编码无效。");
        }
        try { return Convert.FromBase64String(Encoding.ASCII.GetString(encoded)); }
        catch (FormatException error) { throw new InvalidDataException("MMD 曲包编码无效。", error); }
    }

    /// <summary>与 Python <c>bytes.splitlines()</c> 一致：不保留末尾空行。</summary>
    private static List<string> SplitLines(byte[] raw)
    {
        var text = Encoding.ASCII.GetString(raw);
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch is not ('\n' or '\r')) continue;
            lines.Add(text[start..index]);
            if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            start = index + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    private static int RuneLength(string text) => text.EnumerateRunes().Count();
}
