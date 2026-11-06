using System.Globalization;
using System.Text;

namespace HD2Mimnt.ModelConversion.Ias;

public static class IasIsolationManifest
{
    public static byte[] Write(string isolationId, IasFreePartsDocument differences, IasArchiveBinding binding,
        IReadOnlyList<IasUnitRoute>? staticRoutes = null, CancellationToken cancellationToken = default)
    {
        IasFreePartsCodec.ValidateId(isolationId);
        IasFreePartsCodec.Validate(differences, binding, cancellationToken);
        var defaults = differences.Parts.SelectMany(part => part.Options[0].Routes).ToList();
        var allPublished = differences.Parts.SelectMany(part => part.Options).SelectMany(option => option.Routes).ToList();
        staticRoutes ??= [];
        defaults.AddRange(staticRoutes);
        allPublished.AddRange(staticRoutes);
        if (defaults.Count > 64 || defaults.Select(route => route.NativeUnitId).Distinct().Count() != defaults.Count)
            throw new IasFormatException("IAS isolation has duplicate native Units or exceeds 64 routes");
        if (allPublished.Select(route => route.PublishedUnitId).Distinct().Count() != allPublished.Count)
            throw new IasFormatException("Static and difference Unit aliases collide");
        var nativeResources = binding.Equipment.SelectMany(row => row.NativeUnitIds).ToHashSet();
        foreach (var route in staticRoutes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (route.PublishedUnitId == 0 || nativeResources.Contains(route.PublishedUnitId) ||
                binding.Equipment.Any(equipment => !equipment.NativeUnitIds.Contains(route.NativeUnitId)))
                throw new IasFormatException("Static IAS route exceeds ownership or aliases an original Unit");
        }
        var text = new StringBuilder("isolation_version=1\nisolation_id=").Append(isolationId)
            .Append("\ndomain=").Append(IasFreePartsCodec.DomainName(differences.Domain))
            .Append("\narchive=").Append(IasFreePartsCodec.Hex(differences.ArchiveId)).Append('\n');
        foreach (var key in differences.EquipmentKeys.Order())
            text.Append("equipment=0x").Append(key.ToString("X8", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var route in defaults.OrderBy(route => route.NativeUnitId))
            text.Append("unit=").Append(IasFreePartsCodec.Hex(route.NativeUnitId)).Append(',')
                .Append(IasFreePartsCodec.Hex(route.PublishedUnitId)).Append('\n');
        foreach (var route in allPublished.Where(route => route.HasRigProfile).OrderBy(route => route.PublishedUnitId))
            text.Append("rig_profile=").Append(IasFreePartsCodec.Hex(route.PublishedUnitId)).Append('\n');
        cancellationToken.ThrowIfCancellationRequested();
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
