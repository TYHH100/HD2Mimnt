using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Xunit;

namespace HD2Mimnt.Tests.Repositories;

public class AIProviderConfigRepositoryTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly AIProviderConfigRepository _repository;
    private readonly string _testDbPath;

    public AIProviderConfigRepositoryTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_aiprovider_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();
        _repository = new AIProviderConfigRepository(_dbContext);
    }

    [Fact]
    public async Task AddAsync_ValidConfig_Success()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "openai-1",
            Name = "OpenAI GPT-4",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com/v1/chat/completions",
            ApiKey = "sk-test-key",
            Model = "gpt-4-turbo",
            IsEnabled = true,
            MaxTokens = 4000,
            Temperature = 0.7,
            CreatedAt = DateTime.UtcNow
        };

        // Act
        await _repository.AddAsync(config);

        // Assert
        var retrieved = await _repository.GetByIdAsync(config.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(config.Name, retrieved.Name);
        Assert.Equal(config.Type, retrieved.Type);
        Assert.Equal(config.ApiEndpoint, retrieved.ApiEndpoint);
        Assert.Equal(config.Model, retrieved.Model);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllConfigs()
    {
        // Arrange
        var config1 = new AIProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com/v1/chat/completions",
            CreatedAt = DateTime.UtcNow
        };
        var config2 = new AIProviderConfig
        {
            Id = "claude",
            Name = "Claude",
            Type = AIProviderType.Claude,
            ApiEndpoint = "https://api.anthropic.com/v1/messages",
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(config1);
        await _repository.AddAsync(config2);

        // Act
        var configs = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(2, configs.Count);
        Assert.Contains(configs, c => c.Id == "openai");
        Assert.Contains(configs, c => c.Id == "claude");
    }

    [Fact]
    public async Task GetActiveProviderAsync_ReturnsEnabledProvider()
    {
        // Arrange
        var enabledConfig = new AIProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com/v1/chat/completions",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        var disabledConfig = new AIProviderConfig
        {
            Id = "claude",
            Name = "Claude",
            Type = AIProviderType.Claude,
            ApiEndpoint = "https://api.anthropic.com/v1/messages",
            IsEnabled = false,
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(enabledConfig);
        await _repository.AddAsync(disabledConfig);

        // Act
        var active = await _repository.GetActiveProviderAsync();

        // Assert
        Assert.NotNull(active);
        Assert.Equal("openai", active.Id);
        Assert.True(active.IsEnabled);
    }

    [Fact]
    public async Task GetActiveProviderAsync_NoEnabledProvider_ReturnsNull()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com/v1/chat/completions",
            IsEnabled = false,
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(config);

        // Act
        var active = await _repository.GetActiveProviderAsync();

        // Assert
        Assert.Null(active);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingConfig_ReturnsConfig()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "test",
            Name = "Test Provider",
            Type = AIProviderType.LocalLLM,
            ApiEndpoint = "http://localhost:11434/api/generate",
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(config);

        // Act
        var retrieved = await _repository.GetByIdAsync("test");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Test Provider", retrieved.Name);
        Assert.Equal(AIProviderType.LocalLLM, retrieved.Type);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentConfig_ReturnsNull()
    {
        // Act
        var retrieved = await _repository.GetByIdAsync("nonexistent");

        // Assert
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task ExistsAsync_ExistingConfig_ReturnsTrue()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "test",
            Name = "Test",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com",
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(config);

        // Act
        var exists = await _repository.ExistsAsync("test");

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsAsync_NonExistentConfig_ReturnsFalse()
    {
        // Act
        var exists = await _repository.ExistsAsync("nonexistent");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesConfigFields()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "test",
            Name = "Original Name",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com",
            Model = "gpt-3.5-turbo",
            IsEnabled = false,
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(config);

        // Act
        config.Name = "Updated Name";
        config.Model = "gpt-4-turbo";
        config.IsEnabled = true;
        config.MaxTokens = 8000;
        await _repository.UpdateAsync(config);

        // Assert
        var updated = await _repository.GetByIdAsync("test");
        Assert.NotNull(updated);
        Assert.Equal("Updated Name", updated.Name);
        Assert.Equal("gpt-4-turbo", updated.Model);
        Assert.True(updated.IsEnabled);
        Assert.Equal(8000, updated.MaxTokens);
        Assert.NotNull(updated.ModifiedAt);
    }

    [Fact]
    public async Task DeleteAsync_RemovesConfig()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "test",
            Name = "Test",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com",
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(config);

        // Act
        await _repository.DeleteAsync("test");

        // Assert
        var retrieved = await _repository.GetByIdAsync("test");
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentConfig_NoError()
    {
        // Act & Assert - should not throw
        await _repository.DeleteAsync("nonexistent");
    }

    [Fact]
    public async Task SetActiveProviderAsync_EnablesSpecifiedProvider()
    {
        // Arrange
        var config1 = new AIProviderConfig
        {
            Id = "openai",
            Name = "OpenAI",
            Type = AIProviderType.OpenAI,
            ApiEndpoint = "https://api.openai.com",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        var config2 = new AIProviderConfig
        {
            Id = "claude",
            Name = "Claude",
            Type = AIProviderType.Claude,
            ApiEndpoint = "https://api.anthropic.com",
            IsEnabled = false,
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(config1);
        await _repository.AddAsync(config2);

        // Act
        await _repository.SetActiveProviderAsync("claude");

        // Assert
        var openai = await _repository.GetByIdAsync("openai");
        var claude = await _repository.GetByIdAsync("claude");
        Assert.False(openai!.IsEnabled);
        Assert.True(claude!.IsEnabled);

        var active = await _repository.GetActiveProviderAsync();
        Assert.NotNull(active);
        Assert.Equal("claude", active.Id);
    }

    [Fact]
    public async Task AddAsync_WithAllFields_Success()
    {
        // Arrange
        var config = new AIProviderConfig
        {
            Id = "complete",
            Name = "Complete Provider",
            Type = AIProviderType.Custom,
            ApiEndpoint = "https://custom.api.com/v1/chat",
            ApiKey = "custom-api-key-12345",
            Model = "custom-model-v1",
            IsEnabled = true,
            MaxTokens = 8000,
            Temperature = 0.5,
            CreatedAt = DateTime.UtcNow
        };

        // Act
        await _repository.AddAsync(config);

        // Assert
        var retrieved = await _repository.GetByIdAsync("complete");
        Assert.NotNull(retrieved);
        Assert.Equal("Complete Provider", retrieved.Name);
        Assert.Equal(AIProviderType.Custom, retrieved.Type);
        Assert.Equal("https://custom.api.com/v1/chat", retrieved.ApiEndpoint);
        Assert.Equal("custom-api-key-12345", retrieved.ApiKey);
        Assert.Equal("custom-model-v1", retrieved.Model);
        Assert.Equal(8000, retrieved.MaxTokens);
        Assert.Equal(0.5, retrieved.Temperature);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }
}
