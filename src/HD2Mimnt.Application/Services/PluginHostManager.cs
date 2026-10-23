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
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private readonly NamedPipeServerStream _pipeServer;

    public PluginHostPipeServer(string pipeName)
    {
        _pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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

    /// <summary>读取一帧原始 JSON；返回 null 表示对端关闭。</summary>
    public async Task<string?> ReceiveRawAsync(CancellationToken cancellationToken = default)
    {
        var lengthBytes = new byte[4];
        if (!await ReadExactlyAsync(lengthBytes, cancellationToken)) return null;

        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length is <= 0 or > MaxMessageBytes)
        {
            // 长度非法：视为协议破坏，抛出让监听器按宿主崩溃处理
            throw new IOException($"Invalid IPC message length: {length}");
        }

        var messageBytes = new byte[length];
        if (!await ReadExactlyAsync(messageBytes, cancellationToken)) return null;
        return Encoding.UTF8.GetString(messageBytes);
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _pipeServer.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) return false; // 对端关闭
            offset += read;
        }
        return true;
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

    /// <summary>监听循环的取消源，与宿主同生命周期，不随启动调用方令牌取消。</summary>
    public CancellationTokenSource ListenerCts { get; } = new();

    private TaskCompletionSource<PluginOperationResult> _currentCommand =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        // 旧等待者立即取消，避免被替换后永久挂起
        _currentCommand.TrySetCanceled();
        _currentCommand = new TaskCompletionSource<PluginOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Manages plugin host processes
/// </summary>
public class PluginHostManager : IPluginHostManager
{
    private readonly StorageSettingsService? _storage;
    public PluginHostManager(StorageSettingsService? storage = null) => _storage = storage;
    private readonly ConcurrentDictionary<string, PluginHostProcess> _runningHosts = new();

    public async Task<PluginOperationResult> StartPluginAsync(string pluginId, string assemblyPath, string manifestPath, CancellationToken cancellationToken = default)
    {
        // Guid 校验前置：进程启动后才校验只会留下无法清理的孤儿
        if (!Guid.TryParse(pluginId, out var pluginGuid))
        {
            return PluginOperationResult.ErrorResult("Invalid plugin id format", pluginId);
        }
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
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processStartInfo.ArgumentList.Add("--plugin-id");
        processStartInfo.ArgumentList.Add(pluginGuid.ToString());
        processStartInfo.ArgumentList.Add("--pipe-name");
        processStartInfo.ArgumentList.Add(pipeName);

        if (_storage != null) processStartInfo.Environment["HD2MIMNT_RUNTIME_DATA_PATH"] = _storage.Current.AppDataPath;

        Process? process = null;
        PluginHostProcess? hostProcess = null;
        try
        {
            process = Process.Start(processStartInfo);
            if (process == null)
            {
                pipeServer.Dispose();
                return PluginOperationResult.ErrorResult("Failed to start plugin host process");
            }

            // 启动握手（连接 + 加载响应）整体受 10 秒超时约束
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            await pipeServer.WaitForConnectionAsync(linkedCts.Token);

            hostProcess = new PluginHostProcess(hostId, pluginId, process, pipeServer);
            if (!_runningHosts.TryAdd(pluginId, hostProcess))
            {
                await TerminateHostAsync(hostProcess, cancellationToken);
                return PluginOperationResult.ErrorResult("Plugin already running", pluginId);
            }

            // 监听器独占读取管道；初始加载结果同样由它投递
            _ = Task.Run(() => ListenForMessagesAsync(hostProcess, hostProcess.ListenerCts.Token));

            // Send load plugin command
            var loadCommand = new LoadPluginCommand
            {
                Type = nameof(LoadPluginCommand),
                PluginId = pluginGuid,
                PluginPath = assemblyPath,
                ManifestPath = manifestPath
            };
            await pipeServer.SendMessageAsync(loadCommand, linkedCts.Token);

            var response = await hostProcess.WaitForResultAsync().WaitAsync(linkedCts.Token);
            if (!response.Success)
            {
                await TerminateHostAsync(hostProcess, cancellationToken);
                return PluginOperationResult.ErrorResult($"Failed to load plugin: {response.Error}");
            }

            return PluginOperationResult.SuccessResult("Plugin started successfully", hostId);
        }
        catch (OperationCanceledException)
        {
            if (hostProcess != null) await TerminateHostAsync(hostProcess, CancellationToken.None);
            else await CleanupProcessAsync(process, pipeServer);
            return PluginOperationResult.ErrorResult(cancellationToken.IsCancellationRequested
                ? "Plugin start was cancelled"
                : "Plugin host failed to connect within timeout");
        }
        catch (Exception ex)
        {
            if (hostProcess != null) await TerminateHostAsync(hostProcess, CancellationToken.None);
            else await CleanupProcessAsync(process, pipeServer);
            return PluginOperationResult.ErrorResult($"Failed to start plugin host: {ex.Message}");
        }
    }

    private static Task CleanupProcessAsync(Process? process, PluginHostPipeServer pipeServer)
    {
        try { pipeServer.Dispose(); } catch { }
        if (process is { HasExited: false })
        {
            try { process.Kill(); } catch { }
        }
        return Task.CompletedTask;
    }

    /// <summary>停止宿主进程：条件移除字典项、唤醒等待者、关闭管道、等待退出（超时强杀）。</summary>
    private async Task TerminateHostAsync(PluginHostProcess hostProcess, CancellationToken cancellationToken)
    {
        // 条件移除：条目已被监听器清理或从未注册时不动其它实例
        _runningHosts.TryRemove(new KeyValuePair<string, PluginHostProcess>(hostProcess.PluginId, hostProcess));
        hostProcess.SetCrashed();
        try { hostProcess.PipeServer.Dispose(); } catch { }
        try
        {
            if (!hostProcess.Process.HasExited)
            {
                using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitCts.CancelAfter(TimeSpan.FromSeconds(5));
                try { await hostProcess.Process.WaitForExitAsync(exitCts.Token); }
                catch (OperationCanceledException) { try { hostProcess.Process.Kill(); } catch { } }
            }
        }
        catch { /* 进程可能已自行退出 */ }
        hostProcess.ListenerCts.Cancel();
    }

    public async Task StopPluginAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (_runningHosts.TryRemove(pluginId, out var hostProcess))
        {
            await TerminateHostAsync(hostProcess, cancellationToken);
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
            Type = nameof(ExecutePluginCommand),
            PluginId = Guid.Parse(hostProcess.PluginId),
            MethodName = methodName,
            ArgumentsJson = argumentsJson
        };

        await hostProcess.PipeServer.SendMessageAsync(command, cancellationToken);

        return await hostProcess.WaitForResultAsync().WaitAsync(cancellationToken);
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
                hostProcess.SetCrashed();
                hostProcess.PipeServer.Dispose();
                hostProcess.Process.Kill();
            }
            catch
            {
                // Ignore cleanup errors
            }
            hostProcess.ListenerCts.Cancel();
        }
    }

    private async Task ListenForMessagesAsync(PluginHostProcess hostProcess, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var json = await hostProcess.PipeServer.ReceiveRawAsync(cancellationToken);
                if (json is null) break; // 管道已关闭

                switch (ParseMessage(json))
                {
                    case PluginCompletedResponse completed:
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
                        break;

                    case PluginErrorResponse error:
                        hostProcess.SetError($"{error.ErrorMessage}\n{error.StackTrace}");
                        break;

                    // PluginLogMessage 与未知类型暂无消费方，忽略
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // 正常停止路径由 TerminateHostAsync 负责清理
        }
        catch
        {
            // 管道断开或读取失败：宿主异常退出，落到底部统一清理
        }

        // 宿主异常退出：条件移除后清理进程与管道，避免残留
        if (_runningHosts.TryRemove(new KeyValuePair<string, PluginHostProcess>(hostProcess.PluginId, hostProcess)))
        {
            hostProcess.SetCrashed();
            try { hostProcess.PipeServer.Dispose(); } catch { }
            try { if (!hostProcess.Process.HasExited) hostProcess.Process.Kill(); } catch { }
            hostProcess.ListenerCts.Cancel();
        }
    }

    /// <summary>协议以 Type 字符串区分消息类型；抽象基类无法直接反序列化。</summary>
    private static PluginHostMessage? ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Type", out var type) &&
                type.GetString() is { Length: > 0 } typeName)
            {
                return typeName switch
                {
                    nameof(PluginCompletedResponse) => JsonSerializer.Deserialize<PluginCompletedResponse>(json),
                    nameof(PluginErrorResponse) => JsonSerializer.Deserialize<PluginErrorResponse>(json),
                    _ => null,
                };
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
