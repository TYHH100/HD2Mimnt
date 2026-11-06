using System.Security.Cryptography;

namespace HD2Mimnt.ModelConversion.Ias;

public sealed record IasCompanionFile(string Name, ReadOnlyMemory<byte> Data, string Sha256);

public static class IasCompanionBuilder
{
    public static IReadOnlyList<IasCompanionFile> Build(string projectId, string isolationId,
        IasFreePartsDocument differences, IasArchiveBinding binding, IasRigDocument? rig = null,
        IReadOnlyList<IasUnitRoute>? staticRoutes = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IasFreePartsCodec.ValidateId(projectId);
        var isolationBytes = IasIsolationManifest.Write(isolationId, differences, binding, staticRoutes, cancellationToken);
        var routes = differences.Parts.SelectMany(part => part.Options).SelectMany(option => option.Routes)
            .Concat(staticRoutes ?? []);
        var declaredProfiles = routes.Where(route => route.HasRigProfile).Select(route => route.PublishedUnitId).ToHashSet();
        var actualProfiles = rig?.Profiles.Select(profile => profile.UnitId).ToHashSet() ?? [];
        if (!declaredProfiles.SetEquals(actualProfiles))
            throw new IasFormatException("IAS difference/isolation rig declarations do not match the supplied rig profiles");
        var differenceBytes = IasFreePartsCodec.Write(differences, binding, cancellationToken);
        var stem = $"{projectId}.{differences.ArchiveId:x16}";
        var files = new List<IasCompanionFile>
        {
            File(stem + ".difference.ini", differenceBytes),
            File(stem + ".isolation.ini", isolationBytes)
        };
        if (rig is not null)
        {
            var rigBytes = IasRigCodec.Write(rig, cancellationToken);
            files.Add(File(stem + ".rigbin", rigBytes));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return files.AsReadOnly();
    }

    private static IasCompanionFile File(string name, byte[] data) =>
        new(name, data, Convert.ToHexStringLower(SHA256.HashData(data)));
}
