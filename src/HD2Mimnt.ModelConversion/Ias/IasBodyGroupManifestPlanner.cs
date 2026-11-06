using HD2Mimnt.ModelConversion.Source;

namespace HD2Mimnt.ModelConversion.Ias;

public sealed record IasSourceAlternativeBinding(int ModelIndex, string OptionId, IReadOnlyList<IasUnitRoute> Routes);
public sealed record IasSourceBodyGroupBinding(int BodyPartIndex, IasPartSlot Slot, string PartId,
    IReadOnlyList<IasSourceAlternativeBinding> Alternatives);

public static class IasBodyGroupManifestPlanner
{
    public static IasFreePartsDocument Build(SourceModel model, string manifestId, IasArchiveBinding binding,
        IReadOnlyList<IasSourceBodyGroupBinding> groups, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceGroups = model.Mdl.BodyParts.Where(part => part.Models.Count > 1).ToDictionary(part => part.Index);
        if (groups.Count != sourceGroups.Count || !sourceGroups.Keys.ToHashSet().SetEquals(groups.Select(group => group.BodyPartIndex)))
            throw new IasFormatException("Every switchable Source body group must have exactly one explicit IAS binding");
        if (groups.Select(group => group.Slot).Distinct().Count() != groups.Count)
            throw new IasFormatException("Several Source switches share an IAS slot; composite alternatives are required before publishing independent switches");
        var parts = new List<IasFreePart>();
        foreach (var group in groups.OrderBy(group => group.BodyPartIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sourceGroups[group.BodyPartIndex];
            if (group.Alternatives.Count != source.Models.Count ||
                !Enumerable.Range(0, source.Models.Count).ToHashSet().SetEquals(group.Alternatives.Select(option => option.ModelIndex)))
                throw new IasFormatException($"IAS binding omits or duplicates an alternative in Source body group {source.Name}");
            var options = group.Alternatives.OrderBy(option => option.ModelIndex)
                .Select(option => new IasFreeOption(option.OptionId, source.Models[option.ModelIndex].Name, option.Routes)).ToArray();
            parts.Add(new IasFreePart(group.PartId, group.Slot, source.Name, options));
        }
        var result = new IasFreePartsDocument(manifestId, binding.Domain, binding.ArchiveId,
            binding.Equipment.Select(equipment => equipment.EquipmentKey).ToArray(), parts);
        IasFreePartsCodec.Validate(result, binding, cancellationToken);
        return result;
    }
}
