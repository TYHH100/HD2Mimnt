using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Core.Interfaces;

public interface IModelPreviewReader
{
    Task<ModelPreviewResult> PreviewModelAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        bool forceDecodeOversizedStreams = false,
        CancellationToken cancellationToken = default);

    Task<TexturePreviewData?> PreviewTextureAsync(
        DirectoryInfo modDirectory,
        TextureInspectionItem texture,
        int maxPreviewPixels = 1048576,
        CancellationToken cancellationToken = default);
}
