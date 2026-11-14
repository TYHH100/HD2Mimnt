using System.Text;
using System.Text.RegularExpressions;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasRouteOption(string Id, string Name, int Order, IReadOnlyDictionary<string, string> Routes, IReadOnlyList<string> Units);

public sealed record IasRoutePart(string Id, string Name, string Slot, string Default, int Selected, IReadOnlyList<IasRouteOption> Options);

public sealed record IasRouteBodyOwner(string Archive, string Equipment, IReadOnlyList<string> Sources);

public sealed record IasRouting(
    IReadOnlyDictionary<string, string> Initial,
    IReadOnlyDictionary<string, string> Domains,
    IReadOnlyList<IasRoutePart> Parts,
    IReadOnlyList<IasRouteBodyOwner> BodyOwners,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Published,
    IReadOnlyDictionary<string, string> PublishedDomains);

/// <summary>原模组 isolation/difference 描述：默认槽位、部件开关与最终发布 Unit。</summary>
public static class IasRoutes
{
    private static readonly Regex HexIdPattern = new("\\A0x[0-9a-fA-F]{1,16}\\z", RegexOptions.Compiled);

    public static IasRouting Read(IReadOnlyDictionary<string, byte[]> members)
    {
        var initial = new Dictionary<string, string>(StringComparer.Ordinal);
        var domains = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = new List<IasRoutePart>();
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        var slotOptions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var bodyOwners = new List<IasRouteBodyOwner>();
        foreach (var (name, raw) in members.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!name.EndsWith(".isolation.ini", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".difference.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var entries = Records(Encoding.UTF8.GetString(raw).TrimStart('\uFEFF')).ToList();
            var header = entries.Where(entry => entry.Key is "manifest_version" or "difference_logic" or "domain" or "archive" or "equipment")
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            IasBinary.Require(header.GetValueOrDefault("domain") is "body" or "helmet", "unsupported original isolation domain");
            if (name.EndsWith(".isolation.ini", StringComparison.OrdinalIgnoreCase))
            {
                var sources = new List<string>();
                foreach (var (key, value) in entries)
                {
                    if (key != "unit") continue;
                    var fields = value.Split(',');
                    IasBinary.Require(fields.Length == 2, "isolation unit fields");
                    var native = HexId(fields[0]);
                    var published = HexId(fields[1]);
                    IasBinary.Require(!initial.TryGetValue(native, out var existing) || existing == published, "conflicting default slot");
                    initial[native] = published;
                    domains[native] = header["domain"];
                    sources.Add(native);
                }
                if (header["domain"] == "body")
                {
                    var equipment = header.GetValueOrDefault("equipment", "").ToLowerInvariant();
                    if (equipment.StartsWith("0x", StringComparison.Ordinal)) equipment = equipment[2..];
                    bodyOwners.Add(new IasRouteBodyOwner(HexId(header["archive"]), equipment, sources));
                }
            }
            else
            {
                IasBinary.Require(header.GetValueOrDefault("manifest_version") == "6"
                                  && header.GetValueOrDefault("difference_logic") == "free_parts", "unsupported difference version");
                var pending = ParseParts(entries, replaced, slotOptions);
                parts.AddRange(pending);
            }
        }
        IasBinary.Require(initial.Count > 0 && slotOptions.Values.All(set => set.Count == 1), "cross-part original-slot conflict");
        var publishedDomains = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Own(string native, string published)
        {
            IasBinary.Require(domains.ContainsKey(native), "part route has no original isolation owner");
            if (!publishedDomains.TryGetValue(published, out var owners)) publishedDomains[published] = owners = new HashSet<string>(StringComparer.Ordinal);
            owners.Add(domains[native]);
        }
        foreach (var (native, published) in initial) Own(native, published);
        foreach (var part in parts)
        foreach (var option in part.Options)
        foreach (var (native, published) in option.Routes)
            Own(native, published);
        IasBinary.Require(publishedDomains.Values.All(owners => owners.Count == 1), "Unit is routed to conflicting body/head domains");
        var publishedSet = initial.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var part in parts)
        foreach (var option in part.Options)
        foreach (var unit in option.Units)
            publishedSet.Add(unit);
        return new IasRouting(initial, domains, parts, bodyOwners,
            replaced.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            publishedSet.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            publishedDomains.ToDictionary(pair => pair.Key, pair => pair.Value.First(), StringComparer.Ordinal));
    }

    private static List<IasRoutePart> ParseParts(List<(string Key, string Value)> entries, HashSet<string> replaced,
        Dictionary<string, HashSet<string>> slotOptions)
    {
        var pending = new List<PendingPart>();
        var indexById = new Dictionary<string, PendingPart>(StringComparer.Ordinal);
        PendingPart Get(string id) => indexById.TryGetValue(id, out var part)
            ? part
            : throw new InvalidDataException("模型数据检查：" + "missing free part " + id);
        foreach (var (key, value) in entries)
        {
            var fields = value.Split(',');
            switch (key)
            {
                case "free_part":
                {
                    IasBinary.Require(fields.Length == 3, "free-part field count");
                    var part = new PendingPart(fields[0], fields[1], fields[1], fields[2]);
                    IasBinary.Require(indexById.TryAdd(part.Id, part), "duplicate part");
                    pending.Add(part);
                    break;
                }
                case "free_part_display_name":
                {
                    IasBinary.Require(fields.Length >= 2, "free-part display field count");
                    Get(fields[0]).Name = string.Join(',', fields.Skip(1));
                    break;
                }
                case "free_option":
                {
                    IasBinary.Require(fields.Length == 3, "free-option field count");
                    var part = Get(fields[0]);
                    var order = int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture);
                    IasBinary.Require(order == part.Options.Count, "part option order");
                    part.Options.Add(new PendingOption(fields[1], fields[1]));
                    break;
                }
                case "free_option_display_name":
                {
                    IasBinary.Require(fields.Length >= 3, "free-option display field count");
                    var option = Get(fields[0]).Options.First(item => item.Id == fields[1]);
                    option.Name = string.Join(',', fields.Skip(2));
                    break;
                }
                case "free_route":
                {
                    IasBinary.Require(fields.Length == 5, "free-route field count");
                    var option = Get(fields[0]).Options.First(item => item.Id == fields[1]);
                    var native = HexId(fields[2]);
                    _ = HexId(fields[3]);
                    var published = HexId(fields[4]);
                    replaced.Add(native);
                    IasBinary.Require(!option.Routes.TryGetValue(native, out var existing) || existing == published, "duplicate option slot");
                    option.Routes[native] = published;
                    option.Units.Add(published);
                    if (!slotOptions.TryGetValue(native, out var owners)) slotOptions[native] = owners = new HashSet<string>(StringComparer.Ordinal);
                    owners.Add(fields[0]);
                    break;
                }
            }
        }
        var result = new List<IasRoutePart>();
        foreach (var part in pending)
        {
            IasBinary.Require(part.Options.Count > 0 && part.Options.Any(option => option.Id == part.Default), "invalid part default");
            var selected = part.Options.FindIndex(option => option.Id == part.Default) + 1;
            result.Add(new IasRoutePart(part.Id, part.Name, part.Slot, part.Default, selected,
                part.Options.Select((option, order) => new IasRouteOption(option.Id, option.Name, order,
                    option.Routes, option.Units)).ToList()));
        }
        return result;
    }

    public static IReadOnlyList<(string Key, string Value)> Records(string text)
    {
        var result = new List<(string, string)>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var separator = trimmed.IndexOf('=');
            IasBinary.Require(separator > 0, "manifest expected key=value");
            result.Add((trimmed[..separator], trimmed[(separator + 1)..]));
        }
        return result;
    }

    public static string HexId(string value)
    {
        IasBinary.Require(HexIdPattern.IsMatch(value), "manifest resource ID");
        return ulong.Parse(value[2..], System.Globalization.NumberStyles.HexNumber).ToString("x16");
    }

    /// <summary>上游 <c>companion_sets.explicit_descriptor</c>：从原包 isolation/difference 说明生成显式伴随描述。</summary>
    public static IasCompanionDescriptor ExplicitDescriptor(IReadOnlyDictionary<string, byte[]> members, string seed)
    {
        var archives = new List<string>();
        foreach (var (name, raw) in members)
        {
            if (!name.EndsWith(".isolation.ini", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".difference.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var entries = Records(Encoding.UTF8.GetString(raw).TrimStart('\uFEFF'));
            var archiveValue = entries.Where(entry => entry.Key == "archive").Select(entry => entry.Value).FirstOrDefault();
            if (archiveValue is null) continue;
            var archive = HexId(archiveValue);
            if (!archives.Contains(archive)) archives.Add(archive);
        }
        IasBinary.Require(archives.Count > 0, "独立物理包缺少原始 isolation / difference 部署说明，无法确认模型对应关系。");
        var id = IasHash.Sha256Hex("HD2ManualOriginal/" + seed)[..32];
        var result = new List<IasCompanionMember>();
        for (var index = 0; index < archives.Count; index++)
        {
            var files = index == 0
                ? members.Select(entry => new IasCompanionFile(entry.Key, IasHash.Sha256Hex(entry.Value))).ToList()
                : [];
            result.Add(new IasCompanionMember(archives[index], files));
        }
        return new IasCompanionDescriptor("HD2CLExplicitOriginalSelection1", true, id, result);
    }

    private sealed class PendingPart(string id, string name, string slot, string defaultValue)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public string Slot { get; } = slot;
        public string Default { get; } = defaultValue;
        public List<PendingOption> Options { get; } = [];
    }

    private sealed class PendingOption(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public Dictionary<string, string> Routes { get; } = new(StringComparer.Ordinal);
        public List<string> Units { get; } = [];
    }
}
