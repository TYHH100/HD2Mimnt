using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Platforms;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace HD2Mimnt.Tests.Platforms;

public class GitHubPlatformAdapterTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly GitHubPlatformAdapter _platform;

    public GitHubPlatformAdapterTests()
    {
        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpHandler.Object);
        _platform = new GitHubPlatformAdapter(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [Fact]
    public void Platform_Should_Have_Correct_Properties()
    {
        Assert.Equal("GitHub", _platform.Name);
        Assert.Equal("github", _platform.Id);
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.Search));
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.Download));
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.Metadata));
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.Versions));
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.Files));
        Assert.False(_platform.Capabilities.HasFlag(PlatformCapabilities.Authentication));
    }

    [Fact]
    public async Task IsAvailableAsync_Should_Return_True()
    {
        SetupMockResponse(HttpStatusCode.OK, "GitHub Zen", "https://api.github.com/zen");

        var available = await _platform.IsAvailableAsync();
        Assert.True(available);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Return_Mod_Details()
    {
        var repoResponse = new
        {
            id = 123,
            name = "TestMod",
            full_name = "testowner/TestMod",
            description = "A test mod",
            owner = new { login = "testowner", avatar_url = "https://example.com/avatar.png" },
            stargazers_count = 100
        };

        var releaseResponse = new
        {
            id = 456,
            tag_name = "v1.0.0",
            body = "Release notes",
            published_at = DateTime.UtcNow,
            assets = new[]
            {
                new
                {
                    id = 789,
                    name = "TestMod-1.0.0.zip",
                    size = 1024000,
                    download_count = 50,
                    browser_download_url = "https://github.com/testowner/TestMod/releases/download/v1.0.0/TestMod-1.0.0.zip",
                    updated_at = DateTime.UtcNow,
                    label = (string?)null
                }
            }
        };

        SetupMultipleResponses(new (HttpStatusCode, object, string)[]
        {
            (HttpStatusCode.OK, repoResponse, "https://api.github.com/repos/testowner/TestMod"),
            (HttpStatusCode.OK, releaseResponse, "https://api.github.com/repos/testowner/TestMod/releases/latest")
        });

        var modInfo = await _platform.GetModInfoAsync("testowner/TestMod");

        Assert.NotNull(modInfo);
        Assert.Equal("testowner/TestMod", modInfo.Id);
        Assert.Equal("TestMod", modInfo.Name);
        Assert.Equal("testowner", modInfo.Author);
        Assert.Equal("v1.0.0", modInfo.Version);
        Assert.Equal(50, modInfo.DownloadCount);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Handle_Tagged_Release()
    {
        var repoResponse = new
        {
            id = 123,
            name = "TestMod",
            full_name = "testowner/TestMod",
            description = "A test mod",
            owner = new { login = "testowner", avatar_url = "https://example.com/avatar.png" },
            stargazers_count = 100
        };

        var releaseResponse = new
        {
            id = 456,
            tag_name = "v2.0.0",
            body = "Version 2 release",
            published_at = DateTime.UtcNow,
            assets = new[]
            {
                new
                {
                    id = 789,
                    name = "TestMod-2.0.0.zip",
                    size = 2048000,
                    download_count = 25,
                    browser_download_url = "https://github.com/testowner/TestMod/releases/download/v2.0.0/TestMod-2.0.0.zip",
                    updated_at = DateTime.UtcNow,
                    label = (string?)null
                }
            }
        };

        SetupMultipleResponses(new (HttpStatusCode, object, string)[]
        {
            (HttpStatusCode.OK, repoResponse, "https://api.github.com/repos/testowner/TestMod"),
            (HttpStatusCode.OK, releaseResponse, "https://api.github.com/repos/testowner/TestMod/releases/tags/v2.0.0")
        });

        var modInfo = await _platform.GetModInfoAsync("testowner/TestMod/v2.0.0");

        Assert.NotNull(modInfo);
        Assert.Equal("v2.0.0", modInfo.Version);
    }

    [Fact]
    public async Task GetFilesAsync_Should_Return_Files()
    {
        var releaseResponse = new
        {
            id = 456,
            tag_name = "v1.0.0",
            body = "Release notes",
            published_at = DateTime.UtcNow,
            assets = new[]
            {
                new
                {
                    id = 789,
                    name = "TestMod-1.0.0.zip",
                    size = 1024000,
                    download_count = 50,
                    browser_download_url = "https://github.com/testowner/TestMod/releases/download/v1.0.0/TestMod-1.0.0.zip",
                    updated_at = DateTime.UtcNow,
                    label = "Main file"
                },
                new
                {
                    id = 790,
                    name = "TestMod-Source-1.0.0.zip",
                    size = 512000,
                    download_count = 10,
                    browser_download_url = "https://github.com/testowner/TestMod/releases/download/v1.0.0/TestMod-Source-1.0.0.zip",
                    updated_at = DateTime.UtcNow,
                    label = "Source code"
                }
            }
        };

        SetupMockResponse(HttpStatusCode.OK, releaseResponse, "https://api.github.com/repos/testowner/TestMod/releases/latest");

        var files = await _platform.GetFilesAsync("testowner/TestMod");

        Assert.NotNull(files);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Name == "TestMod-1.0.0.zip");
        Assert.Contains(files, f => f.Name == "TestMod-Source-1.0.0.zip");
    }

    [Fact]
    public async Task GetVersionsAsync_Should_Return_Versions()
    {
        var releasesResponse = new[]
        {
            new
            {
                id = 456,
                tag_name = "v2.0.0",
                body = "Version 2",
                published_at = DateTime.UtcNow,
                assets = new[]
                {
                    new
                    {
                        id = 789,
                        name = "TestMod-2.0.0.zip",
                        size = 2048000,
                        download_count = 25,
                        browser_download_url = "https://github.com/testowner/TestMod/releases/download/v2.0.0/TestMod-2.0.0.zip",
                        updated_at = DateTime.UtcNow,
                        label = (string?)null
                    }
                }
            },
            new
            {
                id = 455,
                tag_name = "v1.0.0",
                body = "Version 1",
                published_at = DateTime.UtcNow.AddDays(-7),
                assets = new[]
                {
                    new
                    {
                        id = 788,
                        name = "TestMod-1.0.0.zip",
                        size = 1024000,
                        download_count = 50,
                        browser_download_url = "https://github.com/testowner/TestMod/releases/download/v1.0.0/TestMod-1.0.0.zip",
                        updated_at = DateTime.UtcNow.AddDays(-7),
                        label = (string?)null
                    }
                }
            }
        };

        SetupMockResponse(HttpStatusCode.OK, releasesResponse, "https://api.github.com/repos/testowner/TestMod/releases?per_page=20");

        var versions = await _platform.GetVersionsAsync("testowner/TestMod");

        Assert.NotNull(versions);
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.Version == "v1.0.0");
        Assert.Contains(versions, v => v.Version == "v2.0.0");
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Throw_On_Invalid_ModId()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _platform.GetModInfoAsync("invalid"));
    }

    [Fact]
    public async Task SearchAsync_Should_Return_Repositories()
    {
        var searchResponse = new
        {
            total_count = 1,
            items = new[]
            {
                new
                {
                    id = 123,
                    name = "TestMod",
                    full_name = "testowner/TestMod",
                    description = "A test mod",
                    owner = new { login = "testowner", avatar_url = "https://example.com/avatar.png" },
                    stargazers_count = 100
                }
            }
        };

        var releaseResponse = new
        {
            id = 456,
            tag_name = "v1.0.0",
            body = "Release notes",
            published_at = DateTime.UtcNow,
            assets = new[]
            {
                new
                {
                    id = 789,
                    name = "TestMod-1.0.0.zip",
                    size = 1024000,
                    download_count = 50,
                    browser_download_url = "https://github.com/testowner/TestMod/releases/download/v1.0.0/TestMod-1.0.0.zip",
                    updated_at = DateTime.UtcNow,
                    label = (string?)null
                }
            }
        };

        SetupMultipleResponses(new (HttpStatusCode, object, string)[]
        {
            (HttpStatusCode.OK, searchResponse, "https://api.github.com/search/repositories?q=helldivers%202%20mod%20armor&sort=stars&order=desc&per_page=10&page=1"),
            (HttpStatusCode.OK, releaseResponse, "https://api.github.com/repos/testowner/TestMod/releases/latest")
        });

        var query = new PlatformSearchQuery
        {
            Query = "armor",
            PageSize = 10,
            Page = 1
        };

        var result = await _platform.SearchAsync(query);

        Assert.NotNull(result);
        Assert.Single(result.Mods);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task SearchAsync_Should_Handle_Empty_Results()
    {
        var searchResponse = new
        {
            total_count = 0,
            items = Array.Empty<object>()
        };

        SetupMockResponse(HttpStatusCode.OK, searchResponse);

        var query = new PlatformSearchQuery
        {
            Query = "nonexistent",
            PageSize = 10,
            Page = 1
        };

        var result = await _platform.SearchAsync(query);

        Assert.NotNull(result);
        Assert.Empty(result.Mods);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchAsync_Should_Apply_Sorting()
    {
        var searchResponse = new
        {
            total_count = 2,
            items = new[]
            {
                new
                {
                    id = 123,
                    name = "B Mod",
                    full_name = "owner1/BMod",
                    description = "B",
                    owner = new { login = "owner1", avatar_url = "" },
                    stargazers_count = 50
                },
                new
                {
                    id = 124,
                    name = "A Mod",
                    full_name = "owner2/AMod",
                    description = "A",
                    owner = new { login = "owner2", avatar_url = "" },
                    stargazers_count = 100
                }
            }
        };

        var releaseResponse = new
        {
            id = 456,
            tag_name = "v1.0.0",
            body = "",
            published_at = DateTime.UtcNow,
            assets = new[]
            {
                new
                {
                    id = 789,
                    name = "file.zip",
                    size = 1024,
                    download_count = 10,
                    browser_download_url = "https://example.com/file.zip",
                    updated_at = DateTime.UtcNow,
                    label = (string?)null
                }
            }
        };

        SetupMultipleResponses(new (HttpStatusCode, object, string)[]
        {
            (HttpStatusCode.OK, searchResponse, "https://api.github.com/search/repositories?q=helldivers%202%20mod&sort=stars&order=desc&per_page=10&page=1"),
            (HttpStatusCode.OK, releaseResponse, "https://api.github.com/repos/owner1/BMod/releases/latest"),
            (HttpStatusCode.OK, releaseResponse, "https://api.github.com/repos/owner2/AMod/releases/latest")
        });

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

    private void SetupMockResponse<T>(HttpStatusCode statusCode, T content, string? url = null)
    {
        var json = typeof(T) == typeof(string)
            ? content as string ?? string.Empty
            : JsonSerializer.Serialize(content, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        var response = new HttpResponseMessage
        {
            StatusCode = statusCode,
            Content = new StringContent(json, System.Text.Encoding.UTF8, typeof(T) == typeof(string) ? "text/plain" : "application/json")
        };

        var setup = _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                url == null ? ItExpr.IsAny<HttpRequestMessage>() : ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().StartsWith(url)),
                ItExpr.IsAny<CancellationToken>()
            );

        setup.ReturnsAsync(response);
    }

    private void SetupMultipleResponses<T>(IEnumerable<(HttpStatusCode statusCode, T content, string url)> responses)
    {
        var responseQueue = new Queue<(string url, HttpResponseMessage response)>();

        foreach (var (statusCode, content, url) in responses)
        {
            var json = JsonSerializer.Serialize(content, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            var response = new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            responseQueue.Enqueue((url, response));
        }

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() =>
            {
                if (responseQueue.Count == 0)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                var (url, response) = responseQueue.Dequeue();
                return response;
            });
    }
}
