using System.Net;
using System.Text;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Platforms.AyakaMods;

namespace HD2Mimnt.Tests.Platforms;

public sealed class ModMetadataResponseTests
{
    [Fact]
    public async Task AyakaDetails_ShouldMapObservedVersionAndPublicAuthorPage()
    {
        using var client = new HttpClient(new ResponseHandler("""
            {"mod":{"mod_id":5089,"game_id":119,"title":"Health Regeneration","username":"Argxs",
            "version":"2026-10-15","last_update":1792076743,"can_download":false,
            "User":{"user_id":408385,"username":"Argxs","view_url":"https://ayakamods.com/members/argxs.408385/"}}}
            """));
        using var platform = new AyakaModsPlatformAdapter(client);
        platform.SetApiKey("test-only");
        var info = await platform.GetModInfoAsync("5089");
        Assert.Equal("2026-10-15", info.Version);
        Assert.Equal("Argxs", info.Author);
        Assert.Equal("https://ayakamods.com/members/argxs.408385/", info.AuthorUrl);
    }

    [Theory]
    [InlineData("RatInPlat", "https://www.nexusmods.com/users/164716613")]
    [InlineData("Different author", null)]
    public async Task NexusDetails_ShouldOnlyUseUploaderHomepageWhenAuthorMatches(string author, string? expectedUrl)
    {
        using var client = new HttpClient(new ResponseHandler($$"""
            {"mod_id":16633,"name":"Armor Transmog","author":"{{author}}","uploaded_by":"RatInPlat",
            "version":"0.2.3","uploaded_users_profile_url":"https://www.nexusmods.com/users/164716613"}
            """));
        var platform = new NexusModsPlatformAdapter(client, applicationVersion: "0.1.0");
        platform.SetApiKey("test-only");
        var info = await platform.GetBrowseDetailsAsync("16633");
        Assert.Equal(author, info.Author);
        Assert.Equal("0.2.3", info.Version);
        Assert.Equal(expectedUrl, info.AuthorUrl);
    }

    private sealed class ResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
