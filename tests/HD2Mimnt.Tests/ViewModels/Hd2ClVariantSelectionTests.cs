using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Xunit;

namespace HD2Mimnt.Tests.ViewModels;

public class Hd2ClVariantSelectionTests
{
    [Fact]
    public async Task Resolve_SingleVariant_SkipsPrompt()
    {
        var service = new FakeConversionService([new Hd2ClVariant("all", "模型", null, [])]);
        var prompt = new FakePrompt(null);

        var (variant, canceled) = await Hd2ClVariantSelection.ResolveAsync(service, prompt, Guid.NewGuid());

        Assert.False(canceled);
        Assert.Null(variant);
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task Resolve_MultipleVariants_ReturnsChosen()
    {
        var chosen = new Hd2ClVariant("components/3", "替换 B-08", null, ["components/0", "components/3"]);
        var service = new FakeConversionService([new Hd2ClVariant("components/2", "另一个", null, []), chosen]);
        var prompt = new FakePrompt(chosen);

        var (variant, canceled) = await Hd2ClVariantSelection.ResolveAsync(service, prompt, Guid.NewGuid());

        Assert.False(canceled);
        Assert.Same(chosen, variant);
        Assert.Equal(1, prompt.Calls);
    }

    [Fact]
    public async Task Resolve_MultipleVariants_CancelReturnsCanceled()
    {
        var service = new FakeConversionService([new Hd2ClVariant("a", "A", null, []), new Hd2ClVariant("b", "B", null, [])]);
        var prompt = new FakePrompt(null);

        var (variant, canceled) = await Hd2ClVariantSelection.ResolveAsync(service, prompt, Guid.NewGuid());

        Assert.True(canceled);
        Assert.Null(variant);
    }

    [Theory]
    [InlineData("Model · 【Witch】Milfy 替换 B-08", "【Witch】Milfy 替换 B-08")]
    [InlineData("HD2IAS · VRC-Milfy-B-08", "VRC-Milfy-B-08")]
    [InlineData("Texture · 贴图包", "贴图包")]
    [InlineData("自定义 · 名字", "自定义 · 名字")]
    [InlineData("普通名称", "普通名称")]
    [InlineData("Model · ", "Model · ")]
    public void StripComponentPrefix_RemovesImportPrefixOnly(string source, string expected)
        => Assert.Equal(expected, Hd2ClNames.StripComponentPrefix(source));

    private sealed class FakePrompt(Hd2ClVariant? result) : IHd2ClVariantPrompt
    {
        public int Calls { get; private set; }

        public Task<Hd2ClVariant?> ChooseAsync(IReadOnlyList<Hd2ClVariant> variants)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeConversionService(IReadOnlyList<Hd2ClVariant> variants) : IHd2ClConversionService
    {
        public Task<IReadOnlyList<Hd2ClCandidate>> GetCandidatesAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<Hd2ClCandidate>>([]);

        public Task<IReadOnlyList<Hd2ClVariant>> GetVariantsAsync(Guid modId, CancellationToken token = default)
            => Task.FromResult(variants);

        public Task<Hd2ClConversionResult> ConvertAsync(Guid modId, IReadOnlyList<string>? componentFolders = null,
            string? productName = null, IProgress<string>? progress = null, CancellationToken token = default)
            => throw new NotSupportedException();

        public Task<Hd2ClExportResult> ExportAsync(Guid modId, string outputDirectory,
            IReadOnlyList<string>? componentFolders = null, string? productName = null,
            IProgress<string>? progress = null, CancellationToken token = default)
            => throw new NotSupportedException();
    }
}
