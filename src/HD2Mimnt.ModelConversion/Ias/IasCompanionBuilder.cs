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
        IasFreePartsCodec.ValidateId(isolationId);
        IasFreePartsCodec.ValidateId(differences.ManifestId);
        var isolationBytes = IasIsolationManifest.Write(isolationId, differences, binding, staticRoutes, cancellationToken);
        var routes = differences.Parts.SelectMany(part => part.Options).SelectMany(option => option.Routes)
            .Concat(staticRoutes ?? []);
        var declaredProfiles = routes.Where(route => route.HasRigProfile).Select(route => route.PublishedUnitId).ToHashSet();
        var actualProfiles = rig?.Profiles.Select(profile => profile.UnitId).ToHashSet() ?? [];
        if (!declaredProfiles.SetEquals(actualProfiles))
            throw new IasFormatException("IAS difference/isolation rig declarations do not match the supplied rig profiles");
        var differenceBytes = IasFreePartsCodec.Write(differences, binding, cancellationToken);
        var stem = $"{projectId}.{differences.ArchiveId:x16}";
        // 文件名必须等于文件内部声明的 ID：游戏按 <id>.isolation.ini / <id>.difference.ini 回查，
        // 名字与内容不一致时清单会被当成不存在。骨架则按 {32 位项目}.{16 位 Archive} 认领。
        var files = new List<IasCompanionFile>
        {
            File(differences.ManifestId + ".difference.ini", differenceBytes),
            File(isolationId + ".isolation.ini", isolationBytes)
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
