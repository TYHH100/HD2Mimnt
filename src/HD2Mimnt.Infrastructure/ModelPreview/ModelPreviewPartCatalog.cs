using System.Globalization;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.IO;
using HD2Mimnt.Core.ModelPreview;
using HD2Mimnt.Infrastructure.ModelConversion;

namespace HD2Mimnt.Infrastructure.ModelPreview;

internal static class ModelPreviewPartCatalog
{
    internal sealed record Result(IReadOnlyList<ModelPreviewPartGroup> Parts, IReadOnlySet<ulong> CatalogUnitIds, bool IsCatalog);

    private const string ClMarker = "-- HD2CL_MODEL_V1 ";
    private const int MaxCatalogBytes = 8 * 1024 * 1024;
    private const int MaxSidecarBytes = 1024 * 1024;

    public static async Task<Result> ReadAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<PatchTocInspectionItem> entries,
        CancellationToken cancellationToken)
    {
        var cl = await ReadClPartsAsync(modDirectory, entries, cancellationToken);
        if (cl.IsCatalog) return cl;
        var iasParts = ReadIasParts(modDirectory, entries, cancellationToken);
        return new Result(iasParts, new HashSet<ulong>(), false);
    }

    private static async Task<Result> ReadClPartsAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<PatchTocInspectionItem> entries,
        CancellationToken cancellationToken)
    {
        var effectiveLua = entries.Where(entry => entry.TypeId == IasResourceTypes.Lua)
            .GroupBy(entry => entry.FileId)
            .Select(group => group.OrderBy(entry => entry.PatchOrder).Last())
            .OrderBy(entry => entry.PatchOrder)
            .ToArray();
        var groups = new List<ModelPreviewPartGroup>();
        var catalogUnitIds = new HashSet<ulong>();
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var found = false;
        foreach (var entry in effectiveLua)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.MainSize == 0 || entry.MainSize > MaxCatalogBytes) continue;
            try
            {
                var raw = await ReadResourceAsync(modDirectory, entry, cancellationToken);
                var code = Encoding.UTF8.GetString(raw).TrimStart('\uFEFF');
                if (!Hd2ClCatalogParser.TryParse(code, out var parsed, out var units)) continue;
                found = true;
                catalogUnitIds.UnionWith(units);
                foreach (var group in parsed)
                {
                    if (groupIds.Add(group.Id)) groups.Add(group);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceWarning("HD2CL preview catalog {0:x16}: {1}", entry.FileId, error.Message);
            }
        }
        return new Result(groups, catalogUnitIds, found);
    }

    private static IReadOnlyList<ModelPreviewPartGroup> ReadIasParts(
        DirectoryInfo modDirectory,
        IReadOnlyList<PatchTocInspectionItem> entries,
        CancellationToken cancellationToken)
    {
        var selectedUnits = entries.Where(entry => entry.TypeId == IasResourceTypes.Unit)
            .Select(entry => entry.FileId)
            .ToHashSet();
        if (selectedUnits.Count == 0) return [];

        var groups = new List<ModelPreviewPartGroup>();
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(modDirectory.FullName, "*.difference.ini", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0 || info.Length > MaxSidecarBytes) continue;
                var name = Path.GetFileName(path);
                var parts = IasRoutes.ReadParts(new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [name] = File.ReadAllBytes(path)
                });
                foreach (var part in parts)
                {
                    var options = part.Options.Select(option => new ModelPreviewPartOption
                    {
                        Id = option.Id,
                        Name = option.Name,
                        UnitIds = option.Units.Select(value => ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray()
                    }).ToList();
                    if (options.Count < 2 || !options.Any(option => option.UnitIds.Any(selectedUnits.Contains))) continue;
                    var id = "ias:" + part.Id;
                    if (!groupIds.Add(id)) continue;
                    groups.Add(new ModelPreviewPartGroup
                    {
                        Id = id,
                        Name = part.Name,
                        SelectedIndex = Math.Clamp(part.Selected - 1, 0, options.Count - 1),
                        Options = options
                    });
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceWarning("IAS preview sidecar {0}: {1}", path, error.Message);
            }
        }
        return groups;
    }

    private static async Task<byte[]> ReadResourceAsync(
        DirectoryInfo modDirectory,
        PatchTocInspectionItem entry,
        CancellationToken cancellationToken)
    {
        var path = PathGuard.ResolveInsideRoot(modDirectory.FullName, entry.PatchPath);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (entry.MainOffset > (ulong)stream.Length || entry.MainSize > (ulong)stream.Length - entry.MainOffset)
            throw new InvalidDataException("HD2CL catalog resource is outside the patch file.");
        var data = new byte[(int)entry.MainSize];
        stream.Seek((long)entry.MainOffset, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(data, cancellationToken);
        return data;
    }

    private static class Hd2ClCatalogParser
    {
        public static bool TryParse(string code, out IReadOnlyList<ModelPreviewPartGroup> groups, out IReadOnlySet<ulong> catalogUnitIds)
        {
            groups = [];
            catalogUnitIds = new HashSet<ulong>();
            var markerIndex = code.IndexOf(ClMarker, StringComparison.Ordinal);
            if (markerIndex < 0) return false;
            var returnIndex = code.IndexOf("\nreturn ", markerIndex + ClMarker.Length, StringComparison.Ordinal);
            if (returnIndex < 0) return false;
            try
            {
                var catalog = new Parser(code[(returnIndex + "\nreturn ".Length)..]).ParseDocument();
                (groups, catalogUnitIds) = ReadGroups(catalog);
                return true;
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or FormatException or OverflowException)
            {
                return false;
            }
        }

        private static (IReadOnlyList<ModelPreviewPartGroup> Groups, IReadOnlySet<ulong> CatalogUnitIds) ReadGroups(IasValue catalog)
        {
            var root = Object(catalog) ?? throw new InvalidDataException("HD2CL catalog root is not an object.");
            var models = Items(root, "models") ?? throw new InvalidDataException("HD2CL catalog has no models.");
            var groups = new List<ModelPreviewPartGroup>();
            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            var catalogUnitIds = new HashSet<ulong>();
            for (var modelIndex = 0; modelIndex < models.Count; modelIndex++)
            {
                var model = Object(models[modelIndex]) ?? throw new InvalidDataException("HD2CL model is not an object.");
                var modelId = Text(model, "id") ?? $"model-{modelIndex + 1}";
                var modelName = Text(model, "name") ?? modelId;
                var displayUnits = new Dictionary<ulong, ulong>();
                if (Object(Value(model, "display_units")) is { } mapping)
                {
                    foreach (var (source, display) in mapping.Entries)
                        if (TryParseUnitId(source, out var sourceId) && TryParseUnitId((display as IasValue.Text)?.Value ?? string.Empty, out var displayId))
                            displayUnits[sourceId] = displayId;
                }
                var alwaysVisible = UnitIds(Items(model, "base"), displayUnits).ToHashSet();
                catalogUnitIds.UnionWith(displayUnits.Values);
                catalogUnitIds.UnionWith(alwaysVisible);
                catalogUnitIds.UnionWith(UnitIds(Items(model, "head_units"), displayUnits));
                var parts = Items(model, "parts") ?? [];
                foreach (var partValue in parts)
                {
                    var part = Object(partValue) ?? throw new InvalidDataException("HD2CL part is not an object.");
                    var optionsValue = Items(part, "options") ?? [];
                    if (optionsValue.Count < 2) continue;
                    var options = new List<ModelPreviewPartOption>();
                    var invalidOption = false;
                    foreach (var optionValue in optionsValue)
                    {
                        var option = Object(optionValue) ?? throw new InvalidDataException("HD2CL part option is not an object.");
                        var rawUnits = Items(option, "units") ?? [];
                        var units = new List<ulong>();
                        foreach (var unitValue in rawUnits)
                        {
                            if (unitValue is not IasValue.Text text || !TryParseUnitId(text.Value, out var sourceId))
                            {
                                invalidOption = true;
                                break;
                            }
                            var unitId = displayUnits.TryGetValue(sourceId, out var displayId) ? displayId : sourceId;
                            catalogUnitIds.Add(unitId);
                            if (!alwaysVisible.Contains(unitId)) units.Add(unitId);
                        }
                        if (invalidOption) break;
                        options.Add(new ModelPreviewPartOption
                        {
                            Id = Text(option, "id") ?? $"option-{options.Count + 1}",
                            Name = Text(option, "name") ?? $"option-{options.Count + 1}",
                            UnitIds = units
                        });
                    }
                    if (invalidOption || options.All(option => option.UnitIds.Count == 0)) continue;
                    var partId = Text(part, "id") ?? $"part-{groups.Count + 1}";
                    var id = $"cl:{modelId}:{partId}";
                    if (!groupIds.Add(id)) continue;
                    var selected = Integer(part, "selected") ?? 1;
                    groups.Add(new ModelPreviewPartGroup
                    {
                        Id = id,
                        Name = models.Count > 1 ? $"{modelName} / {Text(part, "name") ?? partId}" : Text(part, "name") ?? partId,
                        SelectedIndex = (int)Math.Clamp(selected - 1, 0, options.Count - 1),
                        Options = options
                    });
                }
            }
            return (groups, catalogUnitIds);
        }

        private static IasValue.Object? Object(IasValue? value) => value as IasValue.Object;

        private static IReadOnlyList<IasValue>? Items(IasValue.Object value, string key)
        {
            var item = value.Entries.FirstOrDefault(entry => entry.Key == key).Value;
            return item switch
            {
                IasValue.Array array => array.Items,
                IasValue.Object { Entries.Count: 0 } => [],
                _ => null
            };
        }

        private static IasValue? Value(IasValue.Object value, string key) =>
            value.Entries.FirstOrDefault(entry => entry.Key == key).Value;

        private static string? Text(IasValue.Object value, string key) => (Value(value, key) as IasValue.Text)?.Value;

        private static long? Integer(IasValue.Object value, string key) => Value(value, key) is IasValue.Integer integer ? integer.Value : null;

        private static IEnumerable<ulong> UnitIds(IReadOnlyList<IasValue>? values, IReadOnlyDictionary<ulong, ulong>? displayUnits = null)
        {
            if (values is null) yield break;
            foreach (var value in values)
                if (value is IasValue.Text text && TryParseUnitId(text.Value, out var unitId))
                    yield return displayUnits is not null && displayUnits.TryGetValue(unitId, out var displayId) ? displayId : unitId;
        }

        private static bool TryParseUnitId(string value, out ulong unitId)
        {
            var text = value.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out unitId);
        }

        private sealed class Parser(string text)
        {
            private int _position;
            private int _depth;
            private int _nodes;
            private IReadOnlyList<IasValue>? _profiles;

            public IasValue ParseDocument()
            {
                SkipWhitespace();
                if (!StartsWith("(function()"))
                {
                    var value = ParseValue();
                    SkipWhitespace();
                    if (_position != text.Length) throw new InvalidDataException("Unexpected text after HD2CL catalog.");
                    return value;
                }

                Consume("(function()");
                Consume("local");
                Consume("_profiles");
                Consume("=");
                var profiles = ParseValue();
                if (profiles is not IasValue.Array array) throw new InvalidDataException("Invalid HD2CL profile table.");
                _profiles = array.Items;
                Consume(";");
                Consume("return");
                var result = ParseValue();
                SkipWhitespace();
                Consume("end");
                Consume(")");
                Consume("(");
                Consume(")");
                SkipWhitespace();
                if (_position != text.Length) throw new InvalidDataException("Unexpected text after HD2CL catalog function.");
                return result;
            }

            private IasValue ParseValue()
            {
                SkipWhitespace();
                if (++_nodes > 1_000_000 || ++_depth > 64) throw new InvalidDataException("HD2CL catalog is too complex.");
                try
                {
                    if (_position >= text.Length) throw new InvalidDataException("Unexpected end of HD2CL catalog.");
                    return text[_position] switch
                    {
                        '{' => ParseTable(),
                        '"' => IasValue.Of(ParseString()),
                        '-' or >= '0' and <= '9' => ParseNumber(),
                        _ => ParseKeyword()
                    };
                }
                finally
                {
                    _depth--;
                }
            }

            private IasValue ParseTable()
            {
                Consume("{");
                SkipWhitespace();
                if (StartsWith("}"))
                {
                    Consume("}");
                    return IasValue.Of(Array.Empty<IasValue>());
                }

                var entries = new List<KeyValuePair<string, IasValue>>();
                var items = new List<IasValue>();
                var isObject = StartsWith("[");
                while (true)
                {
                    SkipWhitespace();
                    if (isObject)
                    {
                        Consume("[");
                        var key = ParseString();
                        Consume("]");
                        Consume("=");
                        entries.Add(new KeyValuePair<string, IasValue>(key, ParseValue()));
                    }
                    else
                    {
                        items.Add(ParseValue());
                    }
                    SkipWhitespace();
                    if (StartsWith("}"))
                    {
                        Consume("}");
                        break;
                    }
                    if (!StartsWith(",") && !StartsWith(";")) throw new InvalidDataException("Invalid HD2CL table separator.");
                    Consume(text[_position].ToString());
                    SkipWhitespace();
                    if (StartsWith("}"))
                    {
                        Consume("}");
                        break;
                    }
                }
                return isObject ? new IasValue.Object(entries) : IasValue.Of(items);
            }

            private string ParseString()
            {
                if (!StartsWith("\"")) throw new InvalidDataException("Expected a quoted HD2CL string.");
                var start = _position++;
                var escaped = false;
                while (_position < text.Length)
                {
                    var ch = text[_position++];
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (ch != '"') continue;
                    using var document = JsonDocument.Parse(text.Substring(start, _position - start));
                    return document.RootElement.GetString() ?? throw new InvalidDataException("Invalid HD2CL string.");
                }
                throw new InvalidDataException("Unterminated HD2CL string.");
            }

            private IasValue ParseNumber()
            {
                var start = _position;
                while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] is '.' or '+' or '-'))
                    _position++;
                var token = text[start.._position];
                if (token.Contains('.') || token.Contains('e') || token.Contains('E'))
                    return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        ? IasValue.Of(number)
                        : throw new InvalidDataException("Invalid HD2CL number.");
                return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                    ? IasValue.Of(integer)
                    : throw new InvalidDataException("Invalid HD2CL integer.");
            }

            private IasValue ParseKeyword()
            {
                if (MatchWord("nil")) return IasValue.Nil.Instance;
                if (MatchWord("true")) return IasValue.Of(true);
                if (MatchWord("false")) return IasValue.Of(false);
                if (MatchWord("_profiles"))
                {
                    SkipWhitespace();
                    Consume("[");
                    var index = ParseNumber();
                    if (index is not IasValue.Integer integer || integer.Value < 1 || integer.Value > _profiles?.Count)
                        throw new InvalidDataException("Invalid HD2CL profile reference.");
                    Consume("]");
                    return _profiles![(int)integer.Value - 1];
                }
                throw new InvalidDataException("Unsupported HD2CL catalog value.");
            }

            private bool MatchWord(string word)
            {
                SkipWhitespace();
                if (!StartsWith(word)) return false;
                var end = _position + word.Length;
                if (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')) return false;
                _position = end;
                return true;
            }

            private void SkipWhitespace()
            {
                while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
            }

            private bool StartsWith(string value) => text.AsSpan(_position).StartsWith(value, StringComparison.Ordinal);

            private void Consume(string value)
            {
                SkipWhitespace();
                if (!StartsWith(value)) throw new InvalidDataException($"Expected '{value}' in HD2CL catalog.");
                _position += value.Length;
            }
        }
    }
}
