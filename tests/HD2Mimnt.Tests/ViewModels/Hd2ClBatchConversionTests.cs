using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Xunit;

namespace HD2Mimnt.Tests.ViewModels;

public class Hd2ClBatchConversionTests
{
    [Fact]
    public async Task Run_SkipsCanceledAndConvertsRest_WithinConcurrencyLimit()
    {
        var service = new FakeConversionService(skipMod: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var items = Enumerable.Range(1, 4)
            .Select(index => new Hd2ClBatchConversion.Item(Guid.Parse($"00000000-0000-0000-0000-{index:D12}"), "模组" + index))
            .ToList();

        var summary = await Hd2ClBatchConversion.RunAsync(service, new FakePrompt(), items, _ => { });

        Assert.Equal(4, summary.Total);
        Assert.Equal(3, summary.Succeeded);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(3, service.Calls);
        Assert.InRange(service.MaxConcurrency, 1, 2);
    }

    [Fact]
    public async Task Run_CountsFailuresAndKeepsGoing()
    {
        var service = new FakeConversionService(failMod: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var items = Enumerable.Range(1, 3)
            .Select(index => new Hd2ClBatchConversion.Item(Guid.Parse($"00000000-0000-0000-0000-{index:D12}"), "模组" + index))
            .ToList();

        var summary = await Hd2ClBatchConversion.RunAsync(service, new FakePrompt(), items, _ => { });

        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        Assert.Contains("失败 1 个", summary.Describe());
    }

    private sealed class FakePrompt : IHd2ClVariantPrompt
    {
        // 只有一个版本时不弹浮窗；返回 null 也不会被调用。
        public Task<Hd2ClVariant?> ChooseAsync(IReadOnlyList<Hd2ClVariant> variants) => Task.FromResult<Hd2ClVariant?>(null);
    }

    private sealed class FakeConversionService(Guid? skipMod = null, Guid? failMod = null) : IHd2ClConversionService
    {
        private int _active;
        public int Calls { get; private set; }
        public int MaxConcurrency { get; private set; }

        public Task<IReadOnlyList<Hd2ClCandidate>> GetCandidatesAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<Hd2ClCandidate>>([]);

        public Task<IReadOnlyList<Hd2ClVariant>> GetVariantsAsync(Guid modId, CancellationToken token = default)
        {
            // 被跳过的模组给出两个版本，模拟用户在选择浮窗里点取消。
            IReadOnlyList<Hd2ClVariant> variants = modId == skipMod
                ? [new Hd2ClVariant("a", "A", null, []), new Hd2ClVariant("b", "B", null, [])]
                : [new Hd2ClVariant("all", "模型", null, [])];
            return Task.FromResult(variants);
        }

        public async Task<Hd2ClConversionResult> ConvertAsync(Guid modId, IReadOnlyList<string>? componentFolders = null,
            string? productName = null, IProgress<string>? progress = null, CancellationToken token = default)
        {
            Calls++;
            var active = Interlocked.Increment(ref _active);
            MaxConcurrency = Math.Max(MaxConcurrency, active);
            try
            {
                await Task.Delay(30, token);
                if (modId == failMod) throw new InvalidOperationException("模拟失败");
                return new Hd2ClConversionResult(modId, productName ?? "模型", null, false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
