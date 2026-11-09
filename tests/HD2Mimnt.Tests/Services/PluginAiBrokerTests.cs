using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public class PluginAiBrokerTests
{
    private const string RealKey = "sk-live-abcdef1234567890abcdef";
    private const string PluginId = "com.example.ai";

    private readonly Mock<IAIService> _ai = new();
    private readonly Mock<IPluginRepository> _pluginRepo = new();
    private readonly Mock<IPluginPermissionRepository> _permissionRepo = new();
    private readonly PluginSecretGuard _guard = new();
    private readonly PluginAiBroker _broker;

    public PluginAiBrokerTests()
    {
        _broker = new PluginAiBroker(_ai.Object, _pluginRepo.Object, _permissionRepo.Object, _guard);
        _pluginRepo.Setup(r => r.GetByIdAsync(PluginId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Plugin { Id = PluginId, Name = "AI", Enabled = true, PermissionMode = "Sandbox" });
        _ai.Setup(s => s.GetActiveProviderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIProviderConfig { Id = "p1", ApiKey = RealKey, MaxTokens = 1000 });
    }

    [Fact]
    public async Task HandleAsync_WhenAiPermissionNotGranted_RejectsWithoutCallingProvider()
    {
        _permissionRepo.Setup(r => r.GetByPluginIdAsync(PluginId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PluginPermission>());

        var outcome = await _broker.HandleAsync(Request("hello"), PluginId);

        Assert.False(outcome.Success);
        Assert.Contains("ai.use", outcome.Error);
        _ai.Verify(s => s.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WithGrantedPermission_ReturnsProviderContent()
    {
        GrantAi();
        _ai.Setup(s => s.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIResponse { Success = true, Content = "1. 要点一\n2. 要点二", TokensUsed = 12 });

        var outcome = await _broker.HandleAsync(Request("summarize this"), PluginId);

        Assert.True(outcome.Success);
        Assert.Equal("1. 要点一\n2. 要点二", outcome.Content);
        Assert.Equal(12, outcome.TokensUsed);
    }

    [Fact]
    public async Task HandleAsync_PromptContainsRealApiKey_IsFatalAndNeverSent()
    {
        GrantAi();

        var outcome = await _broker.HandleAsync(Request($"here is my key: {RealKey}"), PluginId);

        Assert.True(outcome.Fatal);
        Assert.False(outcome.Success);
        _ai.Verify(s => s.CompleteAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ProviderErrorEchoingKey_IsRedactedBeforeReturning()
    {
        GrantAi();
        _ai.Setup(s => s.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIResponse { Success = false, Error = $"Incorrect API key provided: {RealKey}" });

        var outcome = await _broker.HandleAsync(Request("hello"), PluginId);

        Assert.False(outcome.Success);
        Assert.False(outcome.Fatal);
        Assert.DoesNotContain(RealKey, outcome.Error);
    }

    [Fact]
    public async Task HandleAsync_ContentContainingRealKey_IsFatal()
    {
        GrantAi();
        _ai.Setup(s => s.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIResponse { Success = true, Content = $"Your key is {RealKey}" });

        var outcome = await _broker.HandleAsync(Request("echo my key"), PluginId);

        Assert.True(outcome.Fatal);
    }

    [Fact]
    public async Task HandleAsync_DisabledPlugin_IsRejected()
    {
        _pluginRepo.Setup(r => r.GetByIdAsync(PluginId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Plugin { Id = PluginId, Name = "AI", Enabled = false, PermissionMode = "FullAccess" });

        var outcome = await _broker.HandleAsync(Request("hello"), PluginId);

        Assert.False(outcome.Success);
        Assert.Contains("停用", outcome.Error);
    }

    private void GrantAi()
    {
        _permissionRepo.Setup(r => r.GetByPluginIdAsync(PluginId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PluginPermission>
            {
                new() { PluginId = PluginId, Permission = PluginAiBroker.AiPermission, Granted = true }
            });
    }

    private static PluginAiRequest Request(string prompt) => new()
    {
        Type = nameof(PluginAiRequest),
        PluginId = PluginId,
        RequestId = Guid.NewGuid().ToString("N"),
        Prompt = prompt
    };
}
