using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasMorphChannel(
    string Id, string Name, string SourceObject, string RelativeKey,
    double Min, double Max, double Default, double Baseline, double NormalSampleWeight,
    bool HasDriver, bool Muted);

public sealed record IasMorphComponent(uint Type, uint Format, uint Index, uint Offset, uint Size);
public sealed record IasMorphSection(uint IndexByteOffset, uint IndexCount);
public sealed record IasMorphRecord(int Vertex, float[] Values);

public sealed record IasMorphDelta(string ChannelId, IReadOnlyList<IasMorphRecord> Records);

public sealed record IasMorphMesh(
    string UnitId, uint MeshIndex, int LodIndex, uint StreamIndex, uint VertexOffset,
    uint UnitVersion, uint VertexCount, uint StreamVertexCount, uint Stride,
    ulong VertexBufferOffset, ulong VertexBufferSize, string StreamSha256,
    string IndexSha256, string UnitSha256, IReadOnlyList<IasMorphComponent> Components,
    double[] BoundsMin, double[] BoundsMax, uint IndexStride,
    IReadOnlyList<IasMorphSection> Sections, IReadOnlyList<IasMorphDelta> Deltas);

public sealed record IasMorphDocument(
    string ProjectId, string ArchiveId, string SourceSha256,
    IReadOnlyList<IasMorphChannel> Channels, IReadOnlyList<IasMorphMesh> Meshes,
    JsonElement Directory);

public sealed record IasMorphSourceProgram(string Name, string Sha256, IasMorphDocument Document);

/// <summary>HD2Morph1 来源 sidecar 的严格只读解码；未知目录元数据原样保留。</summary>
public static class IasMorphFormat
{
    public const int HeaderSize = 72;
    public const int RecordSize = 28;
    public const int MaxBytes = 268_435_456;
    public const int MaxDirectoryBytes = 16_777_216;
    public const int MaxRecords = (MaxBytes - HeaderSize) / RecordSize;
    public const int MaxChannels = 4096;
    public const int MaxMeshes = 65_536;
    private const double Float32Max = 3.4028234663852886e38;
    private static readonly byte[] Magic = "HD2MOR1\0"u8.ToArray();

    public static IasMorphSourceProgram Read(string name, ReadOnlySpan<byte> raw)
    {
        if (raw.Length is < HeaderSize or > MaxBytes) Fail("file", "invalid file size");
        if (!raw[..8].SequenceEqual(Magic)) Fail("header", "unsupported magic");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]);
        var total = BinaryPrimitives.ReadUInt64LittleEndian(raw[16..]);
        var directorySize = BinaryPrimitives.ReadUInt64LittleEndian(raw[24..]);
        var count = BinaryPrimitives.ReadUInt64LittleEndian(raw[32..]);
        if (version != 1 || headerSize != HeaderSize) Fail("header", "unsupported version or header size");
        if (total != (ulong)raw.Length || directorySize > MaxDirectoryBytes || count > (ulong)MaxRecords)
            Fail("header", "invalid file, directory or record size");
        var payloadStartLong = HeaderSize + (long)directorySize;
        if (payloadStartLong + (long)count * RecordSize != raw.Length)
            Fail("header", "payload size does not match record count");
        if (!SHA256.HashData(raw[HeaderSize..]).AsSpan().SequenceEqual(raw[40..72]))
            Fail("file", "SHA-256 integrity check failed");

        var reader = new Utf8JsonReader(raw.Slice(HeaderSize, (int)directorySize), new JsonReaderOptions
        {
            MaxDepth = 32,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        var nodes = 0;
        var root = ParseValue(ref reader, 0, ref nodes);
        if (reader.Read() || root.ValueKind != JsonValueKind.Object) Fail("directory", "expected one JSON object");
        var canonical = CanonicalJson(root);
        if (!canonical.AsSpan().SequenceEqual(raw.Slice(HeaderSize, (int)directorySize)))
            Fail("directory", "JSON is not in canonical UTF-8 form");

        var channels = ReadChannels(root);
        var meshes = ReadMeshes(root, channels, raw, payloadStartLong, count);
        return new IasMorphSourceProgram(name, Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
            new IasMorphDocument(Hex(root, "project_id", 32), Hex(root, "archive_id", 16),
                Hex(root, "source_sha256", 64), channels, meshes, root.Clone()));
    }

    public static IReadOnlyList<IasMorphSourceProgram> ReadPrograms(IReadOnlyDictionary<string, byte[]> members)
        => members.Where(entry => entry.Key.EndsWith(".hd2morph", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => Read(entry.Key, entry.Value)).ToList();

    private static List<IasMorphChannel> ReadChannels(JsonElement root)
    {
        Text(root, "schema", "HD2Morph1");
        Text(root, "coordinate_space", "mesh_local_pre_skinning");
        Text(root, "normal_mode", "normalized_linear_delta");
        Hex(root, "project_id", 32);
        Hex(root, "archive_id", 16);
        Hex(root, "source_sha256", 64);
        var result = new List<IasMorphChannel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (channel, where) in Items(Property(root, "channels"), "channels", MaxChannels))
        {
            Object(channel, where);
            var id = Hex(channel, "id", 32, where + ".id");
            if (!ids.Add(id)) Fail(where, "duplicate channel id");
            var min = Number(channel, "min", where + ".min");
            var max = Number(channel, "max", where + ".max");
            var defaultValue = Number(channel, "default", where + ".default");
            var baseline = Number(channel, "baseline", where + ".baseline");
            var normalWeight = Number(channel, "normal_sample_weight", where + ".normal_sample_weight");
            var muted = Boolean(channel, "muted", where + ".muted");
            if (min > max) Fail(where, "minimum exceeds maximum");
            if ((min > defaultValue || defaultValue > max) && !(muted && defaultValue == 0)
                || (min > baseline || baseline > max) && !(muted && baseline == 0))
                Fail(where, "default/baseline is outside channel range");
            if (Math.Abs(normalWeight - baseline) <= 1e-12)
                Fail(where, "normal sample weight must differ from baseline");
            result.Add(new IasMorphChannel(id, Name(channel, "name", where + ".name"),
                Name(channel, "source_object", where + ".source_object"),
                Name(channel, "relative_key", where + ".relative_key"), min, max, defaultValue,
                baseline, normalWeight, Boolean(channel, "has_driver", where + ".has_driver"), muted));
        }
        return result;
    }

    private static List<IasMorphMesh> ReadMeshes(JsonElement root, List<IasMorphChannel> channels,
        ReadOnlySpan<byte> raw, long payloadStart, ulong totalRecords)
    {
        var channelIds = channels.Select(channel => channel.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<IasMorphMesh>();
        var meshKeys = new HashSet<(string, uint, int, uint)>();
        ulong nextRecord = 0;
        foreach (var (mesh, where) in Items(Property(root, "meshes"), "meshes", MaxMeshes))
        {
            Object(mesh, where);
            var unitId = Hex(mesh, "unit_id", 16, where + ".unit_id");
            var meshIndex = Integer(mesh, "mesh_index", where + ".mesh_index");
            var lodProperty = Property(mesh, "lod_index");
            long lodValue = 0;
            if (lodProperty.ValueKind != JsonValueKind.Number || !lodProperty.TryGetInt64(out lodValue) || lodValue < -1 || lodValue > uint.MaxValue)
                Fail(where + ".lod_index", "expected an integer in [-1, 4294967295]");
            var lod = checked((int)lodValue);
            var streamIndex = Integer(mesh, "stream_index", where + ".stream_index");
            var vertexOffset = Integer(mesh, "vertex_offset", where + ".vertex_offset");
            var unitVersion = Integer(mesh, "unit_version", where + ".unit_version");
            var vertexCount = Integer(mesh, "vertex_count", 1, uint.MaxValue, where + ".vertex_count");
            var streamVertexCount = Integer(mesh, "stream_vertex_count", 1, uint.MaxValue, where + ".stream_vertex_count");
            var stride = Integer(mesh, "stride", 1, 4096, where + ".stride");
            var vertexBufferOffset = Integer64(mesh, "vertex_buffer_offset", where + ".vertex_buffer_offset");
            var vertexBufferSize = Integer64(mesh, "vertex_buffer_size", where + ".vertex_buffer_size");
            if (!meshKeys.Add((unitId, meshIndex, lod, streamIndex))) Fail(where, "duplicate mesh key");
            if (vertexOffset + (ulong)vertexCount > streamVertexCount) Fail(where, "mesh vertex range exceeds stream");
            if ((ulong)streamVertexCount * stride > vertexBufferSize) Fail(where, "vertex stream exceeds vertex buffer size");
            if (vertexBufferOffset > ulong.MaxValue - vertexBufferSize) Fail(where, "vertex buffer offset overflows uint64");
            var streamSha = Hex(mesh, "stream_sha256", 64, where + ".stream_sha256");
            var indexSha = Hex(mesh, "index_sha256", 64, where + ".index_sha256");
            var unitSha = Hex(mesh, "unit_sha256", 64, where + ".unit_sha256");
            var components = ReadComponents(mesh, stride, where);
            var low = Vector(mesh, "bounds_min", where + ".bounds_min");
            var high = Vector(mesh, "bounds_max", where + ".bounds_max");
            for (var axis = 0; axis < 3; axis++) if (low[axis] > high[axis]) Fail(where, "bounds minimum exceeds maximum");
            var seenChannels = new HashSet<string>(StringComparer.Ordinal);
            var deltas = new List<IasMorphDelta>();
            foreach (var (delta, dwhere) in Items(Property(mesh, "deltas"), where + ".deltas", MaxChannels))
            {
                Object(delta, dwhere);
                if (delta.TryGetProperty("records", out _)) Fail(dwhere, "inline records are forbidden in the directory");
                var channelId = Hex(delta, "channel_id", 32, dwhere + ".channel_id");
                if (!channelIds.Contains(channelId) || !seenChannels.Add(channelId))
                    Fail(dwhere, "unknown or duplicate channel reference");
                var start = Integer(delta, "record_offset", 0, totalRecords, dwhere + ".record_offset");
                var length = Integer(delta, "record_count", 0, totalRecords, dwhere + ".record_count");
                if (start != nextRecord || start + length > totalRecords)
                    Fail(dwhere, "record ranges overlap, contain gaps or exceed payload");
                var records = new List<IasMorphRecord>((int)length);
                var previous = -1;
                for (var index = 0; index < (int)length; index++)
                {
                    var at = checked((int)(payloadStart + (long)(start + (ulong)index) * RecordSize));
                    var vertex = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(at, 4));
                    if (vertex < 0 || vertex >= vertexCount) Fail(dwhere, "record vertex exceeds mesh");
                    if (vertex <= previous) Fail(dwhere, "vertex indices must be unique and ascending");
                    previous = vertex;
                    var values = new float[6];
                    for (var component = 0; component < 6; component++)
                    {
                        values[component] = BinaryPrimitives.ReadSingleLittleEndian(raw.Slice(at + 4 + component * 4, 4));
                        if (!float.IsFinite(values[component]) || Math.Abs(values[component]) > Float32Max)
                            Fail(dwhere, "record contains a nonfinite or out-of-range number");
                    }
                    records.Add(new IasMorphRecord(vertex, values));
                }
                deltas.Add(new IasMorphDelta(channelId, records));
                nextRecord += length;
            }
            var sections = new List<IasMorphSection>();
            if (mesh.TryGetProperty("sections", out var sectionRows))
                foreach (var (section, swhere) in Items(sectionRows, where + ".sections", 1024))
                {
                    Object(section, swhere);
                    sections.Add(new IasMorphSection(Integer(section, "index_byte_offset", swhere + ".index_byte_offset"),
                        Integer(section, "index_count", swhere + ".index_count")));
                }
            var indexStride = mesh.TryGetProperty("index_stride", out var indexStrideValue)
                ? checked((uint)Integer64(indexStrideValue, "index_stride", where + ".index_stride")) : 0;
            result.Add(new IasMorphMesh(unitId, meshIndex, lod, streamIndex, vertexOffset, unitVersion,
                vertexCount, streamVertexCount, stride, vertexBufferOffset, vertexBufferSize, streamSha,
                indexSha, unitSha, components, low, high, indexStride, sections, deltas));
        }
        if (nextRecord != totalRecords) Fail("directory", "unreferenced payload records");
        return result;
    }

    private static List<IasMorphComponent> ReadComponents(JsonElement mesh, uint stride, string where)
    {
        var result = new List<IasMorphComponent>();
        var ids = new HashSet<(uint, uint)>();
        var occupied = new List<(uint Start, ulong End)>();
        foreach (var (component, cwhere) in Items(Property(mesh, "components"), where + ".components", 256))
        {
            Object(component, cwhere);
            var type = Integer(component, "type", cwhere + ".type");
            var format = Integer(component, "format", cwhere + ".format");
            var index = Integer(component, "index", cwhere + ".index");
            var offset = Integer(component, "offset", cwhere + ".offset");
            var size = Integer(component, "size", 1, uint.MaxValue, cwhere + ".size");
            if (!ids.Add((type, index))) Fail(cwhere, "duplicate vertex component");
            var end = offset + (ulong)size;
            if (end > stride || occupied.Any(range => offset < range.End && end > range.Start))
                Fail(cwhere, "vertex component overlaps or exceeds stride");
            occupied.Add((offset, end));
            result.Add(new IasMorphComponent(type, format, index, offset, size));
        }
        if (result.Count == 0) Fail(where, "vertex components cannot be empty");
        return result;
    }

    private static JsonElement ParseValue(ref Utf8JsonReader reader, int depth, ref int nodes, bool advance = true)
    {
        if (depth > 32 || ++nodes > 1_000_000) Fail("directory", "JSON complexity limit exceeded");
        if (advance && !reader.Read()) Fail("directory", "truncated JSON");
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var names = new HashSet<string>(StringComparer.Ordinal);
                var entries = new List<KeyValuePair<string, JsonElement>>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;
                    if (reader.TokenType != JsonTokenType.PropertyName) Fail("directory", "expected object property");
                    var name = reader.GetString()!;
                    ValidateName(name, "directory key", true);
                    if (!names.Add(name)) Fail("directory", "duplicate JSON key " + name);
                    entries.Add(new(name, ParseValue(ref reader, depth + 1, ref nodes)));
                }
                return JsonElementFromObject(entries);
            case JsonTokenType.StartArray:
                var items = new List<JsonElement>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) break;
                    items.Add(ParseValue(ref reader, depth + 1, ref nodes, advance: false));
                }
                return JsonElementFromArray(items);
            case JsonTokenType.String:
                return JsonElement.Parse(JsonSerializer.Serialize(reader.GetString()), new JsonDocumentOptions());
            case JsonTokenType.Number:
                return JsonElement.Parse(reader.ValueSpan, new JsonDocumentOptions());
            case JsonTokenType.True:
            case JsonTokenType.False:
            case JsonTokenType.Null:
                return JsonElement.Parse(reader.TokenType switch
                {
                    JsonTokenType.True => "true"u8,
                    JsonTokenType.False => "false"u8,
                    _ => "null"u8
                }, new JsonDocumentOptions());
            default:
                Fail("directory", "unsupported JSON token");
                return default;
        }
    }

    private static JsonElement JsonElementFromObject(IReadOnlyList<KeyValuePair<string, JsonElement>> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var entry in entries)
            {
                writer.WritePropertyName(entry.Key);
                entry.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement JsonElementFromArray(IReadOnlyList<JsonElement> items)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in items) item.WriteTo(writer);
            writer.WriteEndArray();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static byte[] CanonicalJson(JsonElement value)
    {
        var builder = new StringBuilder();
        WriteCanonical(value, builder);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void WriteCanonical(JsonElement value, StringBuilder builder)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var entries = value.EnumerateObject().OrderBy(entry => entry.Name, IasCodePointComparer.Instance).ToList();
                for (var index = 0; index < entries.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    builder.Append(IasLua.QuoteJson(entries[index].Name, ensureAscii: false)).Append(':');
                    WriteCanonical(entries[index].Value, builder);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var arrayIndex = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (arrayIndex++ > 0) builder.Append(',');
                    WriteCanonical(item, builder);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(IasLua.QuoteJson(value.GetString()!, ensureAscii: false));
                break;
            case JsonValueKind.Number:
                var raw = value.GetRawText();
                if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
                {
                    var integer = BigInteger.Parse(raw, CultureInfo.InvariantCulture);
                    if (integer < long.MinValue || integer > ulong.MaxValue) Fail("directory", "JSON integer exceeds 64-bit range");
                    builder.Append(integer.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    var number = value.GetDouble();
                    if (!double.IsFinite(number)) Fail("directory", "JSON contains a nonfinite number");
                    builder.Append(PythonFloat.Repr(number));
                }
                break;
            case JsonValueKind.True: builder.Append("true"); break;
            case JsonValueKind.False: builder.Append("false"); break;
            case JsonValueKind.Null: builder.Append("null"); break;
            default: Fail("directory", "unsupported JSON value"); break;
        }
    }

    private static IEnumerable<(JsonElement Value, string Where)> Items(JsonElement array, string where, int maximum)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum)
            Fail(where, "expected an array with at most " + maximum + " items");
        var index = 0;
        foreach (var value in array.EnumerateArray()) yield return (value, $"{where}[{index++}]");
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out var property)) return property;
        Fail("directory", "missing " + name);
        return default;
    }

    private static void Object(JsonElement value, string where)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail(where, "expected an object");
    }

    private static string Text(JsonElement value, string name, string? expected = null, string? where = null)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.String || expected is not null && property.GetString() != expected)
            Fail(where ?? name, expected is null ? "expected a string" : "unexpected value");
        return property.GetString()!;
    }

    private static string Name(JsonElement value, string name, string where, bool allowEmpty = false)
    {
        var text = Text(value, name, where: where);
        ValidateName(text, where, allowEmpty);
        return text;
    }

    private static void ValidateName(string text, string where, bool allowEmpty)
    {
        if ((!allowEmpty && text.Length == 0) || text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > 1024)
            Fail(where, "invalid UTF-8 name");
    }

    private static string Hex(JsonElement value, string name, int width, string? where = null)
    {
        var text = Text(value, name, where: where);
        if (text.Length != width || !text.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
            Fail(where ?? name, $"expected {width} lowercase hexadecimal digits");
        return text;
    }

    private static uint Integer(JsonElement value, string name, string where, uint maximum = uint.MaxValue)
        => Integer(value, name, 0, maximum, where);

    private static uint Integer(JsonElement value, string name, long minimum, ulong maximum, string where)
    {
        var property = Property(value, name);
        ulong number = 0;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetUInt64(out number) || number < (ulong)minimum || number > maximum)
            Fail(where, "expected an integer in range");
        return checked((uint)number);
    }

    private static ulong Integer64(JsonElement value, string name, string where)
    {
        var property = Property(value, name);
        ulong number = 0;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetUInt64(out number))
            Fail(where, "expected a uint64 integer");
        return number;
    }

    private static double Number(JsonElement value, string name, string where)
    {
        var property = Property(value, name);
        double number = 0;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out number)
            || !double.IsFinite(number) || Math.Abs(number) > Float32Max)
            Fail(where, "expected a finite float32 number");
        return number;
    }

    private static bool Boolean(JsonElement value, string name, string where)
    {
        var property = Property(value, name);
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail(where, "expected a boolean");
        return property.GetBoolean();
    }

    private static double[] Vector(JsonElement value, string name, string where)
    {
        var array = Property(value, name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 3) Fail(where, "expected three coordinates");
        return array.EnumerateArray().Select((item, index) =>
        {
            double number = 0;
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out number)
                || !double.IsFinite(number) || Math.Abs(number) > Float32Max)
                Fail($"{where}[{index}]", "expected a finite float32 number");
            return number;
        }).ToArray();
    }

    [DoesNotReturn]
    private static void Fail(string where, string message) => throw new InvalidDataException($"形态来源无效：{where}: {message}");
}
