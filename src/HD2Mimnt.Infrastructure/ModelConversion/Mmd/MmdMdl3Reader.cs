using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HD2Mimnt.Infrastructure.ModelConversion.Mmd;

/// <summary>MDL3 舞蹈数据包（注册表 + 镜头采样器 + MS-ADPCM 音轨）。</summary>
public sealed record MmdMdl3Package(
    string Id,
    string Label,
    double Duration,
    IReadOnlyList<double> Stage,
    string? Path,
    IReadOnlyList<string> Resources,
    byte[]? Camera,
    int Fps,
    MmdMdl3Audio? Audio);

/// <summary>
/// MDL3 数据包读取：动作仍为 90 骨骼 Stingray 片段，镜头是烘焙到 30 FPS 的 8 浮点采样行，
/// 音轨是 32 kHz 双声道 MS-ADPCM。识别只依赖 Lua 脚本自身结构，不需要运行时。
/// </summary>
public static class MmdMdl3Reader
{
    private const int ScriptLimit = 8 * 1024 * 1024;
    private static readonly Regex Identifier = new("\\A[a-z0-9_]+\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ResourceId = new("\\A[0-9a-fA-F]{16}\\z", RegexOptions.CultureInvariant);

    public static MmdMdl3Package? TryRead(IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> entries)
    {
        string? registration = null;
        string? cameraCode = null;
        string? audioCode = null;
        foreach (var (key, entry) in entries)
        {
            if (key.Type != IasResourceTypes.Lua || entry.Toc.Length < 8) continue;
            if (!TryUnwrap(entry.Toc, out var code)) continue;
            if (code.StartsWith("return function(PoseMath)", StringComparison.Ordinal))
            {
                if (cameraCode is not null) throw new InvalidDataException("MDL3 镜头采样器不唯一。");
                cameraCode = code;
            }
            else if (code.Contains("data=table.concat(", StringComparison.Ordinal))
            {
                if (audioCode is not null) throw new InvalidDataException("MDL3 音轨数据不唯一。");
                audioCode = code;
            }
            else if (code.StartsWith("return {", StringComparison.Ordinal)
                     && code.Contains("[\"resources\"]", StringComparison.Ordinal)
                     && code.Contains("[\"duration\"]", StringComparison.Ordinal))
            {
                if (registration is not null) throw new InvalidDataException("MDL3 曲目登记不唯一。");
                registration = code;
            }
        }
        if (registration is null) return null;

        var id = Match(registration, "\\[\"id\"\\]\\s*=\\s*\"([^\"]*)\"")
                 ?? throw new InvalidDataException("MDL3 曲目登记缺少标识。");
        if (!Identifier.IsMatch(id)) throw new InvalidDataException("MDL3 曲目标识无效。");
        var label = Unescape(Match(registration, "\\[\"label\"\\]\\s*=\\s*\"((?:\\\\.|[^\"\\\\])*)\"") ?? id);
        var duration = ParseDouble(Match(registration, "\\[\"duration\"\\]\\s*=\\s*([0-9.]+)")
                                   ?? throw new InvalidDataException("MDL3 曲目登记缺少时长。"));
        if (!(duration > 0) || duration > 900)
            throw new InvalidDataException("MDL3 曲目时长无效。");
        var stage = (Match(registration, "\\[\"stage_offset\"\\]\\s*=\\s*\\{([^}]*)\\}")
                     ?? "0,0,0").Split(',')
            .Select(value => ParseDouble(value.Trim())).ToList();
        if (stage.Count != 3 || stage.Any(value => Math.Abs(value) >= 100))
            throw new InvalidDataException("MDL3 舞台偏移无效。");
        var resources = Regex.Matches(Match(registration, "\\[\"resources\"\\]\\s*=\\s*\\{([^}]*)\\}") ?? string.Empty,
                "\"([0-9a-fA-F]{16})\"")
            .Select(match => match.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (resources.Count == 0) throw new InvalidDataException("MDL3 曲目登记没有动作资源。");
        var trackPath = Match(registration, "\\[\"path\"\\]\\s*=\\s*\"([^\"]*)\"");

        var fps = 30;
        byte[]? camera = null;
        if (cameraCode is not null)
            (fps, camera) = MmdAdapter.ParseCameraBlob(cameraCode);
        var audio = audioCode is null ? null : ParseAudio(audioCode);
        return new MmdMdl3Package(id, label, duration, stage, trackPath, resources, camera, fps, audio);
    }

    private static MmdMdl3Audio ParseAudio(string code)
    {
        var format = Convert.FromBase64String(Match(code, "format='([A-Za-z0-9+/=]*)'")
                                              ?? throw new InvalidDataException("MDL3 音轨缺少格式头。"));
        var bytes = int.Parse(Match(code, "bytes=(\\d+)") ?? throw new InvalidDataException("MDL3 音轨缺少长度。"),
            CultureInfo.InvariantCulture);
        if (format.Length < 16) throw new InvalidDataException("MDL3 音轨格式头无效。");
        var rate = Match(code, "rate=(\\d+)") is { } rateText
            ? int.Parse(rateText, CultureInfo.InvariantCulture)
            : BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)) is var headerRate and > 0 and <= 192_000
                ? (int)headerRate
                : throw new InvalidDataException("MDL3 音轨缺少采样率。");
        var channels = Match(code, "channels=(\\d+)") is { } channelText
            ? int.Parse(channelText, CultureInfo.InvariantCulture)
            : BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2)) is var headerChannels and > 0 and <= 8
                ? headerChannels
                : throw new InvalidDataException("MDL3 音轨缺少声道数。");
        var sha = Match(code, "sha256='([a-f0-9]{64})'")
                  ?? throw new InvalidDataException("MDL3 音轨缺少摘要。");
        var start = code.IndexOf("data=table.concat({", StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException("MDL3 音轨缺少数据。");
        var end = code.IndexOf("})", start, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("MDL3 音轨数据截断。");
        using var buffer = new MemoryStream(bytes);
        foreach (Match chunk in Regex.Matches(code[start..end], "'([A-Za-z0-9+/=]+)'"))
        {
            try { buffer.Write(Convert.FromBase64String(chunk.Groups[1].Value)); }
            catch (FormatException error) { throw new InvalidDataException("MDL3 音轨编码无效。", error); }
        }
        var data = buffer.ToArray();
        if (data.Length != bytes || IasHash.Sha256Hex(data) != sha)
            throw new InvalidDataException("MDL3 音轨数据与登记不一致。");
        return new MmdMdl3Audio(data, format, rate, channels);
    }

    private static bool TryUnwrap(byte[] toc, out string code)
    {
        code = string.Empty;
        if (toc.Length < 8) return false;
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(toc);
        if (length != toc.Length - 8 || toc.Length - 8 > ScriptLimit) return false;
        code = Encoding.UTF8.GetString(toc, 8, (int)length);
        return true;
    }

    private static string? Match(string text, string pattern)
        => Regex.Match(text, pattern, RegexOptions.CultureInvariant) is { Success: true } match
            ? match.Groups[1].Value
            : null;

    private static double ParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
           && double.IsFinite(result)
            ? result
            : throw new InvalidDataException("MDL3 数值字段无效。");

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch != '\\' || index + 1 >= value.Length)
            {
                builder.Append(ch);
                continue;
            }
            var next = value[++index];
            builder.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => next });
        }
        return builder.ToString();
    }
}
