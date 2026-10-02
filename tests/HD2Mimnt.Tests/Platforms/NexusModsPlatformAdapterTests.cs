using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Platforms;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace HD2Mimnt.Tests.Platforms;

public class NexusModsPlatformAdapterTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly NexusModsPlatformAdapter _platform;

    public NexusModsPlatformAdapterTests()
    {
        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpHandler.Object);
        _platform = new NexusModsPlatformAdapter(_httpClient);
        _platform.SetApiKey("test-api-key");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [Fact]
    public void Platform_Should_Have_Correct_Properties()
    {
        Assert.Equal("NexusMods", _platform.Name);
        Assert.Equal("nexusmods", _platform.Id);
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.Search));
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.Download));
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.Metadata));
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.Authentication));
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.RateLimit));
    }

    [Fact]
    public void SetApiKey_Should_Authenticate_Platform()
    {
        var platform = new NexusModsPlatformAdapter(new HttpClient());
        Assert.False(platform.IsAuthenticated);

        platform.SetApiKey("my-key");
        Assert.True(platform.IsAuthenticated);
    }

    [Fact]
    public void ClearApiKey_Should_Remove_Authentication()
    {
        _platform.ClearApiKey();
        Assert.False(_platform.IsAuthenticated);
    }

    [Fact]
    public async Task IsAvailableAsync_Should_Return_True_When_Authenticated()
    {
        SetupMockResponse(
            HttpStatusCode.OK,
            new { user_id = 123, name = "TestUser" }
        );

        var available = await _platform.IsAvailableAsync();
        Assert.True(available);
    }

    [Fact]
    public async Task IsAvailableAsync_Should_Return_False_When_Not_Authenticated()
    {
        _platform.ClearApiKey();
        var available = await _platform.IsAvailableAsync();
        Assert.False(available);
    }

    [Fact]
    public async Task SearchAsync_Should_Return_Mods()
    {
        var apiResponse = new[]
        {
            new
            {
                mod_id = 1,
                name = "Test Armor Mod",
                summary = "A test armor mod",
                author = "TestAuthor",
                version = "1.0.0",
                updated_time = DateTime.UtcNow,
                unique_downloads = 1000,
                picture_url = "https://example.com/thumb.jpg",
                category_id = 1,
                endorsement_count = 50
            },
            new
            {
                mod_id = 2,
                name = "Test Weapon Mod",
                summary = "A test weapon mod",
                author = "TestAuthor2",
                version = "2.0.0",
                updated_time = DateTime.UtcNow.AddDays(-1),
                unique_downloads = 500,
                picture_url = "https://example.com/thumb2.jpg",
                category_id = 2,
                endorsement_count = 25
            }
        };

        SetupMockResponse(HttpStatusCode.OK, apiResponse);

        var query = new PlatformSearchQuery
        {
            Query = "armor",
            PageSize = 10,
            Page = 1
        };

        var result = await _platform.SearchAsync(query);

        Assert.NotNull(result);
        Assert.Single(result.Mods);
        Assert.Contains(result.Mods, m => m.Name.Contains("Armor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAsync_Should_Throw_When_Not_Authenticated()
    {
        _platform.ClearApiKey();
        var query = new PlatformSearchQuery { Query = "test" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.SearchAsync(query));
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Return_Mod_Details()
    {
        var apiResponse = new
        {
            mod_id = 123,
            name = "Test Mod",
            summary = "Test description",
            author = "TestAuthor",
            version = "1.0.0",
            updated_time = DateTime.UtcNow,
            unique_downloads = 1000,
            picture_url = "https://example.com/thumb.jpg",
            category_id = 1,
            endorsement_count = 50
        };

        SetupMockResponse(HttpStatusCode.OK, apiResponse);

        var modInfo = await _platform.GetModInfoAsync("123");

        Assert.NotNull(modInfo);
        Assert.Equal("123", modInfo.Id);
        Assert.Equal("Test Mod", modInfo.Name);
        Assert.Equal("TestAuthor", modInfo.Author);
        Assert.Equal(1000, modInfo.DownloadCount);
    }

    [Fact]
    public async Task GetFilesAsync_Should_Return_Files()
    {
        var apiResponse = new
        {
            files = new[]
            {
                new
                {
                    file_id = 1,
                    file_name = "TestMod-1.0.0.zip",
                    version = "1.0.0",
                    size_in_bytes = 1024000,
                    uploaded_time = DateTime.UtcNow,
                    description = "Main file"
                }
            }
        };

        SetupMockResponse(HttpStatusCode.OK, apiResponse);

        var files = await _platform.GetFilesAsync("123");

        Assert.NotNull(files);
        Assert.Single(files);
        Assert.Equal("TestMod-1.0.0.zip", files[0].Name);
        Assert.Equal("1.0.0", files[0].Version);
    }

    [Fact]
    public async Task GetVersionsAsync_Should_Return_Versions()
    {
        var apiResponse = new
        {
            files = new[]
            {
                new
                {
                    file_id = 1,
                    file_name = "TestMod-1.0.0.zip",
                    version = "1.0.0",
                    size_in_bytes = 1024000,
                    uploaded_time = DateTime.UtcNow,
                    description = "Version 1.0.0"
                },
                new
                {
                    file_id = 2,
                    file_name = "TestMod-2.0.0.zip",
                    version = "2.0.0",
                    size_in_bytes = 2048000,
                    uploaded_time = DateTime.UtcNow.AddDays(1),
                    description = "Version 2.0.0"
                }
            }
        };

        SetupMockResponse(HttpStatusCode.OK, apiResponse);

        var versions = await _platform.GetVersionsAsync("123");

        Assert.NotNull(versions);
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.Version == "1.0.0");
        Assert.Contains(versions, v => v.Version == "2.0.0");
    }

    [Fact]
    public async Task SearchAsync_Should_Apply_Sorting()
    {
        var apiResponse = new[]
        {
            new
            {
                mod_id = 1,
                name = "B Mod",
                summary = "Test",
                author = "Author1",
                version = "1.0.0",
                updated_time = DateTime.UtcNow,
                unique_downloads = 100,
                picture_url = "",
                category_id = 1,
                endorsement_count = 10
            },
            new
            {
                mod_id = 2,
                name = "A Mod",
                summary = "Test",
                author = "Author2",
                version = "1.0.0",
                updated_time = DateTime.UtcNow,
                unique_downloads = 200,
                picture_url = "",
                category_id = 1,
                endorsement_count = 20
            }
        };

        SetupMockResponse(HttpStatusCode.OK, apiResponse);

        var query = new PlatformSearchQuery
        {
            Query = "",
            PageSize = 10,
            Page = 1,
            SortBy = PlatformSearchSort.Name
        };

        var result = await _platform.SearchAsync(query);

        Assert.Equal(2, result.Mods.Count);
        Assert.Equal("A Mod", result.Mods[0].Name);
        Assert.Equal("B Mod", result.Mods[1].Name);
    }

    [Fact]
    public async Task SearchAsync_Should_Handle_Pagination()
    {
        var apiResponse = new[]
        {
            new
            {
                mod_id = 1,
                name = "Mod 1",
                summary = "Test",
                author = "Author",
                version = "1.0.0",
                updated_time = DateTime.UtcNow,
                unique_downloads = 100,
                picture_url = "",
                category_id = 1,
                endorsement_count = 10
            },
            new
            {
                mod_id = 2,
                name = "Mod 2",
                summary = "Test",
                author = "Author",
                version = "1.0.0",
                updated_time = DateTime.UtcNow,
                unique_downloads = 100,
                picture_url = "",
                category_id = 1,
                endorsement_count = 10
            }
        };

        SetupMockResponse(HttpStatusCode.OK, apiResponse);

        var query = new PlatformSearchQuery
        {
            Query = "",
            PageSize = 1,
            Page = 1
        };

        var result = await _platform.SearchAsync(query);

        Assert.Single(result.Mods);
        Assert.Equal(2, result.TotalCount);
        Assert.True(result.HasNextPage);
    }

    private void SetupMockResponse<T>(HttpStatusCode statusCode, T content)
    {
        var json = JsonSerializer.Serialize(content, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var response = new HttpResponseMessage
        {
            StatusCode = statusCode,
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

        response.Headers.Add("X-RL-Daily-Remaining", "100");
        response.Headers.Add("X-RL-Daily-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(response);
    }
}
