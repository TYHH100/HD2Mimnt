using System.Globalization;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasModelOwner(string Archive, uint Equipment, string Domain);

public sealed record IasModelTarget(string Slot, string Profile, IReadOnlyList<IasModelOwner> Owners);

public static class IasModelToIasCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<ulong, IasModelTarget>> Units = new(Load);

    public static IReadOnlyDictionary<ulong, IasModelTarget> All => Units.Value;

    public static bool TryGet(ulong unitId, out IasModelTarget target) => Units.Value.TryGetValue(unitId, out target!);

    private static IReadOnlyDictionary<ulong, IasModelTarget> Load()
    {
        using var stream = typeof(IasModelToIasCatalog).Assembly.GetManifestResourceStream(
            "HD2Mimnt.Infrastructure.ModelConversion.Data.ias_model_to_ias_catalog.json")
            ?? throw new InvalidOperationException("缺少内置模型转 IAS 目录。");
        using var document = JsonDocument.Parse(stream);
        IasBinary.Require(document.RootElement.GetProperty("schema").GetString() == "HD2ModelToIasCatalog1",
            "模型转 IAS 目录格式无效");
        var result = new Dictionary<ulong, IasModelTarget>();
        foreach (var item in document.RootElement.GetProperty("units").EnumerateObject())
        {
            var id = ulong.Parse(item.Name, NumberStyles.HexNumber);
            var owners = item.Value.GetProperty("owners").EnumerateArray().Select(owner =>
                new IasModelOwner(owner.GetProperty("archive").GetString()!,
                    uint.Parse(owner.GetProperty("equipment").GetString()!, NumberStyles.HexNumber),
                    owner.GetProperty("domain").GetString()!)).ToList();
            IasBinary.Require(owners.Count > 0, $"模型 Unit {id:x16} 没有装备归属");
            result.Add(id, new IasModelTarget(item.Value.GetProperty("slot").GetString()!,
                item.Value.GetProperty("profile").GetString()!, owners));
        }
        return result;
    }
}
