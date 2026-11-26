using System.Net;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Application.Services.AI;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// Agnes AI 提供商请求格式：思考开关走 chat_template_kwargs.enable_thinking，
/// 且 OpenAIProvider 的原有 reasoning_effort 行为不被子类化改造破坏。
/// </summary>
public class AgnesProviderTests
{
    private const string ChatResponse =
        """
        {"choices":[{"message":{"role":"assistant","content":"你好"}}],
         "usage":{"total_tokens":42,"prompt_tokens":10,"completion_tokens":32}}
        """;

    private static AIProviderConfig CreateConfig(bool enableThinking) => new()
    {
        Id = "default",
        Name = "AI 服务",
        Type = AIProviderType.Agnes,
        ApiEndpoint = "https://apihub.agnes-ai.com/v1/chat/completions",
        ApiKey = "test-key",
        Model = "agnes-3.0-flash",
        IsEnabled = true,
        MaxTokens = 4000,
        Temperature = 0.2,
        EnableThinking = enableThinking,
        ThinkingEffort = "high",
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task SendRequestAsync_ThinkingDisabled_SendsExplicitDisableAndKeepsTemperature()
    {
        var handler = new CapturingHandler();
        var provider = new AgnesProvider(CreateConfig(enableThinking: false), NullLogger<OpenAIProvider>.Instance, new HttpClient(handler));

        var response = await provider.SendRequestAsync(new AIAnalysisContext
        {
            ContextType = AIContextType.Translation,
            TargetLanguage = "zh-CN",
            Data = new Dictionary<string, string> { ["text"] = "Hello" }
        });

        Assert.True(response.Success);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("chat_template_kwargs", out var kwargs));
        Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
        Assert.True(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        Assert.Equal("agnes-3.0-flash", root.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SendRequestAsync_ThinkingEnabled_SendsEnableThinkingWithoutEffortOrTemperature()
    {
        var handler = new CapturingHandler();
        var provider = new AgnesProvider(CreateConfig(enableThinking: true), NullLogger<OpenAIProvider>.Instance, new HttpClient(handler));

        var response = await provider.SendRequestAsync(new AIAnalysisContext
        {
            ContextType = AIContextType.Translation,
            TargetLanguage = "zh-CN",
            Data = new Dictionary<string, string> { ["text"] = "Hello" }
        });

        Assert.True(response.Success);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("chat_template_kwargs", out var kwargs));
        Assert.True(kwargs.GetProperty("enable_thinking").GetBoolean());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task SendRequestAsync_Success_ParsesChatCompletionsResponse()
    {
        var handler = new CapturingHandler();
        var provider = new AgnesProvider(CreateConfig(enableThinking: false), NullLogger<OpenAIProvider>.Instance, new HttpClient(handler));

        var response = await provider.SendRequestAsync(new AIAnalysisContext
        {
            ContextType = AIContextType.Translation,
            TargetLanguage = "zh-CN",
            Data = new Dictionary<string, string> { ["text"] = "Hello" }
        });

        Assert.True(response.Success);
        Assert.Equal("你好", response.Content);
        Assert.Equal(42, response.TokensUsed);
        Assert.Equal(10, response.InputTokens);
        Assert.Contains("Bearer test-key", handler.LastRequest!.Headers.GetValues("Authorization"));
    }

    [Fact]
    public async Task SendRequestAsync_OpenAIThinkingEnabled_StillUsesReasoningEffort()
    {
        var handler = new CapturingHandler();
        var config = CreateConfig(enableThinking: true);
        config.Type = AIProviderType.OpenAI;
        config.Model = "gpt-4o-mini";
        var provider = new OpenAIProvider(config, NullLogger<OpenAIProvider>.Instance, new HttpClient(handler));

        var response = await provider.SendRequestAsync(new AIAnalysisContext
        {
            ContextType = AIContextType.Translation,
            TargetLanguage = "zh-CN",
            Data = new Dictionary<string, string> { ["text"] = "Hello" }
        });

        Assert.True(response.Success);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.TryGetProperty("chat_template_kwargs", out _));
        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());
    }

    [Fact]
    public async Task TestConnectionAsync_UsesFallbackModelWhenConfigModelEmpty()
    {
        var handler = new CapturingHandler();
        var config = CreateConfig(enableThinking: false);
        config.Model = null;
        var provider = new AgnesProvider(config, NullLogger<OpenAIProvider>.Instance, new HttpClient(handler));

        var ok = await provider.TestConnectionAsync();

        Assert.True(ok);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("agnes-3.0-flash", doc.RootElement.GetProperty("model").GetString());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatResponse, Encoding.UTF8, "application/json")
            };
        }
    }
}
