using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.UI.Controls;

/// <summary>WebView2 页面与插件命令之间的受限 JSON 消息桥。</summary>
internal sealed class PluginWebUiMessageBridge
{
    internal const int MaxMessageBytes = 256 * 1024;
    private const int MaxCommandLength = 128;

    private readonly string _hostName;
    private readonly string _pluginId;
    private readonly string _surfaceId;
    private readonly Action<string> _post;
    private readonly Func<string, IReadOnlyDictionary<string, JsonElement>, Task<PluginOperationResult>> _execute;
    private readonly ConcurrentDictionary<long, byte> _pending = new();

    public PluginWebUiMessageBridge(
        string hostName,
        string pluginId,
        string surfaceId,
        Action<string> post,
        Func<string, IReadOnlyDictionary<string, JsonElement>, Task<PluginOperationResult>> execute)
    {
        _hostName = hostName;
        _pluginId = pluginId;
        _surfaceId = surfaceId;
        _post = post;
        _execute = execute;
    }

    public string HostName => _hostName;

    public static string CreateHostName(string pluginId, string surfaceId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{pluginId}\n{surfaceId}")));
        return $"p{hash[..24].ToLowerInvariant()}.hd2mimnt.invalid";
    }

    public static bool IsAllowedOrigin(string? source, string hostName)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && string.Equals(uri.Host, hostName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedResource(Uri uri, string hostName)
        => IsAllowedOrigin(uri.AbsoluteUri, hostName)
           || uri.Scheme is "data" or "blob" or "about";

    public void HandleMessage(string? source, string? message)
    {
        if (!IsAllowedOrigin(source, _hostName)
            || string.IsNullOrEmpty(message)
            || Encoding.UTF8.GetByteCount(message) > MaxMessageBytes)
        {
            return;
        }

        long requestId;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "hd2:invoke", StringComparison.Ordinal))
            {
                return;
            }

            if (!root.TryGetProperty("requestId", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt64(out requestId)
                || requestId < 0)
            {
                return;
            }

            if (!root.TryGetProperty("command", out var commandElement)
                || commandElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(commandElement.GetString())
                || commandElement.GetString()!.Length > MaxCommandLength)
            {
                CompleteError(requestId, "Invalid command.");
                return;
            }

            if (!_pending.TryAdd(requestId, 0))
            {
                CompleteError(requestId, "Duplicate requestId.");
                return;
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("arguments", out var arguments)
                && arguments.ValueKind != JsonValueKind.Null)
            {
                if (arguments.ValueKind != JsonValueKind.Object)
                {
                    _pending.TryRemove(requestId, out _);
                    CompleteError(requestId, "arguments must be a JSON object.");
                    return;
                }

                foreach (var property in arguments.EnumerateObject())
                {
                    values[property.Name] = property.Value.Clone();
                }
            }

            _ = ExecuteAsync(requestId, commandElement.GetString()!, values);
        }
        catch (JsonException)
        {
            // 无法解析的报文没有可信 requestId，直接忽略。
        }
    }

    public void CancelPending()
    {
        foreach (var requestId in _pending.Keys)
        {
            _pending.TryRemove(requestId, out _);
            CompleteError(requestId, "The page navigated away.");
        }
    }

    private async Task ExecuteAsync(
        long requestId,
        string command,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        try
        {
            var result = await _execute(command, values);
            if (!_pending.TryRemove(requestId, out _)) return;

            var response = new JsonObject
            {
                ["type"] = "hd2:result",
                ["requestId"] = requestId,
                ["ok"] = result.Success,
                ["message"] = result.Message,
                ["error"] = result.Error
            };
            if (!string.IsNullOrWhiteSpace(result.DataJson))
            {
                response["data"] = JsonNode.Parse(result.DataJson);
            }
            _post(response.ToJsonString());
        }
        catch (Exception ex)
        {
            if (_pending.TryRemove(requestId, out _))
            {
                CompleteError(requestId, ex.Message);
            }
        }
    }

    private void CompleteError(long requestId, string error)
    {
        _post(new JsonObject
        {
            ["type"] = "hd2:result",
            ["requestId"] = requestId,
            ["ok"] = false,
            ["error"] = error
        }.ToJsonString());
    }

    internal const string BridgeScript = """
        (() => {
          if (window.hd2 || !window.chrome?.webview) return;
          const pending = new Map();
          let sequence = 0;
          window.chrome.webview.addEventListener('message', event => {
            if (!event.data || event.data.type !== 'hd2:result') return;
            const requestId = event.data.requestId;
            const entry = pending.get(requestId);
            if (!entry) return;
            pending.delete(requestId);
            if (event.data.ok) entry.resolve({ message: event.data.message ?? null, data: event.data.data ?? null });
            else entry.reject(new Error(event.data.error || 'Plugin command failed.'));
          });
          Object.defineProperty(window, 'hd2', {
            configurable: false,
            enumerable: true,
            value: Object.freeze({
              invoke(command, args = {}) {
                return new Promise((resolve, reject) => {
                  const requestId = ++sequence;
                  pending.set(requestId, { resolve, reject });
                  try {
                    window.chrome.webview.postMessage({ type: 'hd2:invoke', requestId, command, arguments: args });
                  } catch (error) {
                    pending.delete(requestId);
                    reject(error);
                  }
                });
              }
            })
          });
        })();
        """;
}
