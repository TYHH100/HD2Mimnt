using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginSDK;
using HD2Mimnt.UI.ViewModels;
using Moq;
using System.Text.Json;

namespace HD2Mimnt.Tests.Services;

public class PluginWebUiTests : IDisposable
{
    private readonly Mock<IPluginRepository> _pluginRepo = new();
    private readonly Mock<IPluginPermissionRepository> _permissionRepo = new();
    private readonly Mock<IPluginExecutionRepository> _executionRepo = new();
    private readonly Mock<IPluginHostManager> _hostManager = new();
    private readonly PluginService _service;
    private readonly string _directory;

    public PluginWebUiTests()
    {
        _service = new PluginService(_pluginRepo.Object, _permissionRepo.Object, _executionRepo.Object, _hostManager.Object);
        _directory = Path.Combine(Path.GetTempPath(), "hd2mimnt-webui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void PluginResult_KeepsLegacyOkOverloadForBinaryCompatibility()
    {
        var overload = typeof(PluginResult).GetMethod(nameof(PluginResult.Ok), new[] { typeof(string), typeof(PluginUiUpdate) });

        Assert.NotNull(overload);
        var result = (PluginResult)overload!.Invoke(null, new object?[] { "ok", null })!;
        Assert.True(result.Success);
        Assert.Equal("ok", result.Message);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Manifest_WebView2Page_DeserializesAndKeepsLegacyDefault()
    {
        var manifest = PluginManifestJson.Deserialize("""
        {
          "id": "com.test.web", "name": "Web", "version": "1.0.0",
          "assembly": "Web.dll", "entryType": "Web.Plugin",
          "ui": { "pages": [
            { "id": "web", "title": "Web", "kind": "WebView2", "entry": "web/index.html", "keepAlive": true },
            { "id": "legacy", "title": "Legacy", "blocks": [{ "type": "text", "text": "hi" }] }
          ]}
        }
        """);

        Assert.NotNull(manifest);
        Assert.True(PluginUiPageKinds.IsWebView2(manifest!.Ui.Pages[0].Kind));
        Assert.Equal("web/index.html", manifest.Ui.Pages[0].Entry);
        Assert.True(manifest.Ui.Pages[0].KeepAlive);
        Assert.True(manifest.Ui.Pages[0].FillWindow);
        Assert.Equal(PluginUiPageKinds.Declarative, manifest.Ui.Pages[1].Kind);
    }

    [Fact]
    public async Task ValidateManifestAsync_WebView2EntryInsidePlugin_IsAccepted()
    {
        var path = await WriteManifestAsync("web/index.html");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Web.dll"), "assembly");
        Directory.CreateDirectory(Path.Combine(_directory, "web"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "web", "index.html"), "<h1>Web</h1>");

        var result = await _service.ValidateManifestAsync(path);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData("../outside.html")]
    [InlineData("https://example.com/index.html")]
    [InlineData("web/index.js")]
    public async Task ValidateManifestAsync_InvalidWebView2Entry_IsRejected(string entry)
    {
        var path = await WriteManifestAsync(entry);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Web.dll"), "assembly");

        var result = await _service.ValidateManifestAsync(path);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ValidateManifestAsync_MissingWebView2Entry_IsRejected()
    {
        var path = await WriteManifestAsync("web/index.html");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Web.dll"), "assembly");

        var result = await _service.ValidateManifestAsync(path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("入口不存在"));
    }

    [Fact]
    public async Task ValidateManifestAsync_UnknownPageKind_IsRejected()
    {
        var path = await WriteManifestAsync("web/index.html", "unknown");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Web.dll"), "assembly");

        var result = await _service.ValidateManifestAsync(path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("kind"));
    }

    [Fact]
    public async Task GetUiContributionsAsync_WebView2BundleExposesSafeResourceRoot()
    {
        const string entry = "web/index.html";
        var path = await WriteManifestAsync(entry);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Web.dll"), "assembly");
        Directory.CreateDirectory(Path.Combine(_directory, "web"));
        var entryPath = Path.Combine(_directory, "web", "index.html");
        await File.WriteAllTextAsync(entryPath, "<h1>Web</h1>");
        _pluginRepo.Setup(repo => repo.GetEnabledAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Plugin>
            {
                new() { Id = "com.test.web", Name = "Web", ManifestPath = path, Enabled = true, PermissionMode = "Sandbox" }
            });
        _permissionRepo.Setup(repo => repo.GetByPluginIdAsync("com.test.web", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HD2Mimnt.Core.Entities.PluginPermission>());

        var bundle = Assert.Single(await _service.GetUiContributionsAsync());
        var page = bundle.Ui.Pages[0];

        Assert.Equal(_directory, bundle.ResourceRoot);
        Assert.Equal(entryPath, bundle.ResolveWebEntryPath(page));
        page.Entry = "../outside.html";
        Assert.Null(bundle.ResolveWebEntryPath(page));
    }

    [Fact]
    public async Task ExecutePluginCommandAsync_ConcurrentCalls_AreSerializedPerPlugin()
    {
        const string pluginId = "com.test.web";
        var path = await WriteManifestAsync("web/index.html");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Web.dll"), "assembly");
        _pluginRepo.Setup(repo => repo.GetByIdAsync(pluginId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Plugin
            {
                Id = pluginId,
                Name = "Web",
                ManifestPath = path,
                AssemblyPath = Path.Combine(_directory, "Web.dll"),
                Enabled = true,
                PermissionMode = "Sandbox"
            });
        _executionRepo.Setup(repo => repo.AddAsync(It.IsAny<PluginExecutionHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _executionRepo.Setup(repo => repo.UpdateAsync(It.IsAny<PluginExecutionHistory>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _hostManager.Setup(manager => manager.GetRunningPlugins())
            .Returns(new List<PluginHostInfo> { new() { PluginId = pluginId } });
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _hostManager.Setup(manager => manager.ExecutePluginCommandAsync(
                pluginId, "dashboard", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.SetResult();
                    await releaseFirst.Task;
                }
                return PluginOperationResult.SuccessResult("ok");
            });

        var first = _service.ExecutePluginCommandAsync(pluginId, "dashboard", "{}");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = _service.ExecutePluginCommandAsync(pluginId, "dashboard", "{}");

        Assert.Equal(1, Volatile.Read(ref calls));
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ExecuteWebCommandAsync_WrapsValuesWithSurfaceContext()
    {
        var bundle = new PluginUiBundle
        {
            PluginId = "com.test.web",
            PluginName = "Web",
            Ui = new PluginUiContributions()
        };
        bundle.Ui.Pages.Add(new PluginPageContribution
        {
            Id = "dashboard",
            Title = "Dashboard",
            Kind = PluginUiPageKinds.WebView2,
            Entry = "web/index.html",
            KeepAlive = true
        });
        var mock = new Mock<IPluginService>();
        string? captured = null;
        mock.Setup(service => service.ExecutePluginCommandAsync(
                "com.test.web", "dashboard", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, CancellationToken>((_, _, arguments, _) => captured = arguments)
            .ReturnsAsync(new PluginOperationResult { Success = true, DataJson = """{"ok":true}""" });
        var surface = PluginUiSurfaceViewModel.CreatePage(bundle, bundle.Ui.Pages[0], mock.Object, null);
        using var document = JsonDocument.Parse("""{"name":"Helldiver","count":2}""");
        var values = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());

        var result = await surface.ExecuteWebCommandAsync("dashboard", values);

        Assert.True(result.Success);
        Assert.True(surface.IsWebView);
        Assert.True(surface.KeepAlive);
        Assert.True(surface.FillWindow);
        Assert.Equal(new Avalonia.Thickness(0), surface.PageMargin);
        Assert.False(surface.IsDeclarative);
        Assert.NotNull(captured);
        using var payload = JsonDocument.Parse(captured!);
        Assert.Equal("Helldiver", payload.RootElement.GetProperty("name").GetString());
        Assert.Equal("com.test.web", payload.RootElement.GetProperty("ui").GetProperty("pluginId").GetString());
        Assert.Equal("dashboard", payload.RootElement.GetProperty("ui").GetProperty("surfaceId").GetString());
        Assert.Equal("webView.invoke", payload.RootElement.GetProperty("ui").GetProperty("actionId").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("ui").GetProperty("values").GetProperty("count").GetInt32());

        surface.WebErrorMessage = "WebView2 failed";
        Assert.True(surface.HasWebError);
        Assert.False(surface.IsWebViewActive);
        surface.WebErrorMessage = null;
        Assert.False(surface.HasWebError);
        Assert.True(surface.IsWebViewActive);
    }

    private async Task<string> WriteManifestAsync(string entry, string kind = PluginUiPageKinds.WebView2)
    {
        var manifest = new PluginManifest
        {
            Id = "com.test.web",
            Name = "Web",
            Version = "1.0.0",
            ApiVersion = PluginSdkInfo.Version,
            Assembly = "Web.dll",
            EntryType = "Web.Plugin",
            Commands = { new PluginCommandDescriptor { Name = "dashboard", DisplayName = "Dashboard" } },
            Ui = new PluginUiContributions
            {
                Pages =
                {
                    new PluginPageContribution
                    {
                        Id = "dashboard",
                        Title = "Dashboard",
                        Kind = kind,
                        Entry = entry
                    }
                }
            }
        };
        var path = Path.Combine(_directory, "manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, PluginManifestJson.Options));
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
