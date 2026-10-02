using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Xunit;

namespace HD2Mimnt.Tests.Repositories;

public class ExternalToolRepositoryTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly ExternalToolRepository _repository;
    private readonly string _testDbPath;

    public ExternalToolRepositoryTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_externaltool_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();
        _repository = new ExternalToolRepository(_dbContext);
    }

    [Fact]
    public async Task AddAsync_ValidTool_Success()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "C:\\Windows\\System32\\notepad.exe",
            IsEnabled = true,
            DisplayOrder = 0,
            CreatedAt = DateTime.UtcNow
        };

        // Act
        await _repository.AddAsync(tool);

        // Assert
        var retrieved = await _repository.GetByIdAsync(tool.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(tool.Name, retrieved.Name);
        Assert.Equal(tool.ExecutablePath, retrieved.ExecutablePath);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllTools()
    {
        // Arrange
        var tool1 = new ExternalTool
        {
            Id = "tool1",
            Name = "Tool 1",
            ExecutablePath = "tool1.exe",
            DisplayOrder = 0,
            CreatedAt = DateTime.UtcNow
        };
        var tool2 = new ExternalTool
        {
            Id = "tool2",
            Name = "Tool 2",
            ExecutablePath = "tool2.exe",
            DisplayOrder = 1,
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(tool1);
        await _repository.AddAsync(tool2);

        // Act
        var tools = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Id == "tool1");
        Assert.Contains(tools, t => t.Id == "tool2");
    }

    [Fact]
    public async Task GetEnabledAsync_ReturnsOnlyEnabled()
    {
        // Arrange
        var enabledTool = new ExternalTool
        {
            Id = "enabled",
            Name = "Enabled Tool",
            ExecutablePath = "enabled.exe",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        var disabledTool = new ExternalTool
        {
            Id = "disabled",
            Name = "Disabled Tool",
            ExecutablePath = "disabled.exe",
            IsEnabled = false,
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(enabledTool);
        await _repository.AddAsync(disabledTool);

        // Act
        var tools = await _repository.GetEnabledAsync();

        // Assert
        Assert.Single(tools);
        Assert.Equal("enabled", tools[0].Id);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingTool_ReturnsTool()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "test",
            Name = "Test Tool",
            ExecutablePath = "test.exe",
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(tool);

        // Act
        var retrieved = await _repository.GetByIdAsync("test");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Test Tool", retrieved.Name);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentTool_ReturnsNull()
    {
        // Act
        var retrieved = await _repository.GetByIdAsync("nonexistent");

        // Assert
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task GetByExtensionAsync_ReturnsMatchingTools()
    {
        // Arrange
        var tool1 = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "notepad.exe",
            SupportedExtensions = ".txt,.log",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        var tool2 = new ExternalTool
        {
            Id = "imageeditor",
            Name = "Image Editor",
            ExecutablePath = "editor.exe",
            SupportedExtensions = ".jpg,.png",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(tool1);
        await _repository.AddAsync(tool2);

        // Act
        var txtTools = await _repository.GetByExtensionAsync(".txt");
        var jpgTools = await _repository.GetByExtensionAsync(".jpg");

        // Assert
        Assert.Single(txtTools);
        Assert.Equal("notepad", txtTools[0].Id);
        Assert.Single(jpgTools);
        Assert.Equal("imageeditor", jpgTools[0].Id);
    }

    [Fact]
    public async Task GetByExtensionAsync_NormalizesExtension()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "notepad.exe",
            SupportedExtensions = ".txt",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(tool);

        // Act - without leading dot
        var tools = await _repository.GetByExtensionAsync("txt");

        // Assert
        Assert.Single(tools);
        Assert.Equal("notepad", tools[0].Id);
    }

    [Fact]
    public async Task GetByExtensionAsync_DisabledTools_NotReturned()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "notepad.exe",
            SupportedExtensions = ".txt",
            IsEnabled = false,  // Disabled
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(tool);

        // Act
        var tools = await _repository.GetByExtensionAsync(".txt");

        // Assert
        Assert.Empty(tools);
    }

    [Fact]
    public async Task ExistsAsync_ExistingTool_ReturnsTrue()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "test",
            Name = "Test",
            ExecutablePath = "test.exe",
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(tool);

        // Act
        var exists = await _repository.ExistsAsync("test");

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsAsync_NonExistentTool_ReturnsFalse()
    {
        // Act
        var exists = await _repository.ExistsAsync("nonexistent");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesToolFields()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "test",
            Name = "Original Name",
            ExecutablePath = "original.exe",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(tool);

        // Act
        tool.Name = "Updated Name";
        tool.ExecutablePath = "updated.exe";
        tool.IsEnabled = false;
        await _repository.UpdateAsync(tool);

        // Assert
        var updated = await _repository.GetByIdAsync("test");
        Assert.NotNull(updated);
        Assert.Equal("Updated Name", updated.Name);
        Assert.Equal("updated.exe", updated.ExecutablePath);
        Assert.False(updated.IsEnabled);
        Assert.NotNull(updated.ModifiedAt);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTool()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "test",
            Name = "Test",
            ExecutablePath = "test.exe",
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(tool);

        // Act
        await _repository.DeleteAsync("test");

        // Assert
        var retrieved = await _repository.GetByIdAsync("test");
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentTool_NoError()
    {
        // Act & Assert - should not throw
        await _repository.DeleteAsync("nonexistent");
    }

    [Fact]
    public async Task AddAsync_WithAllFields_Success()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "advanced",
            Name = "Advanced Tool",
            Description = "A tool with all fields",
            ExecutablePath = "C:\\Tools\\advanced.exe",
            Arguments = "-file {FilePath}",
            WorkingDirectory = "C:\\Tools",
            Icon = "tool.ico",
            SupportedExtensions = ".txt,.log,.md",
            IsEnabled = true,
            DisplayOrder = 5,
            CreatedAt = DateTime.UtcNow
        };

        // Act
        await _repository.AddAsync(tool);

        // Assert
        var retrieved = await _repository.GetByIdAsync("advanced");
        Assert.NotNull(retrieved);
        Assert.Equal("Advanced Tool", retrieved.Name);
        Assert.Equal("A tool with all fields", retrieved.Description);
        Assert.Equal("-file {FilePath}", retrieved.Arguments);
        Assert.Equal("C:\\Tools", retrieved.WorkingDirectory);
        Assert.Equal("tool.ico", retrieved.Icon);
        Assert.Equal(".txt,.log,.md", retrieved.SupportedExtensions);
        Assert.Equal(5, retrieved.DisplayOrder);
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
