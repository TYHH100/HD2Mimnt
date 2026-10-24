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
        Guid pluginId = Guid.Empty;
        string pipeName = string.Empty;

        // Parse command line arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--plugin-id" && i + 1 < args.Length)
            {
                Guid.TryParse(args[i + 1], out pluginId);
            }
            else if (args[i] == "--pipe-name" && i + 1 < args.Length)
            {
                pipeName = args[i + 1];
            }
        }

        if (pluginId == Guid.Empty || string.IsNullOrEmpty(pipeName))
        {
            Console.Error.WriteLine("Usage: HD2Mimnt.PluginHost.exe --plugin-id <guid> --pipe-name <name>");
            return 1;
        }

        PluginHostPipeClient? pipeClient = null;
        PluginLoader? pluginLoader = null;

        try
        {
            // Connect to main application via IPC
            pipeClient = new PluginHostPipeClient(pipeName);
            await pipeClient.ConnectAsync(timeoutMilliseconds: 10000);

            // Create plugin loader
            pluginLoader = new PluginLoader(pluginId.ToString());

            // Wait for LoadPlugin command
            var loadCommand = await pipeClient.ReceiveMessageAsync<LoadPluginCommand>();
            if (loadCommand == null)
            {
                throw new InvalidOperationException("Failed to receive LoadPlugin command");
            }

            // Load plugin assembly
            var plugin = await pluginLoader.LoadPluginAsync(
                loadCommand.PluginPath,
                loadCommand.ManifestPath);

            // Create plugin context
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

            var storage = new PluginHostStorage(pluginId.ToString());
            var modAccess = new PluginHostModAccess();
            var configuration = new PluginHostConfiguration(pluginId.ToString());
            var context = new PluginHostContext(logger, storage, modAccess, configuration);

            // Initialize plugin
            await plugin.InitializeAsync(context);

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
                    await HandleExecuteCommandAsync(plugin, executeCmd, pipeClient, pluginId);
                }
            }

            // Shutdown plugin
            await plugin.ShutdownAsync();

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
            pluginLoader?.Dispose();
            pipeClient?.Dispose();
        }
    }

    static async Task HandleExecuteCommandAsync(
        IPlugin plugin,
        ExecutePluginCommand command,
        PluginHostPipeClient pipeClient,
        Guid pluginId)
    {
        try
        {
            // TODO: Use reflection to invoke plugin methods dynamically
            // For now, just send completed response

            await pipeClient.SendMessageAsync(new PluginCompletedResponse
            {
                Type = nameof(PluginCompletedResponse),
                PluginId = pluginId,
                Success = true,
                ResultJson = $"Executed {command.MethodName}"
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
