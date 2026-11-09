using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 通过预览 Worker 进程执行模型解析与贴图解码。大块结果由 Worker 写入临时文件，
/// 这里读入托管数组后立即删除文件；UI 进程只保留渲染真正需要的场景数据。
/// </summary>
public sealed class RemoteModelPreviewReader(IPreviewWorkerSession session) : IModelPreviewReader
{
    public async Task<ModelPreviewResult> PreviewModelAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        bool forceDecodeOversizedStreams = false,
        CancellationToken cancellationToken = default)
    {
        var response = await session.RequestAsync<ModelPreviewLoadResponse>(PreviewMethods.LoadModel,
            new ModelPreviewLoadRequest
            {
                ModDirectory = modDirectory.FullName,
                PatchFiles = patchFiles.Select(file => file.FullName).ToList(),
                ForceDecodeOversizedStreams = forceDecodeOversizedStreams
            }, cancellationToken);
        if (response is null || string.IsNullOrEmpty(response.PackPath))
            throw new InvalidDataException("Preview worker returned no model pack.");

        try
        {
            using var stream = new FileStream(response.PackPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            return ModelPreviewPack.Read(stream);
        }
        finally
        {
            TryDelete(response.PackPath);
        }
    }

    public async Task<TexturePreviewData?> PreviewTextureAsync(
        DirectoryInfo modDirectory,
        TextureInspectionItem texture,
        int maxPreviewPixels = 1048576,
        CancellationToken cancellationToken = default)
    {
        var response = await session.RequestAsync<TexturePreviewResponse>(PreviewMethods.PreviewTexture,
            new TexturePreviewRequest
            {
                ModDirectory = modDirectory.FullName,
                Texture = texture,
                MaxPreviewPixels = maxPreviewPixels
            }, cancellationToken);
        if (response is null || !response.Found || string.IsNullOrEmpty(response.PayloadPath))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(response.PayloadPath);
            var isBgra = string.Equals(response.PayloadKind, "bgra", StringComparison.Ordinal);
            return new TexturePreviewData
            {
                Width = response.Width,
                Height = response.Height,
                Description = response.Description,
                BgraPixels = isBgra ? bytes : null,
                EncodedImageBytes = isBgra ? null : bytes
            };
        }
        finally
        {
            TryDelete(response.PayloadPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
