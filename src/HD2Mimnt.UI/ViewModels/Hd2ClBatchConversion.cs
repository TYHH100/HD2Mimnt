using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 批量转换：先逐个确认版本（有多个版本时弹浮窗），再并发执行转换。
/// 单个 Worker 的峰值内存约 1 GB，因此限制同时运行的数量，避免多开把内存打满。
/// </summary>
public static class Hd2ClBatchConversion
{
    private const int MaxConcurrent = 2;

    public sealed record Item(Guid ModId, string DisplayName);

    public sealed record Summary(int Total, int Succeeded, int Skipped, int Failed)
    {
        public string Describe()
        {
            var head = Failed == 0 && Skipped == 0
                ? $"已生成 {Succeeded} 个 HD2CL 角色库模型。"
                : $"HD2CL 批量转换：成功 {Succeeded} 个，跳过 {Skipped} 个，失败 {Failed} 个。";
            return head;
        }
    }

    public static async Task<Summary> RunAsync(IHd2ClConversionService service, IHd2ClVariantPrompt? prompt,
        IReadOnlyList<Item> items, Action<string> report, CancellationToken token = default)
    {
        var targets = new List<(Item Item, Hd2ClVariant? Variant)>();
        var skipped = 0;
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            var (variant, canceled) = await Hd2ClVariantSelection.ResolveAsync(service, prompt, item.ModId);
            if (canceled)
            {
                skipped++;
                report($"已跳过「{item.DisplayName}」。");
                continue;
            }
            targets.Add((item, variant));
        }

        using var gate = new SemaphoreSlim(Math.Max(1, Math.Min(MaxConcurrent, targets.Count)));
        var failed = 0;
        await Task.WhenAll(targets.Select(async target =>
        {
            await gate.WaitAsync(token);
            try
            {
                var result = await service.ConvertAsync(target.Item.ModId, target.Variant?.ComponentFolders,
                    target.Variant?.Name, token: token);
                report($"已生成「{result.Name}」。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                report($"「{target.Item.DisplayName}」转换失败：{ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }));
        return new Summary(items.Count, targets.Count - failed, skipped, failed);
    }
}
