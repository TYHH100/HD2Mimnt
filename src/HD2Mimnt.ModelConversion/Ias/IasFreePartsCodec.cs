using System.Globalization;
using System.Text;

namespace HD2Mimnt.ModelConversion.Ias;

public static class IasFreePartsCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaximumBytes = 4 * 1024 * 1024;

    public static byte[] Write(IasFreePartsDocument document, IasArchiveBinding binding, CancellationToken cancellationToken = default)
    {
        Validate(document, binding, cancellationToken);
        var text = new StringBuilder();
        text.Append("manifest_version=6\nmanifest_id=").Append(document.ManifestId)
            .Append("\ndifference_logic=free_parts\ndomain=").Append(DomainName(document.Domain))
            .Append("\narchive=").Append(Hex(document.ArchiveId)).Append('\n');
        foreach (var key in document.EquipmentKeys.Order())
            text.Append("equipment=0x").Append(key.ToString("X8", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var part in document.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            text.Append("\nfree_part=").Append(part.Id).Append(',').Append(part.Slot).Append(',').Append(part.Options[0].Id)
                .Append("\nfree_part_display_name=").Append(part.Id).Append(',').Append(part.DisplayName).Append('\n');
            var defaults = part.Options[0].Routes.ToDictionary(route => route.NativeUnitId, route => route.PublishedUnitId);
            for (var index = 0; index < part.Options.Count; index++)
            {
                var option = part.Options[index];
                text.Append("free_option=").Append(part.Id).Append(',').Append(option.Id).Append(',')
                    .Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n')
                    .Append("free_option_display_name=").Append(part.Id).Append(',').Append(option.Id).Append(',').Append(option.DisplayName).Append('\n');
                foreach (var route in option.Routes.OrderBy(route => route.NativeUnitId))
                {
                    text.Append("free_route=").Append(part.Id).Append(',').Append(option.Id).Append(',')
                        .Append(Hex(route.NativeUnitId)).Append(',').Append(Hex(defaults[route.NativeUnitId])).Append(',')
                        .Append(Hex(route.PublishedUnitId)).Append('\n');
                    if (route.HasRigProfile)
                        text.Append("free_rig_profile=").Append(part.Id).Append(',').Append(option.Id).Append(',').Append(Hex(route.PublishedUnitId)).Append('\n');
                }
            }
        }
        var bytes = Utf8.GetBytes(text.ToString());
        if (bytes.Length > MaximumBytes) throw new IasFormatException("IAS difference manifest exceeds 4 MiB");
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    public static IasFreePartsDocument Read(ReadOnlySpan<byte> bytes, IasArchiveBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > MaximumBytes) throw new IasFormatException("IAS difference manifest exceeds 4 MiB");
        string text;
        try { text = Utf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new IasFormatException("IAS manifest is not UTF-8"); }
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var equipment = new List<uint>();
        var parts = new List<PartBuilder>();
        PartBuilder? currentPart = null;
        OptionBuilder? currentOption = null;
        foreach (var raw in text.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith(';')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) throw new IasFormatException("Invalid IAS manifest line");
            var key = line[..separator];
            var value = line[(separator + 1)..];
            switch (key)
            {
                case "manifest_version": case "manifest_id": case "difference_logic": case "domain": case "archive":
                    if (currentPart is not null || !headers.TryAdd(key, value)) throw new IasFormatException("Duplicate or misplaced IAS header");
                    break;
                case "equipment":
                    if (currentPart is not null) throw new IasFormatException("Misplaced equipment row");
                    var equipmentId = ParseHex(value, 8);
                    equipment.Add((uint)equipmentId);
                    break;
                case "free_part":
                {
                    var fields = Fields(value, 3);
                    if (!Enum.TryParse<IasPartSlot>(fields[1], out var slot) || !Enum.IsDefined(slot) || slot.ToString() != fields[1])
                        throw new IasFormatException("Unknown IAS part slot");
                    currentPart = new PartBuilder(fields[0], slot, fields[2]);
                    currentOption = null;
                    parts.Add(currentPart);
                    break;
                }
                case "free_part_display_name":
                {
                    var fields = Fields(value, 2);
                    RequirePart(fields[0]);
                    if (currentPart!.DisplayName is not null) throw new IasFormatException("Duplicate part display name");
                    currentPart.DisplayName = fields[1];
                    break;
                }
                case "free_option":
                {
                    var fields = Fields(value, 3);
                    RequirePart(fields[0]);
                    if (!int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index != currentPart!.Options.Count)
                        throw new IasFormatException("IAS option order is not contiguous");
                    currentOption = new OptionBuilder(fields[1]);
                    currentPart.Options.Add(currentOption);
                    break;
                }
                case "free_option_display_name":
                {
                    var fields = Fields(value, 3);
                    RequireOption(fields[0], fields[1]);
                    if (currentOption!.DisplayName is not null) throw new IasFormatException("Duplicate option display name");
                    currentOption.DisplayName = fields[2];
                    break;
                }
                case "free_route":
                {
                    var fields = Fields(value, 5);
                    RequireOption(fields[0], fields[1]);
                    var route = new IasUnitRoute(ParseHex(fields[2], 16), ParseHex(fields[4], 16));
                    currentOption!.Routes.Add(route);
                    currentOption.Defaults.Add((route.NativeUnitId, ParseHex(fields[3], 16)));
                    break;
                }
                case "free_rig_profile":
                {
                    var fields = Fields(value, 3);
                    RequireOption(fields[0], fields[1]);
                    if (!currentOption!.Profiles.Add(ParseHex(fields[2], 16))) throw new IasFormatException("Duplicate rig profile row");
                    break;
                }
                default: throw new IasFormatException($"Unsupported IAS manifest field: {key}");
            }
        }
        if (headers.GetValueOrDefault("manifest_version") != "6" || headers.GetValueOrDefault("difference_logic") != "free_parts")
            throw new IasFormatException("Expected IAS free-parts v6 manifest");
        var domain = headers.GetValueOrDefault("domain") switch
        {
            "body" => IasContentDomain.Body, "helmet" => IasContentDomain.Helmet,
            _ => throw new IasFormatException("Unknown IAS domain")
        };
        var rows = new List<IasFreePart>();
        foreach (var part in parts)
        {
            if (part.Options.Count == 0 || part.DefaultId != part.Options[0].Id)
                throw new IasFormatException("IAS default must name the first option");
            var defaults = new Dictionary<ulong, ulong>();
            foreach (var route in part.Options[0].Routes)
                if (!defaults.TryAdd(route.NativeUnitId, route.PublishedUnitId)) throw new IasFormatException("Duplicate native route");
            var options = new List<IasFreeOption>();
            foreach (var option in part.Options)
            {
                if (option.Defaults.Any(row => !defaults.TryGetValue(row.Native, out var expected) || row.Alias != expected))
                    throw new IasFormatException("IAS route default alias differs from first option");
                if (!option.Profiles.IsSubsetOf(option.Routes.Select(route => route.PublishedUnitId)))
                    throw new IasFormatException("Rig row references an unknown option Unit");
                options.Add(new IasFreeOption(option.Id, option.DisplayName ?? throw new IasFormatException("Missing option display name"),
                    option.Routes.Select(route => route with { HasRigProfile = option.Profiles.Contains(route.PublishedUnitId) }).ToArray()));
            }
            rows.Add(new IasFreePart(part.Id, part.Slot, part.DisplayName ?? throw new IasFormatException("Missing part display name"), options));
        }
        var document = new IasFreePartsDocument(headers.GetValueOrDefault("manifest_id") ?? "", domain,
            ParseHex(headers.GetValueOrDefault("archive") ?? "", 16), equipment, rows);
        Validate(document, binding, cancellationToken);
        return document;

        void RequirePart(string id)
        {
            if (currentPart is null || currentPart.Id != id) throw new IasFormatException("IAS row references the wrong part");
        }
        void RequireOption(string part, string option)
        {
            RequirePart(part);
            if (currentOption is null || currentOption.Id != option) throw new IasFormatException("IAS row references the wrong option");
        }
    }

    internal static void Validate(IasFreePartsDocument document, IasArchiveBinding binding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateId(document.ManifestId);
        if (!Enum.IsDefined(document.Domain) || document.ArchiveId == 0 || document.ArchiveId != binding.ArchiveId || document.Domain != binding.Domain)
            throw new IasFormatException("IAS archive/domain does not match the supplied equipment binding");
        if (document.EquipmentKeys.Count == 0 || document.EquipmentKeys.Any(key => key == 0) || document.EquipmentKeys.Distinct().Count() != document.EquipmentKeys.Count ||
            binding.Equipment.Select(row => row.EquipmentKey).Distinct().Count() != binding.Equipment.Count ||
            !document.EquipmentKeys.ToHashSet().SetEquals(binding.Equipment.Select(row => row.EquipmentKey)))
            throw new IasFormatException("IAS equipment keys are missing, duplicate or differ from the binding");
        if (binding.Equipment.Any(row => row.NativeUnitIds.Count == 0 || row.NativeUnitIds.Contains(0UL) || row.NativeUnitIds.Distinct().Count() != row.NativeUnitIds.Count))
            throw new IasFormatException("Invalid equipment Unit ownership");
        if (document.Parts.Count == 0 || document.Parts.Count > 8) throw new IasFormatException("Invalid IAS part count");
        var occupied = new HashSet<ulong>();
        var published = new HashSet<ulong>();
        var partIds = new HashSet<string>(StringComparer.Ordinal);
        var slots = new HashSet<IasPartSlot>();
        var nativeResources = binding.Equipment.SelectMany(row => row.NativeUnitIds).ToHashSet();
        foreach (var part in document.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateId(part.Id); ValidateDisplayName(part.DisplayName);
            if (!partIds.Add(part.Id) || !slots.Add(part.Slot) || !Enum.IsDefined(part.Slot) ||
                (part.Slot == IasPartSlot.Head) != (document.Domain == IasContentDomain.Helmet))
                throw new IasFormatException("Duplicate or mismatched IAS part/domain");
            if (part.Options.Count == 0) throw new IasFormatException("IAS part has no options");
            var defaults = part.Options[0].Routes.Select(route => route.NativeUnitId).ToHashSet();
            if (defaults.Count == 0 || defaults.Any(id => !occupied.Add(id))) throw new IasFormatException("IAS parts overlap native Unit routes");
            foreach (var equipment in binding.Equipment)
                if (!defaults.IsSubsetOf(equipment.NativeUnitIds)) throw new IasFormatException("IAS route exceeds equipment ownership");
            var optionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in part.Options)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateId(option.Id); ValidateDisplayName(option.DisplayName);
                if (!optionIds.Add(option.Id) || option.Routes.Count != defaults.Count ||
                    !defaults.SetEquals(option.Routes.Select(route => route.NativeUnitId)))
                    throw new IasFormatException("IAS option ID or body-type route set is inconsistent");
                foreach (var route in option.Routes)
                {
                    if (route.PublishedUnitId == 0 || nativeResources.Contains(route.PublishedUnitId) || !published.Add(route.PublishedUnitId))
                        throw new IasFormatException("IAS published Unit IDs collide with original resources or other options");
                }
            }
        }
        if (occupied.Count > 32) throw new IasFormatException("IAS free-parts routes exceed the 32-slot runtime limit");
    }

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 63 || id.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')) || id is "." or "..")
            throw new IasFormatException("IAS identifiers must contain 1–63 safe ASCII characters");
    }

    private static void ValidateDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim() != name || name.Any(ch => char.IsControl(ch) || ch is ',' or '#' or ';'))
            throw new IasFormatException("IAS display name contains delimiters or control characters");
        try
        {
            if (Utf8.GetByteCount(name) >= 128) throw new IasFormatException("IAS display name exceeds 127 UTF-8 bytes");
        }
        catch (EncoderFallbackException) { throw new IasFormatException("IAS display name contains invalid Unicode"); }
    }

    private static string[] Fields(string value, int count)
    {
        var fields = value.Split(',');
        if (fields.Length != count) throw new IasFormatException("Incorrect number of IAS row fields");
        return fields;
    }

    private static ulong ParseHex(string value, int digits)
    {
        if (value.Length != digits + 2 || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number) || number == 0)
            throw new IasFormatException("Invalid IAS hexadecimal resource identifier");
        return number;
    }

    internal static string Hex(ulong value) => "0x" + value.ToString("X16", CultureInfo.InvariantCulture);
    internal static string DomainName(IasContentDomain domain) => domain == IasContentDomain.Body ? "body" : "helmet";

    private sealed class PartBuilder(string id, IasPartSlot slot, string defaultId)
    {
        public string Id { get; } = id;
        public IasPartSlot Slot { get; } = slot;
        public string DefaultId { get; } = defaultId;
        public string? DisplayName { get; set; }
        public List<OptionBuilder> Options { get; } = [];
    }
    private sealed class OptionBuilder(string id)
    {
        public string Id { get; } = id;
        public string? DisplayName { get; set; }
        public List<IasUnitRoute> Routes { get; } = [];
        public List<(ulong Native, ulong Alias)> Defaults { get; } = [];
        public HashSet<ulong> Profiles { get; } = [];
    }
}
