using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Plugin host message for IPC communication
/// </summary>
public abstract class PluginHostMessage
{
    public string Type { get; set; } = string.Empty;

    /// <summary>插件标识，取自 manifest.Id（与 PluginHost 侧协议保持一致）。</summary>
    public string PluginId { get; set; } = string.Empty;
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

    /// <summary>声明式界面更新 JSON（与宿主进程侧字段名保持一致，靠 JSON 隐式匹配）。</summary>
    public string? UiJson { get; set; }

    public List<string>? Errors { get; set; }
}

public class PluginErrorResponse : PluginHostMessage
{
    public string ErrorMessage { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
}

public class PluginLogMessage : PluginHostMessage
{
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
}

public class PluginAiRequest : PluginHostMessage
{
    public string RequestId { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public int? MaxOutputTokens { get; set; }
}

public class PluginAiResponse : PluginHostMessage
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int TokensUsed { get; set; }
}

/// <summary>
/// Named pipe server for plugin host IPC
/// </summary>
internal class PluginHostPipeServer : IDisposable
{
    private readonly NamedPipeServerStream _pipeServer;

    // 帧为“长度 + 载荷”两次写入：UI 线程发命令、监听线程回 AI 响应可能并发，
    // 必须串行化，否则会交错破坏协议。
    private readonly SemaphoreSlim _sendGate = new(1, 1);

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
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await PipeFrame.SendAsync(_pipeServer, JsonSerializer.Serialize(message), cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>读取一帧原始 JSON；返回 null 表示对端关闭。</summary>
    public Task<string?> ReceiveRawAsync(CancellationToken cancellationToken = default)
        => PipeFrame.ReceiveAsync(_pipeServer, cancellationToken);

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

    /// <summary>安全违规：给出明确原因，避免上层只看到“进程崩溃”。</summary>
    public void SetSecurityViolation(string reason)
    {
        IsRunning = false;
        _currentCommand.TrySetResult(new PluginOperationResult { Success = false, Error = reason });
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
    private readonly ILogger<PluginHostManager>? _logger;
    private readonly IPluginAiBroker? _aiBroker;
    private readonly IPluginSecurityReporter? _securityReporter;
    private readonly IPluginSecretGuard _secretGuard;

    public PluginHostManager(
        StorageSettingsService? storage = null,
        ILogger<PluginHostManager>? logger = null,
        IPluginAiBroker? aiBroker = null,
        IPluginSecurityReporter? securityReporter = null,
        IPluginSecretGuard? secretGuard = null)
    {
        _storage = storage;
        _logger = logger;
        _aiBroker = aiBroker;
        _securityReporter = securityReporter;
        _secretGuard = secretGuard ?? new PluginSecretGuard();
    }

    private readonly ConcurrentDictionary<string, PluginHostProcess> _runningHosts = new();

    public async Task<PluginOperationResult> StartPluginAsync(string pluginId, string assemblyPath, string manifestPath, CancellationToken cancellationToken = default)
    {
        // 标识校验前置：进程启动后才校验只会留下无法清理的孤儿
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return PluginOperationResult.ErrorResult("Invalid plugin id", pluginId);
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
        processStartInfo.ArgumentList.Add(pluginId);
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
                PluginId = pluginId,
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
            PluginId = hostProcess.PluginId,
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
                    {
                        var (message, messageViolation) = Sanitize(completed.ResultJson);
                        if (messageViolation is not null)
                        {
                            await HandleViolationAsync(hostProcess, $"命令结果包含真实密钥：{messageViolation}", cancellationToken);
                            return;
                        }

                        var (errors, errorsViolation) = Sanitize(completed.Errors is null
                            ? null
                            : string.Join("; ", completed.Errors));
                        if (errorsViolation is not null)
                        {
                            await HandleViolationAsync(hostProcess, $"命令错误信息包含真实密钥：{errorsViolation}", cancellationToken);
                            return;
                        }

                        // 声明式界面同属插件输出，同样要过密钥扫描：一旦回传内容含真实密钥立即终止
                        var (uiJson, uiViolation) = Sanitize(completed.UiJson);
                        if (uiViolation is not null)
                        {
                            await HandleViolationAsync(hostProcess, $"界面内容包含真实密钥：{uiViolation}", cancellationToken);
                            return;
                        }

                        hostProcess.SetCompleted(new PluginOperationResult
                        {
                            Success = completed.Success,
                            Message = message,
                            Error = string.IsNullOrEmpty(errors) ? null : errors,
                            UiJson = uiJson
                        });
                        break;
                    }

                    case PluginErrorResponse error:
                    {
                        var (text, violation) = Sanitize($"{error.ErrorMessage}\n{error.StackTrace}");
                        if (violation is not null)
                        {
                            await HandleViolationAsync(hostProcess, $"错误信息包含真实密钥：{violation}", cancellationToken);
                            return;
                        }

                        hostProcess.SetError(text ?? "插件执行失败");
                        break;
                    }

                    case PluginLogMessage log:
                    {
                        var (message, violation) = Sanitize(log.Message);
                        if (violation is not null)
                        {
                            await HandleViolationAsync(hostProcess, $"日志包含真实密钥：{violation}", cancellationToken);
                            return;
                        }

                        var (exception, exceptionViolation) = Sanitize(log.Exception);
                        if (exceptionViolation is not null)
                        {
                            await HandleViolationAsync(hostProcess, $"异常信息包含真实密钥：{exceptionViolation}", cancellationToken);
                            return;
                        }

                        _logger?.Log(
                            ParseLogLevel(log.Level),
                            "插件 {PluginId} [{Level}] {Message}{Exception}",
                            hostProcess.PluginId,
                            log.Level,
                            message,
                            string.IsNullOrEmpty(exception) ? string.Empty : Environment.NewLine + exception);
                        break;
                    }

                    case PluginAiRequest aiRequest:
                    {
                        if (_aiBroker is null)
                        {
                            await hostProcess.PipeServer.SendMessageAsync(new PluginAiResponse
                            {
                                Type = nameof(PluginAiResponse),
                                PluginId = hostProcess.PluginId,
                                RequestId = aiRequest.RequestId,
                                Success = false,
                                Error = "AI 能力未启用"
                            }, cancellationToken);
                            break;
                        }

                        var outcome = await _aiBroker.HandleAsync(aiRequest, hostProcess.PluginId, cancellationToken);
                        if (outcome.Fatal)
                        {
                            await HandleViolationAsync(
                                hostProcess,
                                outcome.FatalReason ?? "插件 AI 请求触发安全策略",
                                cancellationToken);
                            return;
                        }

                        await hostProcess.PipeServer.SendMessageAsync(new PluginAiResponse
                        {
                            Type = nameof(PluginAiResponse),
                            PluginId = hostProcess.PluginId,
                            RequestId = aiRequest.RequestId,
                            Success = outcome.Success,
                            Content = outcome.Content,
                            Error = outcome.Error,
                            TokensUsed = outcome.TokensUsed
                        }, cancellationToken);
                        break;
                    }
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

    private static LogLevel ParseLogLevel(string level) => level switch
    {
        "Debug" => LogLevel.Debug,
        "Warning" => LogLevel.Warning,
        "Error" => LogLevel.Error,
        _ => LogLevel.Information,
    };

    /// <summary>
    /// 插件可控文本的统一出口：命中真实密钥返回违规原因（调用方负责终止），
    /// 命中凭据格式则脱敏后继续，避免把可疑内容原样落库或写日志。
    /// </summary>
    private (string? Text, string? Violation) Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text, null);
        }

        var scan = _secretGuard.Scan(text);
        if (scan.IsFatal)
        {
            return (null, scan.Reason);
        }

        if (scan.IsViolation)
        {
            _logger?.LogWarning("插件输出包含疑似凭据，已脱敏：{Reason}", scan.Reason);
        }

        return (_secretGuard.Redact(text), null);
    }

    /// <summary>
    /// 立即掐断：给出明确结果 → 停用插件并记录安全事件 → 强杀宿主进程。
    /// 顺序不可颠倒，否则停用过程中的重试可能重新拉起宿主。
    /// </summary>
    private async Task HandleViolationAsync(
        PluginHostProcess hostProcess,
        string reason,
        CancellationToken cancellationToken)
    {
        _logger?.LogCritical(
            "插件 {PluginId} 触发密钥安全策略，立即终止：{Reason}",
            hostProcess.PluginId,
            reason);

        hostProcess.SetSecurityViolation(reason);

        if (_securityReporter is not null)
        {
            try
            {
                await _securityReporter.ReportViolationAsync(hostProcess.PluginId, reason, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "记录插件 {PluginId} 安全事件失败", hostProcess.PluginId);
            }
        }

        ForceTerminate(hostProcess.PluginId);
    }

    /// <summary>协议以 Type 字符串区分消息类型；抽象基类无法直接反序列化。</summary>
    private PluginHostMessage? ParseMessage(string json)
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
                    nameof(PluginLogMessage) => JsonSerializer.Deserialize<PluginLogMessage>(json),
                    nameof(PluginAiRequest) => JsonSerializer.Deserialize<PluginAiRequest>(json),
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
