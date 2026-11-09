using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HD2Mimnt.Application.Services.AI;

/// <summary>
/// OpenAI provider implementation
/// </summary>
public class OpenAIProvider : IAIProvider, IAIProviderProbe
{
    private const string ProviderName = "OpenAI";

    private readonly AIProviderConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAIProvider> _logger;

    public OpenAIProvider(AIProviderConfig config, ILogger<OpenAIProvider> logger)
    {
        _config = config;
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public AIProviderConfig GetConfiguration() => _config;

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var result = await TestConnectionDetailedAsync(cancellationToken);
        return result.Success;
    }

    public async Task<AIResponse> TestConnectionDetailedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _config.ApiEndpoint);
            request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");

            var payload = new
            {
                model = _config.Model ?? "gpt-3.5-turbo",
                messages = new[]
                {
                    new { role = "user", content = "test" }
                },
                max_tokens = 5
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new AIResponse
                {
                    Success = false,
                    Error = AIHttpError.Format(ProviderName, response.StatusCode, body)
                };
            }

            return new AIResponse { Success = true, Content = "OK" };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI connection test failed");
            return new AIResponse { Success = false, Error = $"连接 OpenAI 服务失败：{ex.Message}" };
        }
    }

    public async Task<AIProviderModelsResult> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var root = OpenAICompatibleUrl.GetApiRoot(_config.ApiEndpoint);
            var request = new HttpRequestMessage(HttpMethod.Get, OpenAICompatibleUrl.Combine(root, "models"));
            if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new AIProviderModelsResult
                {
                    Success = false,
                    Error = AIHttpError.Format(ProviderName, response.StatusCode, body)
                };
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            if (!json.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return new AIProviderModelsResult { Success = false, Error = "模型列表响应格式无法识别。" };
            }

            var models = new List<AIModelInfo>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    models.Add(new AIModelInfo { Id = id.GetString() ?? string.Empty });
                }
            }

            return new AIProviderModelsResult
            {
                Success = true,
                Models = models.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI model listing failed");
            return new AIProviderModelsResult { Success = false, Error = $"获取模型列表失败：{ex.Message}" };
        }
    }

    public async Task<AIResponse> SendRequestAsync(AIAnalysisContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var prompt = AIProviderPrompts.Build(context);

            var request = new HttpRequestMessage(HttpMethod.Post, _config.ApiEndpoint);
            request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");

            var payload = new Dictionary<string, object?>
            {
                ["model"] = _config.Model ?? "gpt-3.5-turbo",
                ["messages"] = new object[]
                {
                    new { role = "system", content = prompt.System },
                    new { role = "user", content = prompt.User }
                },
                ["max_tokens"] = _config.MaxTokens
            };

            // 推理模型不接受温度参数；非推理模型沿用原有温度。
            if (_config.EnableThinking)
            {
                payload["reasoning_effort"] = NormalizeEffort(_config.ThinkingEffort);
            }
            else
            {
                payload["temperature"] = _config.Temperature;
            }

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                return new AIResponse
                {
                    Success = false,
                    Error = AIHttpError.Format(ProviderName, response.StatusCode, errorContent)
                };
            }

            var responseJson = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            if (!responseJson.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var contentProperty)
                || contentProperty.ValueKind != JsonValueKind.String)
            {
                return new AIResponse { Success = false, Error = "AI 返回格式不是 Chat Completions 格式。" };
            }

            var content = contentProperty.GetString() ?? string.Empty;
            // 兼容部分不返回 usage 的 OpenAI 兼容网关。
            var usage = AIUsageParser.ParseOpenAiCompatible(responseJson);
            var elapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            using (_logger.BeginScope(AIUsageParser.LogFields(_config.Model ?? "gpt-3.5-turbo", usage.InputTokens,
                       usage.CachedInputTokens, Math.Max(0, usage.TotalTokens - usage.InputTokens),
                       usage.TotalTokens, elapsedMs)))
            {
                _logger.LogDebug("AI 请求完成");
            }

            return new AIResponse
            {
                Success = true,
                Content = content,
                TokensUsed = usage.TotalTokens,
                InputTokens = usage.InputTokens,
                CachedInputTokens = usage.CachedInputTokens
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenAI request failed");
            return new AIResponse
            {
                Success = false,
                Error = $"Request failed: {ex.Message}"
            };
        }
    }

    private static string NormalizeEffort(string? effort)
    {
        return (effort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "low" or "minimal" => "low",
            "high" or "max" or "xhigh" or "ultra" => "high",
            _ => "medium"
        };
    }
}
