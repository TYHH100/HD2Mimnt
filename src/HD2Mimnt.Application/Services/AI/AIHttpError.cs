using System.Net;
using System.Text.Json;

namespace HD2Mimnt.Application.Services.AI;

/// <summary>
/// Turns provider HTTP errors into actionable messages. All OpenAI-compatible
/// services (including DeepSeek) return {"error":{"message":...}} on failure.
/// </summary>
internal static class AIHttpError
{
    public static string Format(string provider, HttpStatusCode status, string? body)
    {
        var hint = (int)status switch
        {
            400 => "请求格式错误",
            401 => "API Key 无效或缺失",
            402 => "账户余额不足，请充值后重试",
            403 => "没有访问权限",
            404 => "接口地址不存在，请检查 API 端点",
            408 => "请求超时，请稍后重试",
            422 => "请求参数无效",
            429 => "请求过于频繁，已触发速率限制，请稍后重试",
            500 => "服务端内部错误，请稍后重试",
            502 => "网关错误，请稍后重试",
            503 => "服务端繁忙，请稍后重试",
            529 => "服务端繁忙，请稍后重试",
            _ => "请求失败"
        };

        var detail = ExtractDetail(body);
        var message = $"{provider}：{hint}（HTTP {(int)status}）";
        return string.IsNullOrWhiteSpace(detail) ? message : $"{message}：{detail}";
    }

    private static string ExtractDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return Truncate(error.GetString());
                }

                if (error.ValueKind == JsonValueKind.Object)
                {
                    var parts = new List<string>();
                    if (error.TryGetProperty("message", out var message)
                        && message.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(message.GetString() ?? string.Empty);
                    }

                    if (error.TryGetProperty("code", out var code)
                        && code.ValueKind == JsonValueKind.String
                        && parts.Count == 0)
                    {
                        parts.Add(code.GetString() ?? string.Empty);
                    }

                    if (parts.Count > 0)
                    {
                        return Truncate(string.Join(" / ", parts));
                    }
                }
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("message", out var topMessage)
                && topMessage.ValueKind == JsonValueKind.String)
            {
                return Truncate(topMessage.GetString());
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through and show the raw (truncated) body.
        }

        return Truncate(body);
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}

/// <summary>URL helpers for OpenAI-compatible endpoints.</summary>
internal static class OpenAICompatibleUrl
{
    private const string ChatCompletionsPath = "/chat/completions";

    /// <summary>Derives the API root from a Chat Completions endpoint.</summary>
    public static Uri GetApiRoot(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return new Uri(endpoint);
        }

        var path = uri.AbsolutePath;
        var markerIndex = path.LastIndexOf(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            path = path[..markerIndex];
        }

        return new Uri($"{uri.Scheme}://{uri.Authority}{path.TrimEnd('/')}");
    }

    public static string Combine(Uri root, string relative)
        => root.ToString().TrimEnd('/') + "/" + relative.TrimStart('/');

    /// <summary>
    /// DeepSeek's balance API lives at the service root (/user/balance), not under /v1.
    /// </summary>
    public static Uri GetDeepSeekBalanceUri(string endpoint)
    {
        var root = GetApiRoot(endpoint);
        var path = root.AbsolutePath;
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }

        return new Uri($"{root.Scheme}://{root.Authority}{path.TrimEnd('/')}/user/balance");
    }
}
