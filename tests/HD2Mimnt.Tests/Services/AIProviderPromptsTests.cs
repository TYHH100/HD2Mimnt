using System.Text.Json;
using HD2Mimnt.Application.Services.AI;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public sealed class AIProviderPromptsTests
{
    [Fact]
    public void DeepSeekUsage_ParsesCacheHitAndMiss()
    {
        using var document = JsonDocument.Parse("""
            {"usage":{"total_tokens":900,"prompt_cache_hit_tokens":640,"prompt_cache_miss_tokens":160,"completion_tokens":100}}
            """);

        var usage = AIUsageParser.ParseOpenAiCompatible(document.RootElement);

        Assert.Equal(900, usage.TotalTokens);
        Assert.Equal(800, usage.InputTokens);
        Assert.Equal(640, usage.CachedInputTokens);
    }

    [Fact]
    public void OpenAiUsage_ParsesCachedTokensDetails()
    {
        using var document = JsonDocument.Parse("""
            {"usage":{"total_tokens":500,"prompt_tokens":400,"completion_tokens":100,"prompt_tokens_details":{"cached_tokens":320}}}
            """);

        var usage = AIUsageParser.ParseOpenAiCompatible(document.RootElement);

        Assert.Equal(500, usage.TotalTokens);
        Assert.Equal(400, usage.InputTokens);
        Assert.Equal(320, usage.CachedInputTokens);
    }

    [Fact]
    public void Prompts_KeepSystemPrefixStableAcrossLanguages()
    {
        var chinese = AIProviderPrompts.Build(new AIAnalysisContext
        {
            ContextType = AIContextType.BatchTranslation,
            TargetLanguage = "zh-CN",
            Data = new Dictionary<string, string> { ["items"] = "{\"items\":[{\"id\":0,\"text\":\"A\"}]}" }
        });
        var english = AIProviderPrompts.Build(new AIAnalysisContext
        {
            ContextType = AIContextType.BatchTranslation,
            TargetLanguage = "en-US",
            Data = new Dictionary<string, string> { ["items"] = "{\"items\":[{\"id\":0,\"text\":\"B\"}]}" }
        });

        Assert.Equal(chinese.System, english.System);
        Assert.StartsWith("Target language:", chinese.User);
        Assert.DoesNotContain("zh-CN", chinese.System, StringComparison.Ordinal);
        Assert.Contains("zh-CN", chinese.User, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompts_PutVariableDataInUserMessage()
    {
        var prompt = AIProviderPrompts.Build(new AIAnalysisContext
        {
            ContextType = AIContextType.Translation,
            TargetLanguage = "ja-JP",
            Data = new Dictionary<string, string> { ["text"] = "unique-source-text" }
        });

        Assert.DoesNotContain("unique-source-text", prompt.System, StringComparison.Ordinal);
        Assert.Contains("unique-source-text", prompt.User, StringComparison.Ordinal);
    }
}
