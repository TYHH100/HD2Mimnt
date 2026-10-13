using System.Net;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms;

namespace HD2Mimnt.Tests.Platforms;

public sealed class NexusModsBrowseTests
{
    [Theory]
    [InlineData(ModBrowseFeed.LatestAdded, "latest_added")]
    [InlineData(ModBrowseFeed.LatestUpdated, "latest_updated")]
    public async Task Browse_ShouldUseDocumentedV1FeedAndMapWebsiteIdentity(ModBrowseFeed feed, string endpoint)
    {
        using var handler = new BrowseHandler("""[{"mod_id":123,"name":"Armor","author":"Builder","summary":"Summary","picture_url":"https://staticdelivery.nexusmods.com/image.png","version":"2.0","updated_timestamp":1791763200}]""");
        using var client = new HttpClient(handler);
        var platform = new NexusModsPlatformAdapter(client, applicationVersion: "0.1.0");
        platform.SetApiKey("browse-key");
        var result = await platform.BrowseAsync(feed);
        Assert.Equal($"https://api.nexusmods.com/v1/games/helldivers2/mods/{endpoint}.json", handler.Url);
        Assert.Equal("browse-key", handler.ApiKey);
        Assert.Equal("HD2Mimnt", handler.ApplicationName);
        Assert.Equal("0.1.0", handler.ApplicationVersion);
        var mod = Assert.Single(result.Mods);
        Assert.Equal("123", mod.Id);
        Assert.Equal("https://www.nexusmods.com/helldivers2/mods/123", mod.Metadata["mod_page_url"]);
        Assert.Equal("Summary", mod.Description);
        Assert.NotNull(mod.UpdatedAt);
        Assert.Equal(10, result.PageSize);
        Assert.False(result.HasNextPage);
        Assert.False(client.DefaultRequestHeaders.Contains("apikey"));
    }

    [Fact]
    public async Task Trending_ShouldRemainPublicAndKeepTheFiveItemScope()
    {
        using var handler = new BrowseHandler("""{"data":{"mods":[{"name":"Armor","mod_page_url":"https://www.nexusmods.com/helldivers2/mods/123"}]}}""");
        using var client = new HttpClient(handler);
        var platform = new NexusModsPlatformAdapter(client, applicationVersion: "0.1.0");
        var result = await platform.BrowseAsync(ModBrowseFeed.Trending);
        Assert.Equal("https://api.nexusmods.com/v3/games/helldivers2/trending-mods", handler.Url);
        Assert.Null(handler.ApiKey);
        Assert.Equal(5, result.PageSize);
        Assert.Contains("5", result.ScopeDescription);
    }

    [Fact]
    public async Task Details_ShouldRejectMismatchedIdentityAndStripPresentationMarkup()
    {
        using var handler = new BrowseHandler("""{"mod_id":123,"name":"Armor","description":"[b]Hello[/b]<br>World &amp; armor"}""");
        using var client = new HttpClient(handler);
        var platform = new NexusModsPlatformAdapter(client, applicationVersion: "0.1.0");
        platform.SetApiKey("browse-key");
        var info = await platform.GetBrowseDetailsAsync("123");
        Assert.Equal("Hello\nWorld & armor", info.Description);
        await Assert.ThrowsAsync<JsonException>(() => platform.GetBrowseDetailsAsync("456"));
        await Assert.ThrowsAsync<ArgumentException>(() => platform.GetBrowseDetailsAsync("123/other"));
    }

    [Fact]
    public async Task ProtectedFeedsAndDetails_ShouldRejectMissingKeyWithoutNetworkRequests()
    {
        using var handler = new BrowseHandler("[]");
        using var client = new HttpClient(handler);
        var platform = new NexusModsPlatformAdapter(client, applicationVersion: "0.1.0");
        await Assert.ThrowsAsync<InvalidOperationException>(() => platform.BrowseAsync(ModBrowseFeed.LatestAdded));
        await Assert.ThrowsAsync<InvalidOperationException>(() => platform.GetBrowseDetailsAsync("123"));
        Assert.Null(handler.Url);
    }

    private sealed class BrowseHandler(string json) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? ApiKey { get; private set; }
        public string? ApplicationName { get; private set; }
        public string? ApplicationVersion { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Url = request.RequestUri!.AbsoluteUri;
            ApiKey = request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null;
            ApplicationName = request.Headers.GetValues("Application-Name").Single();
            ApplicationVersion = request.Headers.GetValues("Application-Version").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Search_ShouldQueryNameOrAuthorWithinGameAndPageWithoutLocalFiltering()
    {
        using var handler = new SearchHandler("""{"data":{"mods":{"totalCount":42,"nodes":[{"modId":123,"gameId":6119,"name":"Unrelated name","author":"Builder","summary":"Found on server"}]}}}""");
        using var client = new HttpClient(handler);
        var platform = new NexusModsPlatformAdapter(client);
        var result = await platform.SearchBrowseAsync(new() { Query = " Builder ", Page = 2, PageSize = 20 });
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.nexusmods.com/v2/graphql", handler.Url);
        Assert.Null(handler.ApiKey);
        var variables = handler.Body.GetProperty("variables");
        Assert.Equal(20, variables.GetProperty("count").GetInt32());
        Assert.Equal(20, variables.GetProperty("offset").GetInt32());
        var filter = variables.GetProperty("filter");
        Assert.Equal("AND", filter.GetProperty("op").GetString());
        Assert.Equal("helldivers2", filter.GetProperty("gameDomainName")[0].GetProperty("value").GetString());
        var match = filter.GetProperty("filter")[0];
        Assert.Equal("OR", match.GetProperty("op").GetString());
        foreach (var field in new[] { "name", "author" })
        {
            Assert.Equal("WILDCARD", match.GetProperty(field)[0].GetProperty("op").GetString());
            Assert.Equal("Builder", match.GetProperty(field)[0].GetProperty("value").GetString());
        }
        Assert.Equal("Unrelated name", Assert.Single(result.Mods).Name);
        Assert.Equal(42, result.TotalCount);
        Assert.True(result.HasNextPage);
    }

    [Theory]
    [InlineData("""{"errors":[{"message":"failure"}],"data":{"mods":{"totalCount":0,"nodes":[]}}}""")]
    [InlineData("""{"data":{"mods":{"totalCount":1,"nodes":[{"modId":123,"gameId":1}]}}}""")]
    public async Task Search_ShouldRejectGraphErrorsAndOtherGames(string response)
    {
        using var handler = new SearchHandler(response);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<JsonException>(() => new NexusModsPlatformAdapter(client).SearchBrowseAsync(new() { Query = "armor" }));
    }

    private sealed class SearchHandler(string json) : HttpMessageHandler
    {
        public JsonElement Body { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? ApiKey { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Method = request.Method; Url = request.RequestUri!.AbsoluteUri;
            ApiKey = request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null;
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Body = document.RootElement.Clone();
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
