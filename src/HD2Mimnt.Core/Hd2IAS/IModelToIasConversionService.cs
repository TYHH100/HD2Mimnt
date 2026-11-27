namespace HD2Mimnt.Core.Hd2IAS;

public sealed record ModelToIasCandidate(Guid ModId, string Name, string Path, int UnitCount);

public sealed record ModelToIasBranch(string Name, IReadOnlyList<string> Directories);

public sealed record ModelToIasConversionResult(Guid ModId, string Name, Guid? ProfileId, bool SourceDisabled);

public static class ModelToIasProductTag
{
    public const string Value = "ModelToIAS";
}

public interface IModelToIasConversionService
{
    Task<IReadOnlyList<ModelToIasCandidate>> GetCandidatesAsync(CancellationToken token = default);

    Task<ModelToIasConversionResult> ConvertAsync(Guid modId, string? productName = null,
        IProgress<string>? progress = null, CancellationToken token = default);
}
