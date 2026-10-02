using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public class ExternalToolServiceTests
{
    private readonly Mock<IExternalToolRepository> _mockRepository;
    private readonly Mock<ILogger<ExternalToolService>> _mockLogger;
    private readonly ExternalToolService _service;
    private readonly string _tempTestFile;

    public ExternalToolServiceTests()
    {
        _mockRepository = new Mock<IExternalToolRepository>();
        _mockLogger = new Mock<ILogger<ExternalToolService>>();
        _service = new ExternalToolService(_mockRepository.Object, _mockLogger.Object);

        // Create a temporary test file
        _tempTestFile = Path.Combine(Path.GetTempPath(), "test_file.txt");
        File.WriteAllText(_tempTestFile, "test content");
    }

    [Fact]
    public async Task GetAllToolsAsync_ReturnsAllTools()
    {
        // Arrange
        var tools = new List<ExternalTool>
        {
            new ExternalTool { Id = "tool1", Name = "Tool 1", ExecutablePath = "notepad.exe" },
            new ExternalTool { Id = "tool2", Name = "Tool 2", ExecutablePath = "notepad++.exe" }
        };
        _mockRepository.Setup(r => r.GetAllAsync(default)).ReturnsAsync(tools);

        // Act
        var result = await _service.GetAllToolsAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, t => t.Id == "tool1");
        Assert.Contains(result, t => t.Id == "tool2");
    }

    [Fact]
    public async Task GetEnabledToolsAsync_ReturnsOnlyEnabledTools()
    {
        // Arrange
        var tools = new List<ExternalTool>
        {
            new ExternalTool { Id = "tool1", Name = "Tool 1", ExecutablePath = "notepad.exe", IsEnabled = true }
        };
        _mockRepository.Setup(r => r.GetEnabledAsync(default)).ReturnsAsync(tools);

        // Act
        var result = await _service.GetEnabledToolsAsync();

        // Assert
        Assert.Single(result);
        Assert.True(result[0].IsEnabled);
    }

    [Fact]
    public async Task GetToolsForFileAsync_ReturnsToolsForExtension()
    {
        // Arrange
        var tools = new List<ExternalTool>
        {
            new ExternalTool { Id = "tool1", Name = "Notepad", ExecutablePath = "notepad.exe", SupportedExtensions = ".txt,.log" }
        };
        _mockRepository.Setup(r => r.GetByExtensionAsync(".txt", default)).ReturnsAsync(tools);

        // Act
        var result = await _service.GetToolsForFileAsync("test.txt");

        // Assert
        Assert.Single(result);
        Assert.Equal("Notepad", result[0].Name);
    }

    [Fact]
    public async Task GetToolsForFileAsync_EmptyPath_ReturnsEmpty()
    {
        // Act
        var result = await _service.GetToolsForFileAsync("");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetToolsForFileAsync_NoExtension_ReturnsEmpty()
    {
        // Act
        var result = await _service.GetToolsForFileAsync("file_without_extension");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task RegisterToolAsync_ValidTool_Success()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "C:\\Windows\\System32\\notepad.exe"
        };
        _mockRepository.Setup(r => r.ExistsAsync(tool.Id, default)).ReturnsAsync(false);
        _mockRepository.Setup(r => r.AddAsync(tool, default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.RegisterToolAsync(tool);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Tool registered successfully", result.Message);
        _mockRepository.Verify(r => r.AddAsync(tool, default), Times.Once);
    }

    [Fact]
    public async Task RegisterToolAsync_DuplicateId_Failure()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "C:\\Windows\\System32\\notepad.exe"
        };
        _mockRepository.Setup(r => r.ExistsAsync(tool.Id, default)).ReturnsAsync(true);

        // Act
        var result = await _service.RegisterToolAsync(tool);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error);
    }

    [Fact]
    public async Task RegisterToolAsync_InvalidTool_Failure()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "",  // Invalid: empty ID
            Name = "Notepad",
            ExecutablePath = "notepad.exe"
        };

        // Act
        var result = await _service.RegisterToolAsync(tool);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("validation failed", result.Error);
    }

    [Fact]
    public async Task UpdateToolAsync_ExistingTool_Success()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad Updated",
            ExecutablePath = "C:\\Windows\\System32\\notepad.exe"
        };
        _mockRepository.Setup(r => r.GetByIdAsync(tool.Id, default)).ReturnsAsync(tool);
        _mockRepository.Setup(r => r.UpdateAsync(tool, default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.UpdateToolAsync(tool);

        // Assert
        Assert.True(result.Success);
        _mockRepository.Verify(r => r.UpdateAsync(tool, default), Times.Once);
    }

    [Fact]
    public async Task UpdateToolAsync_NonExistentTool_Failure()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "nonexistent",
            Name = "Tool",
            ExecutablePath = "tool.exe"
        };
        _mockRepository.Setup(r => r.GetByIdAsync(tool.Id, default)).ReturnsAsync((ExternalTool?)null);

        // Act
        var result = await _service.UpdateToolAsync(tool);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task DeleteToolAsync_ExistingTool_Success()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "notepad.exe"
        };
        _mockRepository.Setup(r => r.GetByIdAsync(tool.Id, default)).ReturnsAsync(tool);
        _mockRepository.Setup(r => r.DeleteAsync(tool.Id, default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.DeleteToolAsync(tool.Id);

        // Assert
        Assert.True(result.Success);
        _mockRepository.Verify(r => r.DeleteAsync(tool.Id, default), Times.Once);
    }

    [Fact]
    public async Task DeleteToolAsync_NonExistentTool_Failure()
    {
        // Arrange
        _mockRepository.Setup(r => r.GetByIdAsync("nonexistent", default)).ReturnsAsync((ExternalTool?)null);

        // Act
        var result = await _service.DeleteToolAsync("nonexistent");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task SetToolEnabledAsync_Enable_Success()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "notepad.exe",
            IsEnabled = false
        };
        _mockRepository.Setup(r => r.GetByIdAsync(tool.Id, default)).ReturnsAsync(tool);
        _mockRepository.Setup(r => r.UpdateAsync(It.IsAny<ExternalTool>(), default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.SetToolEnabledAsync(tool.Id, true);

        // Assert
        Assert.True(result.Success);
        Assert.True(tool.IsEnabled);
    }

    [Fact]
    public async Task SetToolEnabledAsync_Disable_Success()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "notepad.exe",
            IsEnabled = true
        };
        _mockRepository.Setup(r => r.GetByIdAsync(tool.Id, default)).ReturnsAsync(tool);
        _mockRepository.Setup(r => r.UpdateAsync(It.IsAny<ExternalTool>(), default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.SetToolEnabledAsync(tool.Id, false);

        // Assert
        Assert.True(result.Success);
        Assert.False(tool.IsEnabled);
    }

    [Fact]
    public async Task ValidateToolAsync_ValidTool_Success()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "C:\\Windows\\System32\\notepad.exe"
        };

        // Act
        var result = await _service.ValidateToolAsync(tool);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateToolAsync_EmptyId_Failure()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "",
            Name = "Notepad",
            ExecutablePath = "notepad.exe"
        };

        // Act
        var result = await _service.ValidateToolAsync(tool);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ID is required"));
    }

    [Fact]
    public async Task ValidateToolAsync_EmptyName_Failure()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "",
            ExecutablePath = "notepad.exe"
        };

        // Act
        var result = await _service.ValidateToolAsync(tool);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("name is required"));
    }

    [Fact]
    public async Task ValidateToolAsync_EmptyExecutablePath_Failure()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = ""
        };

        // Act
        var result = await _service.ValidateToolAsync(tool);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Executable path is required"));
    }

    [Fact]
    public async Task ValidateToolAsync_NonExistentExecutable_Failure()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "tool",
            Name = "Tool",
            ExecutablePath = "C:\\NonExistent\\tool.exe"
        };

        // Act
        var result = await _service.ValidateToolAsync(tool);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Executable not found"));
    }

    [Fact]
    public async Task ValidateToolAsync_ExtensionWithoutDot_Warning()
    {
        // Arrange
        var tool = new ExternalTool
        {
            Id = "notepad",
            Name = "Notepad",
            ExecutablePath = "C:\\Windows\\System32\\notepad.exe",
            SupportedExtensions = "txt,log"  // Missing dots
        };

        // Act
        var result = await _service.ValidateToolAsync(tool);

        // Assert
        Assert.True(result.IsValid);  // Still valid, just warnings
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("should start with a dot"));
    }

    public void Dispose()
    {
        // Cleanup temp test file
        if (File.Exists(_tempTestFile))
        {
            File.Delete(_tempTestFile);
        }
    }
}
