using System.Text.Json;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 通过预览 Worker 进程巡检音频清单。原版基线比对在 Worker 内完成（读取同一份设置与
/// 游戏目录），UI 只接收清单；关闭预览时进程终止，基线与解析缓存一并释放。
/// </summary>
public sealed class RemoteAudioPreviewReader(IPreviewWorkerSession session) : IAudioPreviewReader
{
    public async Task<AudioInventoryResult> InspectAsync(
        DirectoryInfo modDirectory,
        IReadOnlyList<FileInfo> patchFiles,
        Func<string, IGameAudioBaseline?>? baselineResolver,
        CancellationToken cancellationToken = default)
    {
        // baselineResolver 由主程序构造；远程模式改用 Worker 自己的设置与基线提供器。
        _ = baselineResolver;
        var response = await session.RequestAsync<AudioInspectResponse>(PreviewMethods.InspectAudio,
            new AudioInspectRequest
            {
                ModDirectory = modDirectory.FullName,
                PatchFiles = patchFiles.Select(file => file.FullName).ToList()
            }, cancellationToken);
        if (response is null || string.IsNullOrEmpty(response.InventoryPath))
            throw new InvalidDataException("Preview worker returned no audio inventory.");

        try
        {
            var json = File.ReadAllText(response.InventoryPath);
            return JsonSerializer.Deserialize<AudioInventoryResult>(json)
                ?? throw new InvalidDataException("Preview worker returned an empty audio inventory.");
        }
        finally
        {
            try { File.Delete(response.InventoryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
