using System.Text.Json;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.Samples.WebUiDashboard;

public sealed class WebUiDashboardPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "com.hd2mimnt.samples.webui",
        Name = "WebView2 界面示例",
        Version = "1.0.0",
        Author = "HD2Mimnt",
        Description = "WebView2 页面示例。",
        ApiVersion = PluginSdkInfo.Version,
        Runtime = "net10.0",
        Assembly = "WebUiDashboard.dll",
        EntryType = typeof(WebUiDashboardPlugin).FullName!,
        Ui = new PluginUiContributions
        {
            Pages =
            {
                new PluginPageContribution
                {
                    Id = "dashboard",
                    Title = "WebView2 仪表盘",
                    Icon = "Code",
                    Kind = PluginUiPageKinds.WebView2,
                    Entry = "web/index.html",
                    KeepAlive = true,
                    FillWindow = true
                }
            }
        }
    };

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.Info("WebView2 示例：首次调用命令，宿主已按需启动并完成初始化");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class DashboardCommand : IPluginCommand
{
    public PluginCommandDescriptor Descriptor { get; } = new()
    {
        Name = "dashboard",
        DisplayName = "读取仪表盘数据"
    };

    public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default)
    {
        var name = ReadString(context.Arguments, "name") ?? "Helldiver";
        var data = JsonSerializer.SerializeToElement(new
        {
            title = "WebView2 桥接正常",
            greeting = $"你好，{name}！",
            pluginId = "com.hd2mimnt.samples.webui",
            items = new[]
            {
                new { label = "页面宿主", value = "CoreWebView2 + Avalonia NativeControlHost" },
                new { label = "命令通道", value = "window.hd2.invoke → IPC → IPluginCommand" },
                new { label = "网络策略", value = "仅插件目录内的同源资源" }
            },
            timestamp = DateTimeOffset.UtcNow
        });
        return Task.FromResult(PluginResult.Ok("数据已从插件进程返回。", ui: null, data: data));
    }

    private static string? ReadString(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
