using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Controls;
using System.Text.Json;

namespace HD2Mimnt.Tests.Services;

public class PluginWebUiMessageBridgeTests
{
    private readonly string _host = PluginWebUiMessageBridge.CreateHostName("com.test.web", "dashboard");
    private readonly string _origin = $"https://{PluginWebUiMessageBridge.CreateHostName("com.test.web", "dashboard")}/index.html";

    [Fact]
    public void HostName_IsStableAndIsolatedPerSurface()
    {
        Assert.Equal(_host, PluginWebUiMessageBridge.CreateHostName("com.test.web", "dashboard"));
        Assert.NotEqual(_host, PluginWebUiMessageBridge.CreateHostName("com.test.web", "other"));
        Assert.NotEqual(_host, PluginWebUiMessageBridge.CreateHostName("com.test.other", "dashboard"));
        Assert.EndsWith(".hd2mimnt.invalid", _host);
    }

    [Theory]
    [InlineData("https://example.com/index.html")]
    [InlineData("http://host/index.html")]
    [InlineData("file:///C:/index.html")]
    [InlineData(null)]
    public void HandleMessage_ForeignOrigin_IsIgnored(string? source)
    {
        var invoked = 0;
        var bridge = CreateBridge((_, _) => { invoked++; return Task.FromResult(PluginOperationResult.SuccessResult()); }, _ => { });

        bridge.HandleMessage(source, """{"type":"hd2:invoke","requestId":1,"command":"dashboard","arguments":{}}""");

        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task HandleMessage_ValidRequest_ExecutesCommandAndPostsStructuredResult()
    {
        var invoked = new TaskCompletionSource<(string Command, IReadOnlyDictionary<string, JsonElement> Values)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = new TaskCompletionSource<PluginOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = CreateBridge((command, values) =>
        {
            invoked.SetResult((command, values));
            return execution.Task;
        }, json => posted.SetResult(json));

        bridge.HandleMessage(_origin, """{"type":"hd2:invoke","requestId":7,"command":"dashboard","arguments":{"name":"Helldiver","count":2}}""");
        var call = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        execution.SetResult(new PluginOperationResult
        {
            Success = true,
            Message = "ok",
            DataJson = """{"title":"Dashboard","items":[1,2]}"""
        });

        using var response = JsonDocument.Parse(await posted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("dashboard", call.Command);
        Assert.Equal("Helldiver", call.Values["name"].GetString());
        Assert.Equal(7, response.RootElement.GetProperty("requestId").GetInt64());
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Dashboard", response.RootElement.GetProperty("data").GetProperty("title").GetString());
    }

    [Fact]
    public async Task HandleMessage_NonObjectArguments_ReturnsErrorWithoutExecution()
    {
        var posted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = CreateBridge((_, _) => Task.FromResult(PluginOperationResult.SuccessResult()), json => posted.SetResult(json));

        bridge.HandleMessage(_origin, """{"type":"hd2:invoke","requestId":8,"command":"dashboard","arguments":[1,2]}""");

        using var response = JsonDocument.Parse(await posted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("arguments", response.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CancelPending_RejectsOutstandingRequest()
    {
        var execution = new TaskCompletionSource<PluginOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = CreateBridge((_, _) => execution.Task, json => posted.SetResult(json));
        bridge.HandleMessage(_origin, """{"type":"hd2:invoke","requestId":9,"command":"dashboard"}""");

        bridge.CancelPending();

        using var response = JsonDocument.Parse(await posted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(9, response.RootElement.GetProperty("requestId").GetInt64());
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("navigated", response.RootElement.GetProperty("error").GetString());
        execution.SetResult(PluginOperationResult.SuccessResult());
    }

    [Fact]
    public void HandleMessage_OversizedPayload_IsIgnored()
    {
        var invoked = false;
        var bridge = CreateBridge((_, _) => { invoked = true; return Task.FromResult(PluginOperationResult.SuccessResult()); }, _ => { });
        var payload = """{"type":"hd2:invoke","requestId":1,"command":"dashboard","arguments":{"text":"}"""
                       + new string('x', PluginWebUiMessageBridge.MaxMessageBytes)
                       + "\"}}";

        bridge.HandleMessage(_origin, payload);

        Assert.False(invoked);
    }

    [Fact]
    public void IsAllowedResource_BlocksRemoteButAllowsInlineAssets()
    {
        Assert.True(PluginWebUiMessageBridge.IsAllowedResource(new Uri(_origin), _host));
        Assert.True(PluginWebUiMessageBridge.IsAllowedResource(new Uri("data:image/png;base64,AA=="), _host));
        Assert.False(PluginWebUiMessageBridge.IsAllowedResource(new Uri("https://example.com/app.js"), _host));
    }

    private PluginWebUiMessageBridge CreateBridge(
        Func<string, IReadOnlyDictionary<string, JsonElement>, Task<PluginOperationResult>> execute,
        Action<string> post)
        => new(_host, "com.test.web", "dashboard", post, execute);
}
