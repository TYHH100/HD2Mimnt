namespace HD2Mimnt.ModelConversion.Ias;

public enum IasContentDomain { Body, Helmet }
public enum IasPartSlot { Hips, LeftArm, RightArm, Torso, Torso_Armor, LeftLeg, RightLeg, Head }

public sealed record IasEquipmentBinding(uint EquipmentKey, IReadOnlyList<ulong> NativeUnitIds);
public sealed record IasArchiveBinding(ulong ArchiveId, IasContentDomain Domain, IReadOnlyList<IasEquipmentBinding> Equipment);
public sealed record IasUnitRoute(ulong NativeUnitId, ulong PublishedUnitId, bool HasRigProfile = false);
public sealed record IasFreeOption(string Id, string DisplayName, IReadOnlyList<IasUnitRoute> Routes);
public sealed record IasFreePart(string Id, IasPartSlot Slot, string DisplayName, IReadOnlyList<IasFreeOption> Options);
public sealed record IasFreePartsDocument(string ManifestId, IasContentDomain Domain, ulong ArchiveId,
    IReadOnlyList<uint> EquipmentKeys, IReadOnlyList<IasFreePart> Parts);
