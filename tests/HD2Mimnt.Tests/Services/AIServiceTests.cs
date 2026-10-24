using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.AI;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public class AIServiceTests
{
    private readonly Mock<IAIProviderConfigRepository> _mockConfigRepository;
    private readonly Mock<IModRepository> _mockModRepository;
    private readonly Mock<ILogger<AIService>> _mockLogger;
    private readonly Mock<ILogger<OpenAIProvider>> _mockOpenAILogger;
    private readonly Mock<ILogger<ClaudeProvider>> _mockClaudeLogger;
    private readonly Mock<ILogger<LocalLLMProvider>> _mockLocalLLMLogger;
    private readonly AIService _service;

    public AIServiceTests()
    {
        _mockConfigRepository = new Mock<IAIProviderConfigRepository>();
        _mockModRepository = new Mock<IModRepository>();
        _mockLogger = new Mock<ILogger<AIService>>();
        _mockOpenAILogger = new Mock<ILogger<OpenAIProvider>>();
        _mockClaudeLogger = new Mock<ILogger<ClaudeProvider>>();
        _mockLocalLLMLogger = new Mock<ILogger<LocalLLMProvider>>();

        _service = new AIService(
            _mockConfigRepository.Object,
            _mockModRepository.Object,
            _mockLogger.Object,
            _mockOpenAILogger.Object,
            _mockClaudeLogger.Object,
            _mockLocalLLMLogger.Object
        );
    }

    [Fact]
    public async Task IsAvailableAsync_NoActiveProvider_ReturnsFalse()
    {
        // Arrange
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync((AIProviderConfig?)null);

        // Act
        var result = await _service.IsAvailableAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_ActiveProviderExists_ReturnsTrue()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com/v1/chat/completions",
            IsEnabled = true
        };
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync(config);

        // Act
        var result = await _service.IsAvailableAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task GetActiveProviderAsync_ReturnsActiveProvider()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com/v1/chat/completions",
            IsEnabled = true
        };
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync(config);

        // Act
        var result = await _service.GetActiveProviderAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("openai", result.Id);
    }

    [Fact]
    public async Task TranslateAsync_NoProvider_ReturnsError()
    {
        // Arrange
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync((AIProviderConfig?)null);

        // Act
        var result = await _service.TranslateAsync("Hello", "zh");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("No AI provider configured", result.Error);
    }

    [Fact]
    public async Task AnalyzeModAsync_NoProvider_ReturnsError()
    {
        // Arrange
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync((AIProviderConfig?)null);

        // Act
        var result = await _service.AnalyzeModAsync(Guid.NewGuid().ToString());

        // Assert
        Assert.False(result.Success);
        Assert.Contains("No AI provider configured", result.Error);
    }

    [Fact]
    public async Task AnalyzeModAsync_InvalidModId_ReturnsError()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com/v1/chat/completions",
            IsEnabled = true
        };
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync(config);

        // Act
        var result = await _service.AnalyzeModAsync("invalid-guid");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Invalid mod ID format", result.Error);
    }

    [Fact]
    public async Task AnalyzeModAsync_ModNotFound_ReturnsError()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com/v1/chat/completions",
            IsEnabled = true
        };
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync(config);

        var modId = Guid.NewGuid();
        _mockModRepository.Setup(r => r.GetByIdAsync(modId, default))
            .ReturnsAsync((Mod?)null);

        // Act
        var result = await _service.AnalyzeModAsync(modId.ToString());

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Mod not found", result.Error);
    }

    [Fact]
    public async Task ExplainLogAsync_NoProvider_ReturnsError()
    {
        // Arrange
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync((AIProviderConfig?)null);

        // Act
        var result = await _service.ExplainLogAsync("Error log content");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("No AI provider configured", result.Error);
    }

    [Fact]
    public async Task SummarizeChangelogAsync_NoProvider_ReturnsError()
    {
        // Arrange
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync((AIProviderConfig?)null);

        // Act
        var result = await _service.SummarizeChangelogAsync("Changelog content");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("No AI provider configured", result.Error);
    }

    [Fact]
    public async Task ExplainConflictAsync_NoProvider_ReturnsError()
    {
        // Arrange
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync((AIProviderConfig?)null);

        // Act
        var result = await _service.ExplainConflictAsync(new List<string> { Guid.NewGuid().ToString() });

        // Assert
        Assert.False(result.Success);
        Assert.Contains("No AI provider configured", result.Error);
    }

    [Fact]
    public async Task CheckSecurityAsync_NoProvider_ReturnsError()
    {
        // Arrange
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync((AIProviderConfig?)null);

        // Act
        var result = await _service.CheckSecurityAsync(Guid.NewGuid().ToString());

        // Assert
        Assert.False(result.Success);
        Assert.Contains("No AI provider configured", result.Error);
    }

    [Fact]
    public async Task CheckSecurityAsync_InvalidModId_ReturnsError()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com/v1/chat/completions",
            IsEnabled = true
        };
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync(config);

        // Act
        var result = await _service.CheckSecurityAsync("invalid-guid");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Invalid mod ID format", result.Error);
    }

    [Fact]
    public async Task CheckSecurityAsync_ModNotFound_ReturnsError()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com/v1/chat/completions",
            IsEnabled = true
        };
        _mockConfigRepository.Setup(r => r.GetActiveProviderAsync(default))
            .ReturnsAsync(config);

        var modId = Guid.NewGuid();
        _mockModRepository.Setup(r => r.GetByIdAsync(modId, default))
            .ReturnsAsync((Mod?)null);

        // Act
        var result = await _service.CheckSecurityAsync(modId.ToString());

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Mod not found", result.Error);
    }
}
