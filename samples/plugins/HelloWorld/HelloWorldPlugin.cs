using System.Text.Json;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.Samples.HelloWorld;

/// <summary>
/// 最小可用插件：一个 IPlugin 入口 + 一个命令贡献。
/// 演示初始化日志、插件私有存储与命令参数读取。
/// </summary>
public sealed class HelloWorldPlugin : IPlugin
{
    private IPluginContext? _context;

    public PluginManifest Manifest { get; } = new()
    {
        Id = "com.hd2mimnt.samples.hello",
        Name = "Hello World 示例",
        Version = "1.0.0",
        Author = "HD2Mimnt",
        Description = "最小可用插件：向管理器贡献一个 hello 命令。",
        ApiVersion = PluginSdkInfo.Version,
        Runtime = "net10.0",
        PermissionMode = PluginPermissionMode.Sandbox,
        Assembly = "HelloWorld.dll",
        EntryType = typeof(HelloWorldPlugin).FullName!,
        Commands =
        {
            new PluginCommandDescriptor
            {
                Name = "hello",
                DisplayName = "打个招呼",
                Description = "读取可选参数 {\"name\":\"...\"}，返回问候语并累计调用次数。"
            }
        }
    };

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        context.Logger.Info("Hello World：首次调用命令，宿主已按需启动并完成初始化");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _context?.Logger.Info("Hello World 已停止");
        return Task.CompletedTask;
    }
}

/// <summary>
/// 命令实现。宿主会扫描程序集内所有 IPluginCommand，并按 Descriptor.Name 暴露。
/// </summary>
public sealed class HelloCommand : IPluginCommand
{
    private const string CounterFile = "hello-count.txt";

    public PluginCommandDescriptor Descriptor { get; } = new()
    {
        Name = "hello",
        DisplayName = "打个招呼",
        Description = "读取可选参数 {\"name\":\"...\"}，返回问候语并累计调用次数。"
    };

    public async Task<PluginResult> ExecuteAsync(
        PluginCommandContext context,
        CancellationToken cancellationToken = default)
    {
        var name = ReadName(context.Arguments) ?? "HD2";

        var count = 0;
        if (context.Storage.FileExists(CounterFile))
        {
            var raw = await context.Storage.ReadFileAsync(CounterFile, cancellationToken);
            _ = int.TryParse(raw.Trim(), out count);
        }

        count++;
        await context.Storage.WriteFileAsync(CounterFile, count.ToString(), cancellationToken);
        context.Logger.Info($"hello 被调用，第 {count} 次");

        return PluginResult.Ok($"你好，{name}！这是第 {count} 次执行。");
    }

    /// <summary>参数是调用方给的原始 JSON，格式不对时退回默认值而不是抛异常。</summary>
    private static string? ReadName(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("name", out var value)
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
