using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Platforms;

namespace HD2Mimnt.Tests.Platforms;

public class MockPlatformAdapterTests : IDisposable
{
    private readonly MockPlatformAdapter _platform;
    private readonly HttpClient _httpClient;

    public MockPlatformAdapterTests()
    {
        _httpClient = new HttpClient();
        _platform = new MockPlatformAdapter(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [Fact]
    public async Task SearchAsync_Should_Return_Results()
    {
        // Arrange
        var query = new PlatformSearchQuery
        {
            Query = "armor",
            PageSize = 10,
            Page = 1
        };

        // Act
        var result = await _platform.SearchAsync(query);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Mods);
        Assert.Contains(result.Mods, m => m.Name.Contains("Armor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Return_Info()
    {
        // Act
        var modInfo = await _platform.GetModInfoAsync("mod1");

        // Assert
        Assert.NotNull(modInfo);
        Assert.Equal("mod1", modInfo.Id);
        Assert.Equal("Test Armor Mod", modInfo.Name);
        Assert.Equal("TestAuthor", modInfo.Author);
    }

    [Fact]
    public async Task DownloadModAsync_Should_Return_Stream()
    {
        // Act
        var stream = await _platform.DownloadModAsync("mod1");

        // Assert
        Assert.NotNull(stream);
        Assert.True(stream.CanRead);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task GetVersionsAsync_Should_Return_Versions()
    {
        // Act
        var versions = await _platform.GetVersionsAsync("mod1");

        // Assert
        Assert.NotNull(versions);
        Assert.NotEmpty(versions);
        Assert.Equal("1.0.0", versions[0].Version);
    }

    [Fact]
    public async Task IsAvailableAsync_Should_Return_True()
    {
        // Act
        var available = await _platform.IsAvailableAsync();

        // Assert
        Assert.True(available);
    }

    [Fact]
    public async Task SearchAsync_Should_Handle_Pagination()
    {
        // Arrange
        var query = new PlatformSearchQuery
        {
            Query = "",
            PageSize = 1,
            Page = 1
        };

        // Act
        var result = await _platform.SearchAsync(query);

        // Assert
        Assert.Single(result.Mods);
        Assert.Equal(1, result.Page);
        Assert.Equal(1, result.PageSize);
        Assert.True(result.HasNextPage);
    }

    [Fact]
    public async Task SearchAsync_Should_Apply_Filters()
    {
        // Arrange
        var query = new PlatformSearchQuery
        {
            Query = "weapon",
            PageSize = 10,
            Page = 1
        };

        // Act
        var result = await _platform.SearchAsync(query);

        // Assert
        Assert.NotEmpty(result.Mods);
        Assert.All(result.Mods, m =>
            Assert.True(m.Name.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
                       m.Description.Contains("weapon", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DownloadModAsync_Should_Report_Progress()
    {
        // Arrange
        DownloadProgress? reportedProgress = null;
        var progress = new ImmediateProgress<DownloadProgress>(p => reportedProgress = p);

        // Act
        await _platform.DownloadModAsync("mod1", progress);

        // Assert
        Assert.NotNull(reportedProgress);
        Assert.True(reportedProgress.Percentage >= 0);
        Assert.True(reportedProgress.TotalBytes > 0);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Throw_For_Unknown_Mod()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _platform.GetModInfoAsync("unknown_mod"));
    }

    [Fact]
    public async Task GetFilesAsync_Should_Return_Files()
    {
        // Act
        var files = await _platform.GetFilesAsync("mod1");

        // Assert
        Assert.NotNull(files);
        Assert.NotEmpty(files);
        Assert.Contains(files, f => f.Name.EndsWith(".zip"));
    }

    [Fact]
    public async Task SearchAsync_Should_Sort_By_Name()
    {
        // Arrange
        var query = new PlatformSearchQuery
        {
            Query = "",
            PageSize = 10,
            Page = 1,
            SortBy = PlatformSearchSort.Name
        };

        // Act
        var result = await _platform.SearchAsync(query);

        // Assert
        Assert.True(result.Mods.Count >= 2);
        Assert.True(string.Compare(result.Mods[0].Name, result.Mods[1].Name, StringComparison.Ordinal) <= 0);
    }

    [Fact]
    public async Task SearchAsync_Should_Sort_By_DownloadCount()
    {
        // Arrange
        var query = new PlatformSearchQuery
        {
            Query = "",
            PageSize = 10,
            Page = 1,
            SortBy = PlatformSearchSort.DownloadCount
        };

        // Act
        var result = await _platform.SearchAsync(query);

        // Assert
        Assert.True(result.Mods.Count >= 2);
        Assert.True(result.Mods[0].DownloadCount >= result.Mods[1].DownloadCount);
    }

    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public ImmediateProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
