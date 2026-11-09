using System.Text.Json;
using HD2Mimnt.PluginHost.IPC;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.PluginHost;

/// <summary>
/// 插件侧 AI 客户端：把请求原样转给主程序，只接收模型文本。
///
/// 关键约束：
/// - 宿主进程不持有、不请求、不缓存任何 API Key；
/// - 请求在客户端串行化，因为命令执行期间管道由本客户端独占读取，
///   并发调用会导致响应帧被其它调用取走；
/// - 主程序若判定提示词含密钥，会直接终止本进程，此处表现为连接断开。
/// </summary>
public sealed class PluginHostAiClient : IPluginAiClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly PluginHostPipeClient _pipe;
    private readonly string _pluginId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PluginHostAiClient(PluginHostPipeClient pipe, string pluginId)
    {
        _pipe = pipe;
        _pluginId = pluginId;
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_pipe.IsConnected);

    public async Task<PluginAiResult> CompleteAsync(
        string prompt,
        PluginAiOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return PluginAiResult.Fail("提示词不能为空");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var requestId = Guid.NewGuid().ToString("N");

            await _pipe.SendMessageAsync(new PluginAiRequest
            {
                Type = nameof(PluginAiRequest),
                PluginId = _pluginId,
                RequestId = requestId,
                Prompt = prompt,
                SystemPrompt = options?.SystemPrompt,
                MaxOutputTokens = options?.MaxOutputTokens
            }, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            while (true)
            {
                var json = await _pipe.ReceiveRawAsync(timeoutCts.Token);
                if (json is null)
                {
                    return PluginAiResult.Fail("与主程序的连接已断开");
                }

                if (!string.Equals(TryMessageType(json), nameof(PluginAiResponse), StringComparison.Ordinal))
                {
                    continue; // 协议为请求/响应式，执行期间不会插入其它消息；忽略未知帧
                }

                var response = JsonSerializer.Deserialize<PluginAiResponse>(json);
                if (response is null || !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                {
                    continue;
                }

                return response.Success
                    ? PluginAiResult.Ok(response.Content, response.TokensUsed)
                    : PluginAiResult.Fail(response.Error ?? "AI 请求失败");
            }
        }
        catch (OperationCanceledException)
        {
            return PluginAiResult.Fail(cancellationToken.IsCancellationRequested ? "AI 请求已取消" : "AI 请求超时");
        }
        catch (Exception ex)
        {
            return PluginAiResult.Fail($"AI 请求失败：{ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? TryMessageType(string json)
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
