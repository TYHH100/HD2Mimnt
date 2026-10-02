using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public class PluginStorageTests
{
    private readonly Mock<IPluginService> _mockPluginService;
    private readonly PluginStorage _storage;
    private readonly string _tempBaseDirectory;
    private const string TestPluginId = "test.plugin";

    public PluginStorageTests()
    {
        _mockPluginService = new Mock<IPluginService>();
        _tempBaseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _storage = new PluginStorage(TestPluginId, _tempBaseDirectory, _mockPluginService.Object);
    }

    [Fact]
    public void GetDataDirectory_ReturnsIsolatedDirectory()
    {
        // Act
        var dataDir = _storage.GetDataDirectory();

        // Assert
        Assert.NotNull(dataDir);
        Assert.Contains("PluginData", dataDir);
        Assert.Contains(TestPluginId, dataDir);
        Assert.True(Directory.Exists(dataDir));
    }

    [Fact]
    public async Task WriteFileAsync_WithPermission_CreatesFile()
    {
        // Arrange
        var fileName = "test.txt";
        var content = "Hello, Plugin!";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(true);

        // Act
        await _storage.WriteFileAsync(fileName, content);

        // Assert
        Assert.True(_storage.FileExists(fileName));
    }

    [Fact]
    public async Task WriteFileAsync_WithoutPermission_ThrowsUnauthorizedException()
    {
        // Arrange
        var fileName = "test.txt";
        var content = "Hello, Plugin!";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(false);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _storage.WriteFileAsync(fileName, content));
    }

    [Fact]
    public async Task ReadFileAsync_WithPermission_ReturnsContent()
    {
        // Arrange
        var fileName = "test.txt";
        var content = "Hello, Plugin!";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(true);
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.read", default)).ReturnsAsync(true);

        await _storage.WriteFileAsync(fileName, content);

        // Act
        var result = await _storage.ReadFileAsync(fileName);

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task ReadFileAsync_WithoutPermission_ThrowsUnauthorizedException()
    {
        // Arrange
        var fileName = "test.txt";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.read", default)).ReturnsAsync(false);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _storage.ReadFileAsync(fileName));
    }

    [Fact]
    public async Task ReadFileAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        // Arrange
        var fileName = "nonexistent.txt";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.read", default)).ReturnsAsync(true);

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.ReadFileAsync(fileName));
    }

    [Fact]
    public void FileExists_ExistingFile_ReturnsTrue()
    {
        // Arrange
        var fileName = "test.txt";
        var content = "Hello, Plugin!";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(true);

        _storage.WriteFileAsync(fileName, content).Wait();

        // Act
        var result = _storage.FileExists(fileName);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void FileExists_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var fileName = "nonexistent.txt";

        // Act
        var result = _storage.FileExists(fileName);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task DeleteFileAsync_WithPermission_DeletesFile()
    {
        // Arrange
        var fileName = "test.txt";
        var content = "Hello, Plugin!";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(true);

        await _storage.WriteFileAsync(fileName, content);
        Assert.True(_storage.FileExists(fileName));

        // Act
        await _storage.DeleteFileAsync(fileName);

        // Assert
        Assert.False(_storage.FileExists(fileName));
    }

    [Fact]
    public async Task DeleteFileAsync_WithoutPermission_ThrowsUnauthorizedException()
    {
        // Arrange
        var fileName = "test.txt";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default))
            .ReturnsAsync((string pluginId, string permission, CancellationToken token) => permission == "storage.write" && pluginId == TestPluginId);

        await _storage.WriteFileAsync(fileName, "content");

        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(false);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _storage.DeleteFileAsync(fileName));
    }

    [Fact]
    public async Task WriteFileAsync_PathTraversal_ThrowsInvalidOperationException()
    {
        // Arrange
        var fileName = "../../../evil.txt";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(true);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _storage.WriteFileAsync(fileName, "evil content"));
    }

    [Fact]
    public async Task WriteFileAsync_AbsolutePath_ThrowsArgumentException()
    {
        // Arrange
        var fileName = "C:\\evil.txt";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(true);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.WriteFileAsync(fileName, "evil content"));
    }

    [Fact]
    public async Task WriteFileAsync_EmptyFileName_ThrowsArgumentException()
    {
        // Arrange
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(true);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.WriteFileAsync("", "content"));
    }

    [Fact]
    public async Task WriteFileAsync_InvalidCharacters_ThrowsArgumentException()
    {
        // Arrange
        var fileName = "test<>|.txt";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(true);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.WriteFileAsync(fileName, "content"));
    }

    [Fact]
    public async Task WriteFileAsync_Subdirectory_CreatesDirectoryStructure()
    {
        // Arrange
        var fileName = "subdir/nested/test.txt";
        var content = "Nested content";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(true);
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.read", default)).ReturnsAsync(true);

        // Act
        await _storage.WriteFileAsync(fileName, content);

        // Assert
        Assert.True(_storage.FileExists(fileName));
        var readContent = await _storage.ReadFileAsync(fileName);
        Assert.Equal(content, readContent);
    }

    [Fact]
    public async Task DeleteFileAsync_NonExistentFile_DoesNotThrow()
    {
        // Arrange
        var fileName = "nonexistent.txt";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "storage.write", default)).ReturnsAsync(true);

        // Act & Assert (should not throw)
        await _storage.DeleteFileAsync(fileName);
    }

    public void Dispose()
    {
        // Cleanup temp directory
        if (Directory.Exists(_tempBaseDirectory))
        {
            Directory.Delete(_tempBaseDirectory, true);
        }
    }
}
