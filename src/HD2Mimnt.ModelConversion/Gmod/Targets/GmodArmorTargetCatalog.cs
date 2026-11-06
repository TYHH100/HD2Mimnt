using System.Globalization;
using System.Text.Json;
using HD2Mimnt.ModelConversion.Hd2;
using HD2Mimnt.ModelConversion.Ias;

namespace HD2Mimnt.ModelConversion.Gmod.Targets;

/// <summary>
/// 一套可以替换的游戏内护甲：IAS 绑定所需的 Archive / equipment / Unit 闭包，
/// 外加一个「载体」Unit（整套模型接管的那一个槽位）。
///
/// HD2 的一件护甲由多个 Unit 组成（身体约 20–29 个，头盔通常 1 个主头部件）。
/// GMOD 模型是整身一份网格，所以只能接管其中一个 Unit 作为载体，
/// 其余 Unit 由转换器写成零面积占位，否则原护甲会和模型一起显示。
/// </summary>
public sealed record GmodArmorTarget(
    ulong ArchiveId,
    IasContentDomain Domain,
    string Code,
    string Name,
    ulong CarrierUnitId,
    IReadOnlyList<uint> EquipmentKeys,
    IReadOnlyList<ulong> UnitIds)
{
    public string DomainName => IasFreePartsCodec.DomainName(Domain);

    /// <summary>界面与报告里的显示名：护甲代号 + 名称，缺名称时退化为 Archive ID。</summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return $"0x{ArchiveId:x16}";
            if (string.IsNullOrWhiteSpace(Code) || Name.StartsWith(Code, StringComparison.OrdinalIgnoreCase)) return Name;
            return $"{Code} · {Name}";
        }
    }

    /// <summary>需要写成占位网格的 Unit：闭包里除载体以外的全部 Unit。</summary>
    public IReadOnlyList<ulong> CleanupUnitIds => UnitIds.Where(id => id != CarrierUnitId).Order().ToArray();
}

/// <summary>
/// 随程序分发的目标护甲目录（<c>Config/ArmorTargets.json</c>，由
/// <c>tools/ArmorTargets/build_armor_targets.py</c> 从 HD2SDK 的护甲数据生成）。
/// 转换程序集只负责解析，不猜测游戏目录位置。
/// </summary>
public static class GmodArmorTargetCatalog
{
    public const string Schema = "HD2MimntArmorTargets1";

    public static IReadOnlyList<GmodArmorTarget> Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String ||
            schema.GetString() != Schema)
            throw new Hd2FormatException($"Armour target catalog must declare schema {Schema}.");
        if (!root.TryGetProperty("targets", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new Hd2FormatException("Armour target catalog has no targets array.");

        var targets = new List<GmodArmorTarget>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray()) targets.Add(Read(row));
        if (targets.Count == 0) throw new Hd2FormatException("Armour target catalog is empty.");
        return targets.AsReadOnly();
    }

    /// <summary>
    /// 按护甲代号（<c>TR-62</c>）、显示名或 Archive ID 查找；大小写不敏感。
    /// 代号重复时（同一套甲有多个 Archive）返回第一个匹配项，调用方应展示完整列表让用户选择。
    /// </summary>
    public static GmodArmorTarget? Find(IReadOnlyList<GmodArmorTarget> targets, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var value = key.Trim();
        if (TryParseArchive(value, out var archive))
        {
            var byArchive = targets.FirstOrDefault(target => target.ArchiveId == archive);
            if (byArchive is not null) return byArchive;
        }
        return targets.FirstOrDefault(target => string.Equals(target.Code, value, StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault(target => string.Equals(target.DisplayName, value, StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault(target => string.Equals(target.Name, value, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryParseArchive(string value, out ulong archive)
    {
        archive = 0;
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return text.Length is > 0 and <= 16 &&
            ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out archive) &&
            archive != 0;
    }

    private static GmodArmorTarget Read(JsonElement row)
    {
        var archiveText = RequiredString(row, "archive");
        if (!TryParseArchive(archiveText, out var archive))
            throw new Hd2FormatException($"Armour target has an invalid archive id: {archiveText}");
        var domain = RequiredString(row, "domain") switch
        {
            "BODY" => IasContentDomain.Body,
            "HELMET" => IasContentDomain.Helmet,
            var other => throw new Hd2FormatException($"Armour target 0x{archive:x16} has an unknown domain {other}.")
        };
        var carrier = RequiredUnit(row, "carrier");
        var units = RequiredUnits(row, "units");
        if (!units.Contains(carrier))
            throw new Hd2FormatException($"Armour target 0x{archive:x16} carries a Unit outside its own closure.");
        var equipment = new List<uint>();
        if (!row.TryGetProperty("equipment", out var keys) || keys.ValueKind != JsonValueKind.Array)
            throw new Hd2FormatException($"Armour target 0x{archive:x16} has no equipment keys.");
        foreach (var key in keys.EnumerateArray())
        {
            var text = key.GetString();
            if (text is null || !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) ||
                value == 0)
                throw new Hd2FormatException($"Armour target 0x{archive:x16} has an invalid equipment key.");
            equipment.Add(value);
        }
        if (equipment.Count == 0)
            throw new Hd2FormatException($"Armour target 0x{archive:x16} has no equipment keys.");
        return new GmodArmorTarget(archive, domain, OptionalString(row, "code"), OptionalString(row, "name"),
            carrier, equipment.AsReadOnly(), units);
    }

    private static string RequiredString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new Hd2FormatException($"Armour target is missing \"{name}\".");

    private static string OptionalString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static ulong RequiredUnit(JsonElement row, string name)
    {
        var text = RequiredString(row, name);
        return TryParseArchive(text, out var unit) && unit != 0
            ? unit
            : throw new Hd2FormatException($"Armour target has an invalid {name} Unit: {text}");
    }

    private static IReadOnlyList<ulong> RequiredUnits(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
            throw new Hd2FormatException($"Armour target is missing \"{name}\".");
        var units = new List<ulong>(values.GetArrayLength());
        foreach (var value in values.EnumerateArray())
        {
            var text = value.GetString();
            if (text is null || !TryParseArchive(text, out var unit) || unit == 0)
                throw new Hd2FormatException($"Armour target has an invalid Unit in \"{name}\": {text}");
            units.Add(unit);
        }
        if (units.Count == 0) throw new Hd2FormatException($"Armour target has an empty \"{name}\" list.");
        return units.AsReadOnly();
    }
}
