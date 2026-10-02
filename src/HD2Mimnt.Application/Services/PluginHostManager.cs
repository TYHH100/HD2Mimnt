using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Plugin host message for IPC communication
/// </summary>
public abstract class PluginHostMessage
{
    public string Type { get; set; } = string.Empty;
    public Guid PluginId { get; set; }
}

public class LoadPluginCommand : PluginHostMessage
{
    public string PluginPath { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public Dictionary<string, string> Arguments { get; set; } = new();
}

public class ExecutePluginCommand : PluginHostMessage
{
    public string MethodName { get; set; } = string.Empty;
    public string? ArgumentsJson { get; set; }
}

public class PluginCompletedResponse : PluginHostMessage
{
    public bool Success { get; set; }
    public string? ResultJson { get; set; }
    public List<string>? Errors { get; set; }
}

public class PluginErrorResponse : PluginHostMessage
{
    public string ErrorMessage { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
}

/// <summary>
/// Named pipe server for plugin host IPC
/// </summary>
internal class PluginHostPipeServer : IDisposable
{
    private readonly NamedPipeServerStream _pipeServer;

    public PluginHostPipeServer(string pipeName)
    {
        _pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous);
    }

    public async Task WaitForConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _pipeServer.WaitForConnectionAsync(cancellationToken);
    }

    public async Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default) where T : PluginHostMessage
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        var lengthBytes = BitConverter.GetBytes(bytes.Length);

        await _pipeServer.WriteAsync(lengthBytes, 0, 4, cancellationToken);
        await _pipeServer.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        await _pipeServer.FlushAsync(cancellationToken);
    }

    public async Task<T?> ReceiveMessageAsync<T>(CancellationToken cancellationToken = default) where T : PluginHostMessage
    {
        var lengthBytes = new byte[4];
        var bytesRead = await _pipeServer.ReadAsync(lengthBytes, 0, 4, cancellationToken);
        if (bytesRead == 0) return null;

        var length = BitConverter.ToInt32(lengthBytes, 0);
        var messageBytes = new byte[length];
        bytesRead = await _pipeServer.ReadAsync(messageBytes, 0, length, cancellationToken);
        if (bytesRead == 0) return null;

        var json = Encoding.UTF8.GetString(messageBytes);
        return JsonSerializer.Deserialize<T>(json);
    }

    public bool IsConnected => _pipeServer.IsConnected;

    public void Dispose()
    {
        _pipeServer?.Dispose();
    }
}

/// <summary>
/// Represents a running plugin host process
/// </summary>
internal class PluginHostProcess
{
    public Guid HostId { get; }
    public string PluginId { get; }
    public Process Process { get; }
    public PluginHostPipeServer PipeServer { get; }
    public DateTime StartedAt { get; }
    public bool IsRunning { get; private set; } = true;

    private TaskCompletionSource<PluginOperationResult> _currentCommand = new();

    public PluginHostProcess(Guid hostId, string pluginId, Process process, PluginHostPipeServer pipeServer)
    {
        HostId = hostId;
        PluginId = pluginId;
        Process = process;
        PipeServer = pipeServer;
        StartedAt = DateTime.UtcNow;
    }

    public void SetCompleted(PluginOperationResult result)
    {
        _currentCommand.TrySetResult(result);
    }

    public void SetError(string error)
    {
        _currentCommand.TrySetResult(new PluginOperationResult { Success = false, Error = error });
    }

    public void SetCrashed()
    {
        IsRunning = false;
        _currentCommand.TrySetResult(new PluginOperationResult { Success = false, Error = "Plugin host process crashed" });
    }

    public Task<PluginOperationResult> WaitForResultAsync() => _currentCommand.Task;

    public void StartNewCommand()
    {
        _currentCommand = new TaskCompletionSource<PluginOperationResult>();
    }
}

/// <summary>
/// Manages plugin host processes
/// </summary>
public class PluginHostManager : IPluginHostManager
{
    private readonly ConcurrentDictionary<string, PluginHostProcess> _runningHosts = new();

    public async Task<PluginOperationResult> StartPluginAsync(string pluginId, string assemblyPath, string manifestPath, CancellationToken cancellationToken = default)
    {
        if (_runningHosts.ContainsKey(pluginId))
        {
            return PluginOperationResult.ErrorResult("Plugin already running", pluginId);
        }

        var hostId = Guid.NewGuid();
        var pipeName = $"HD2Mimnt.PluginHost.{hostId}";

        // Create named pipe server
        var pipeServer = new PluginHostPipeServer(pipeName);

        // Spawn plugin host process
        var hostExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HD2Mimnt.PluginHost.exe");
        var processStartInfo = new ProcessStartInfo
        {
            FileName = hostExePath,
            Arguments = $"--plugin-id {pluginId} --pipe-name {pipeName}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = Process.Start(processStartInfo);
        if (process == null)
        {
            return PluginOperationResult.ErrorResult("Failed to start plugin host process");
        }

        // Wait for connection with timeout
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            await pipeServer.WaitForConnectionAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            return PluginOperationResult.ErrorResult("Plugin host failed to connect within timeout");
        }

        // Send load plugin command
        var loadCommand = new LoadPluginCommand
        {
            PluginId = Guid.Parse(pluginId),
            PluginPath = assemblyPath,
            ManifestPath = manifestPath
        };

        await pipeServer.SendMessageAsync(loadCommand, cancellationToken);

        // Create host process tracking
        var hostProcess = new PluginHostProcess(hostId, pluginId, process, pipeServer);
        _runningHosts.TryAdd(pluginId, hostProcess);

        // Start listening for messages
        _ = Task.Run(() => ListenForMessagesAsync(hostProcess, cancellationToken), cancellationToken);

        // Wait for initial load response
        var response = await pipeServer.ReceiveMessageAsync<PluginCompletedResponse>(cancellationToken);
        if (response == null || !response.Success)
        {
            return PluginOperationResult.ErrorResult($"Failed to load plugin: {response?.Errors?.FirstOrDefault()}");
        }

        return PluginOperationResult.SuccessResult("Plugin started successfully", hostId);
    }

    public async Task StopPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (_runningHosts.TryRemove(pluginId, out var hostProcess))
        {
            try
            {
                hostProcess.PipeServer.Dispose();
                hostProcess.Process.WaitForExit(5000);

                if (!hostProcess.Process.HasExited)
                {
                    hostProcess.Process.Kill();
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    public async Task<PluginOperationResult> ExecutePluginCommandAsync(string pluginId, string methodName, string? argumentsJson = null, CancellationToken cancellationToken = default)
    {
        if (!_runningHosts.TryGetValue(pluginId, out var hostProcess))
        {
            return PluginOperationResult.ErrorResult("Plugin host not found", pluginId);
        }

        hostProcess.StartNewCommand();

        var command = new ExecutePluginCommand
        {
            PluginId = Guid.Parse(hostProcess.PluginId),
            MethodName = methodName,
            ArgumentsJson = argumentsJson
        };

        await hostProcess.PipeServer.SendMessageAsync(command, cancellationToken);

        return await hostProcess.WaitForResultAsync();
    }

    public IReadOnlyList<PluginHostInfo> GetRunningPlugins()
    {
        return _runningHosts.Values.Select(p => new PluginHostInfo
        {
            HostId = p.HostId,
            PluginId = p.PluginId,
            StartedAt = p.StartedAt,
            IsRunning = p.IsRunning
        }).ToList();
    }

    public void ForceTerminate(string pluginId)
    {
        if (_runningHosts.TryRemove(pluginId, out var hostProcess))
        {
            try
            {
                hostProcess.Process.Kill();
                hostProcess.PipeServer.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private async Task ListenForMessagesAsync(PluginHostProcess hostProcess, CancellationToken cancellationToken)
    {
        try
        {
            while (hostProcess.PipeServer.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var message = await hostProcess.PipeServer.ReceiveMessageAsync<PluginHostMessage>(cancellationToken);
                if (message == null) break;

                if (message is PluginCompletedResponse completed)
                {
                    var result = new PluginOperationResult
                    {
                        Success = completed.Success,
                        Message = completed.ResultJson
                    };

                    if (completed.Errors != null)
                    {
                        result.Error = string.Join("; ", completed.Errors);
                    }

                    hostProcess.SetCompleted(result);
                }
                else if (message is PluginErrorResponse error)
                {
                    hostProcess.SetError($"{error.ErrorMessage}\n{error.StackTrace}");
                }
            }
        }
        catch (IOException)
        {
            // Pipe disconnected - plugin crashed
            hostProcess.SetCrashed();
        }
        catch (Exception)
        {
            // Other errors
            hostProcess.SetCrashed();
        }
    }
}
