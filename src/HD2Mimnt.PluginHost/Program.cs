using HD2Mimnt.PluginHost.IPC;
using HD2Mimnt.PluginSDK;
using System.Text.Json;

namespace HD2Mimnt.PluginHost;

/// <summary>
/// Plugin host process entry point
/// Executes plugins in isolated process
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        var pluginId = string.Empty;
        var pipeName = string.Empty;

        // Parse command line arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--plugin-id" && i + 1 < args.Length)
            {
                pluginId = args[i + 1];
            }
            else if (args[i] == "--pipe-name" && i + 1 < args.Length)
            {
                pipeName = args[i + 1];
            }
        }

        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrEmpty(pipeName))
        {
            Console.Error.WriteLine("Usage: HD2Mimnt.PluginHost.exe --plugin-id <id> --pipe-name <name>");
            return 1;
        }

        PluginHostPipeClient? pipeClient = null;
        IPluginRuntime? runtime = null;

        try
        {
            // Connect to main application via IPC
            pipeClient = new PluginHostPipeClient(pipeName);
            await pipeClient.ConnectAsync(timeoutMilliseconds: 10000);

            // Create logger first: 运行时加载阶段（含脚本解释）就要能回传日志
            var logger = new PluginHostLogger((level, message, exception) =>
            {
                var logMsg = new PluginLogMessage
                {
                    Type = nameof(PluginLogMessage),
                    PluginId = pluginId,
                    Level = level,
                    Message = message,
                    Exception = exception?.ToString()
                };
                pipeClient.SendMessageAsync(logMsg).Wait();
            });

            // Wait for LoadPlugin command
            var loadCommand = await pipeClient.ReceiveMessageAsync<LoadPluginCommand>();
            if (loadCommand == null)
            {
                throw new InvalidOperationException("Failed to receive LoadPlugin command");
            }

            // 按 manifest 的 kind 选择运行时：程序集插件或 Lua 脚本插件
            runtime = await PluginRuntimeFactory.CreateAsync(pluginId, loadCommand, logger);

            // Create plugin context
            var storage = new PluginHostStorage(pluginId);
            var modAccess = new PluginHostModAccess();
            var configuration = new PluginHostConfiguration(pluginId);
            var ai = new PluginHostAiClient(pipeClient, pluginId);
            var context = new PluginHostContext(logger, storage, modAccess, configuration, ai);

            // Initialize plugin
            await runtime.InitializeAsync(context);

            // 声明与实现一致性：manifest 声明但没有实现的命令在这里暴露，不阻断加载
            foreach (var diagnostic in runtime.Diagnostics)
            {
                logger.Warning(diagnostic);
            }

            // Send ready response
            await pipeClient.SendMessageAsync(new PluginCompletedResponse
            {
                Type = nameof(PluginCompletedResponse),
                PluginId = pluginId,
                Success = true,
                ResultJson = "Plugin loaded and initialized"
            });

            // Wait for commands; 协议以 Type 字符串分发，抽象基类不能直接反序列化
            while (pipeClient.IsConnected)
            {
                var json = await pipeClient.ReceiveRawAsync();
                if (json is null) break;

                if (TryMessageType(json) == nameof(ExecutePluginCommand) &&
                    JsonSerializer.Deserialize<ExecutePluginCommand>(json) is { } executeCmd)
                {
                    await HandleExecuteCommandAsync(runtime, context, executeCmd, pipeClient, pluginId);

                    // 脚本超时后无法安全中断 Lua 状态机：回执已发出，这里回收整个进程
                    if (runtime.HasRunawayExecution)
                    {
                        logger.Error("检测到失控执行，插件宿主进程即将退出");
                        return 70;
                    }
                }
            }

            // Shutdown plugin
            await runtime.ShutdownAsync();

            return 0; // Success
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Plugin host failed: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);

            if (pipeClient != null)
            {
                try
                {
                    await pipeClient.SendMessageAsync(new PluginErrorResponse
                    {
                        Type = nameof(PluginErrorResponse),
                        PluginId = pluginId,
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace
                    });
                }
                catch
                {
                    // Failed to send error, pipe may be disconnected
                }
            }

            return 1; // Failure
        }
        finally
        {
            runtime?.Dispose();
            pipeClient?.Dispose();
        }
    }

    static async Task HandleExecuteCommandAsync(
        IPluginRuntime runtime,
        PluginHostContext context,
        ExecutePluginCommand command,
        PluginHostPipeClient pipeClient,
        string pluginId)
    {
        try
        {
            if (!runtime.Commands.TryGetValue(command.MethodName, out var handler))
            {
                await pipeClient.SendMessageAsync(new PluginCompletedResponse
                {
                    Type = nameof(PluginCompletedResponse),
                    PluginId = pluginId,
                    Success = false,
                    Errors = new List<string> { $"插件未提供命令 '{command.MethodName}'" }
                });
                return;
            }

            var commandContext = new PluginCommandContext(
                context.Logger,
                context.Storage,
                context.Configuration,
                command.ArgumentsJson,
                context.AI);

            var result = await handler.ExecuteAsync(commandContext, CancellationToken.None);

            await pipeClient.SendMessageAsync(new PluginCompletedResponse
            {
                Type = nameof(PluginCompletedResponse),
                PluginId = pluginId,
                Success = result.Success,
                ResultJson = result.Success ? result.Message : null,
                UiJson = result.Ui is null
                    ? null
                    : JsonSerializer.Serialize(result.Ui, PluginManifestJson.Options),
                Errors = result.Success ? null : new List<string> { result.Error ?? "命令执行失败" }
            });
        }
        catch (Exception ex)
        {
            await pipeClient.SendMessageAsync(new PluginErrorResponse
            {
                Type = nameof(PluginErrorResponse),
                PluginId = pluginId,
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace
            });
        }
    }

    /// <summary>从原始 JSON 中读取消息 Type 判别字段。</summary>
    static string? TryMessageType(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Type", out var type) ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
