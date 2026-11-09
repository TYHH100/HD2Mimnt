using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Analysis;

namespace HD2Mimnt.Application.Services;

/// <summary>一次自动拆包批次的汇总：处理的模组数与提取的 Lua 数量。</summary>
public readonly record struct LuaExtractionBatchSummary(int ProcessedMods, int SourceCount, int BytecodeCount);

/// <summary>
/// 自动拆包 Lua 服务：把模组补丁容器内的 Lua 资源提取到模组目录下的 LuaExtracted 审查目录。
/// 处理记录按内容戳落库：功能关闭再开启时跳过已处理模组，模组内容变化后才重拆。
/// </summary>
public sealed class LuaExtractionService(
    IModRepository modRepository,
    ILuaExtractionStore store,
    SettingsService settings)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsEnabled => settings.GetSetting(SettingKeys.LuaAutoExtract, false);

    /// <summary>对模组库全部模组补拆（开启开关时调用）；设置未开启时直接返回空摘要。</summary>
    public async Task<LuaExtractionBatchSummary> ExtractForLibraryAsync(CancellationToken cancellationToken = default)
    {
        var all = await modRepository.GetAllAsync(cancellationToken);
        return await ExtractForModsAsync([.. all.Select(mod => mod.Id)], cancellationToken);
    }

    /// <summary>对给定模组执行自动拆包；已处理且内容未变化的模组按记录跳过。</summary>
    public async Task<LuaExtractionBatchSummary> ExtractForModsAsync(IReadOnlyList<Guid> modIds,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || modIds.Count == 0) return default;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 读取时顺带清理已删除模组的孤儿记录。
            var records = (await store.GetAllAsync(cancellationToken)).ToDictionary(record => record.ModId);
            var processed = 0;
            var sourceTotal = 0;
            var bytecodeTotal = 0;
            foreach (var modId in modIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mod = await modRepository.GetByIdAsync(modId, cancellationToken);
                if (mod == null || !Directory.Exists(mod.Path)) continue;
                var stamp = await Task.Run(() => ModContentStamp.Calculate(mod.Path), cancellationToken);
                if (records.TryGetValue(modId, out var record) && record.ContentStamp == stamp) continue;
                try
                {
                    var output = Path.Combine(mod.Path, ModDerivedContent.LuaFolderName);
                    // 内容变化后的重拆先清空旧输出，避免残留上一版本的脚本误导审查。
                    if (record is not null)
                        await Task.Run(() => SafeDelete(output), CancellationToken.None);
                    var summary = await Task.Run(
                        () => LuaExtractor.Extract(mod.Path, output, cancellationToken), cancellationToken);
                    // 记录的写入不响应取消：拆包已完成，取消不应留下半程状态。
                    await store.UpsertAsync(new LuaExtractionResult
                    {
                        ModId = modId,
                        ExtractedAtUtc = DateTime.UtcNow,
                        SourceCount = summary.SourceCount,
                        BytecodeCount = summary.BytecodeCount,
                        ContentStamp = stamp
                    }, CancellationToken.None);
                    processed++;
                    sourceTotal += summary.SourceCount;
                    bytecodeTotal += summary.BytecodeCount;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // 单个模组损坏不中断整批；不写记录，下次开启或导入时重试。
                }
            }
            return new LuaExtractionBatchSummary(processed, sourceTotal, bytecodeTotal);
        }
        finally { _gate.Release(); }
    }

    private static void SafeDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
