using System.Linq;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.AI;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class OptionTranslationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"hd2mimnt-ai-translation-{Guid.NewGuid():N}");

    private OptionTranslationService CreateService(Mock<IAIService> ai)
    {
        var repository = new Mock<ISettingsRepository>();
        repository.Setup(repo => repo.IsLegacyMigrationComplete()).Returns(true);
        repository.Setup(repo => repo.ReadAll()).Returns(new Dictionary<string, string>
        {
            [SettingKeys.AiTranslationTargetLanguage] = "\"zh-CN\""
        });
        repository.Setup(repo => repo.ApplyAsync(
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(Task.CompletedTask);
        repository.Setup(repo => repo.ResetAsync(It.IsAny<string?>())).Returns(Task.CompletedTask);

        var settings = new SettingsService(repository.Object, _root);
        return new OptionTranslationService(ai.Object, settings, Path.Combine(_root, "cache"));
    }

    private static Mock<IAIService> CreateAi(Func<string, string> translate)
    {
        var ai = new Mock<IAIService>();
        ai.Setup(service => service.TranslateBatchAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, string language, CancellationToken _) =>
                new AIBatchTranslationResult
                {
                    Success = true,
                    Translations = texts.ToDictionary(text => text, translate),
                    TokensUsed = 10
                });
        return ai;
    }

    [Fact]
    public async Task TranslateAsync_SecondCall_ShouldUseLocalCache()
    {
        var ai = CreateAi(text => $"译:{text}");
        var service = CreateService(ai);

        var first = await service.TranslateAsync(["Option A", "Option B"]);
        var second = await service.TranslateAsync(["Option A", "Option B"]);

        Assert.Equal(2, first.ApiTextCount);
        Assert.Equal(0, first.LocalCacheHits);
        Assert.Equal(0, second.ApiTextCount);
        Assert.Equal(2, second.LocalCacheHits);
        Assert.Equal("译:Option A", second.Translations["Option A"]);
        ai.Verify(api => api.TranslateBatchAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TranslateAsync_ShouldIgnoreBlankAndDuplicateTexts()
    {
        var ai = CreateAi(text => $"译:{text}");
        var service = CreateService(ai);

        var result = await service.TranslateAsync(["Option A", "", "  ", "Option A", "Option B"]);

        Assert.Equal(2, result.Translations.Count);
        ai.Verify(api => api.TranslateBatchAsync(
            It.Is<IReadOnlyList<string>>(texts => texts.Count == 2),
            "Simplified Chinese",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TranslateAsync_WhenProviderFails_ShouldThrowAndNotCache()
    {
        var ai = new Mock<IAIService>();
        ai.Setup(service => service.TranslateBatchAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIBatchTranslationResult { Success = false, Error = "boom" });
        var service = CreateService(ai);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TranslateAsync(["Option A"]));
        Assert.Contains("boom", exception.Message);

        var cached = await service.GetCachedTranslationsAsync(["Option A"]);
        Assert.Empty(cached);
    }

    [Fact]
    public async Task GetCachedTranslationsAsync_ShouldReturnOnlyKnownTexts()
    {
        var ai = CreateAi(text => $"译:{text}");
        var service = CreateService(ai);
        await service.TranslateAsync(["Known"]);

        var cached = await service.GetCachedTranslationsAsync(["Known", "Unknown"]);

        Assert.Single(cached);
        Assert.Equal("译:Known", cached["Known"]);
    }

    [Fact]
    public void Parser_ShouldAcceptJsonArrayAndJsonLines()
    {
        Assert.True(TranslationResponseParser.TryParse(
            "[{\"id\":0,\"text\":\"甲\"},{\"id\":1,\"text\":\"乙\"}]",
            ["A", "B"],
            out var arrayTranslations,
            out var arrayError));
        Assert.Null(arrayError);
        Assert.Equal("乙", arrayTranslations["B"]);

        Assert.True(TranslationResponseParser.TryParse(
            "```json\n{\"id\":0,\"text\":\"甲\"}\n{\"id\":1,\"text\":\"乙\"}\n```",
            ["A", "B"],
            out var lineTranslations,
            out var lineError));
        Assert.Null(lineError);
        Assert.Equal("甲", lineTranslations["A"]);
    }

    [Fact]
    public void Parser_WhenItemsMissing_ShouldFail()
    {
        Assert.False(TranslationResponseParser.TryParse(
            "[{\"id\":0,\"text\":\"甲\"}]",
            ["A", "B"],
            out _,
            out var error));
        Assert.Contains("不完整", error);
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
