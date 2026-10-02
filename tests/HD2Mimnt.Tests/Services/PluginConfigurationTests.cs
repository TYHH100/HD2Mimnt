using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public class PluginConfigurationTests
{
    private readonly Mock<IPluginService> _mockPluginService;
    private readonly PluginConfiguration _configuration;
    private readonly string _tempBaseDirectory;
    private const string TestPluginId = "test.plugin";

    public PluginConfigurationTests()
    {
        _mockPluginService = new Mock<IPluginService>();
        _tempBaseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _configuration = new PluginConfiguration(TestPluginId, _tempBaseDirectory, _mockPluginService.Object);
    }

    [Fact]
    public void Get_NonExistentKey_ReturnsNull()
    {
        // Act
        var result = _configuration.Get("nonexistent.key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_WithPermission_StoresValue()
    {
        // Arrange
        var key = "test.key";
        var value = "test.value";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        // Act
        await _configuration.SetAsync(key, value);
        var result = _configuration.Get(key);

        // Assert
        Assert.Equal(value, result);
    }

    [Fact]
    public async Task SetAsync_WithoutPermission_ThrowsUnauthorizedException()
    {
        // Arrange
        var key = "test.key";
        var value = "test.value";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(false);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _configuration.SetAsync(key, value));
    }

    [Fact]
    public async Task SetAsync_UpdateExistingKey_OverwritesValue()
    {
        // Arrange
        var key = "test.key";
        var initialValue = "initial.value";
        var updatedValue = "updated.value";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        await _configuration.SetAsync(key, initialValue);

        // Act
        await _configuration.SetAsync(key, updatedValue);
        var result = _configuration.Get(key);

        // Assert
        Assert.Equal(updatedValue, result);
    }

    [Fact]
    public void GetKeys_EmptyConfiguration_ReturnsEmptyList()
    {
        // Act
        var keys = _configuration.GetKeys();

        // Assert
        Assert.Empty(keys);
    }

    [Fact]
    public async Task GetKeys_WithMultipleKeys_ReturnsAllKeys()
    {
        // Arrange
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        await _configuration.SetAsync("key1", "value1");
        await _configuration.SetAsync("key2", "value2");
        await _configuration.SetAsync("key3", "value3");

        // Act
        var keys = _configuration.GetKeys().ToList();

        // Assert
        Assert.Equal(3, keys.Count);
        Assert.Contains("key1", keys);
        Assert.Contains("key2", keys);
        Assert.Contains("key3", keys);
    }

    [Fact]
    public async Task DeleteAsync_WithPermission_RemovesKey()
    {
        // Arrange
        var key = "test.key";
        var value = "test.value";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        await _configuration.SetAsync(key, value);
        Assert.NotNull(_configuration.Get(key));

        // Act
        await _configuration.DeleteAsync(key);

        // Assert
        Assert.Null(_configuration.Get(key));
    }

    [Fact]
    public async Task DeleteAsync_WithoutPermission_ThrowsUnauthorizedException()
    {
        // Arrange
        var key = "test.key";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default))
            .ReturnsAsync((string pluginId, string permission, CancellationToken token) => permission == "config.write" && pluginId == TestPluginId);

        await _configuration.SetAsync(key, "value");

        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(false);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _configuration.DeleteAsync(key));
    }

    [Fact]
    public async Task DeleteAsync_NonExistentKey_DoesNotThrow()
    {
        // Arrange
        var key = "nonexistent.key";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        // Act & Assert (should not throw)
        await _configuration.DeleteAsync(key);
    }

    [Fact]
    public async Task SetAsync_EmptyKey_ThrowsArgumentException()
    {
        // Arrange
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _configuration.SetAsync("", "value"));
    }

    [Fact]
    public async Task SetAsync_NullKey_ThrowsArgumentException()
    {
        // Arrange
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _configuration.SetAsync(null!, "value"));
    }

    [Fact]
    public async Task SetAsync_KeyTooLong_ThrowsArgumentException()
    {
        // Arrange
        var longKey = new string('a', 257); // 257 characters (max is 256)
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _configuration.SetAsync(longKey, "value"));
    }

    [Fact]
    public async Task Configuration_PersistsAcrossInstances()
    {
        // Arrange
        var key = "persistent.key";
        var value = "persistent.value";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        await _configuration.SetAsync(key, value);

        // Act - Create new instance with same plugin ID
        var newConfiguration = new PluginConfiguration(TestPluginId, _tempBaseDirectory, _mockPluginService.Object);
        var result = newConfiguration.Get(key);

        // Assert
        Assert.Equal(value, result);
    }

    [Fact]
    public async Task SetAsync_SpecialCharactersInValue_StoresCorrectly()
    {
        // Arrange
        var key = "special.key";
        var value = "Value with \"quotes\", \n newlines, and 中文字符";
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        // Act
        await _configuration.SetAsync(key, value);
        var result = _configuration.Get(key);

        // Assert
        Assert.Equal(value, result);
    }

    [Fact]
    public async Task SetAsync_ConcurrentAccess_ThreadSafe()
    {
        // Arrange
        _mockPluginService.Setup(s => s.CheckPermissionAsync(TestPluginId, "config.write", default)).ReturnsAsync(true);

        var tasks = new List<Task>();

        // Act - Concurrent writes
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                await _configuration.SetAsync($"key{index}", $"value{index}");
            }));
        }

        await Task.WhenAll(tasks);

        // Assert - All keys should be present
        var keys = _configuration.GetKeys().ToList();
        Assert.Equal(100, keys.Count);
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
