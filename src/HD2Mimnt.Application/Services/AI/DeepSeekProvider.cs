using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HD2Mimnt.Application.Services.AI;

/// <summary>
/// DeepSeek provider. Uses the OpenAI-compatible Chat Completions API plus
/// DeepSeek-specific model listing, balance and thinking-mode support.
/// </summary>
public class DeepSeekProvider : IAIProvider, IAIProviderProbe, IAIProviderBalance
{
    private const string ProviderName = "DeepSeek";
    private const string DefaultModel = "deepseek-flash";

    private readonly AIProviderConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DeepSeekProvider> _logger;

    public DeepSeekProvider(AIProviderConfig config, ILogger<DeepSeekProvider> logger)
    {
        _config = config;
        _logger = logger;
        _httpClient = new HttpClient
        {
            // 思考模式下长回答明显更慢，留足时间避免误判为失败。
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    public AIProviderConfig GetConfiguration() => _config;

    private string Model => string.IsNullOrWhiteSpace(_config.Model) ? DefaultModel : _config.Model;

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var result = await TestConnectionDetailedAsync(cancellationToken);
        return result.Success;
    }

    public async Task<AIResponse> TestConnectionDetailedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = CreateRequest(HttpMethod.Post, _config.ApiEndpoint);
            var payload = new Dictionary<string, object?>
            {
                ["model"] = Model,
                ["messages"] = new[] { new { role = "user", content = "ping" } },
                ["max_tokens"] = 1,
                ["thinking"] = new { type = "disabled" }
            };
            request.Content = CreateJsonContent(payload);

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
            _logger.LogError(ex, "DeepSeek connection test failed");
            return new AIResponse { Success = false, Error = $"连接 DeepSeek 失败：{ex.Message}" };
        }
    }

    public async Task<AIResponse> SendRequestAsync(AIAnalysisContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var prompt = AIProviderPrompts.Build(context);

            var request = CreateRequest(HttpMethod.Post, _config.ApiEndpoint);
            var payload = new Dictionary<string, object?>
            {
                ["model"] = Model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = prompt.System },
                    new { role = "user", content = prompt.User }
                },
                ["max_tokens"] = _config.MaxTokens,
                ["thinking"] = new { type = _config.EnableThinking ? "enabled" : "disabled" }
            };

            if (_config.EnableThinking)
            {
                payload["reasoning_effort"] = NormalizeEffort(_config.ThinkingEffort);
            }
            else
            {
                // 思考模式会忽略采样参数；非思考模式保留温度。
                payload["temperature"] = _config.Temperature;
            }

            request.Content = CreateJsonContent(payload);
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
                return new AIResponse { Success = false, Error = "DeepSeek 返回格式不是 Chat Completions 格式。" };
            }

            var usage = AIUsageParser.ParseOpenAiCompatible(responseJson);
            _logger.LogDebug(
                "DeepSeek token usage: input {Input}, cached {Cached}, total {Total}",
                usage.InputTokens, usage.CachedInputTokens, usage.TotalTokens);

            return new AIResponse
            {
                Success = true,
                Content = contentProperty.GetString() ?? string.Empty,
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
            _logger.LogError(ex, "DeepSeek request failed");
            return new AIResponse { Success = false, Error = $"DeepSeek 请求失败：{ex.Message}" };
        }
    }

    public async Task<AIProviderModelsResult> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var root = OpenAICompatibleUrl.GetApiRoot(_config.ApiEndpoint);
            var request = CreateRequest(HttpMethod.Get, OpenAICompatibleUrl.Combine(root, "models"));

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
                return new AIProviderModelsResult { Success = false, Error = "DeepSeek 模型列表响应格式无法识别。" };
            }

            var models = new List<AIModelInfo>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idProperty)
                    || idProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                models.Add(new AIModelInfo
                {
                    Id = idProperty.GetString() ?? string.Empty,
                    DisplayName = ReadString(item, "name"),
                    ContextWindow = ReadInt32(item, "context_window"),
                    MaxOutputTokens = ReadInt32(item, "max_output_tokens"),
                    ThinkingLevels = ReadThinkingLevels(item)
                });
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
            _logger.LogError(ex, "DeepSeek model listing failed");
            return new AIProviderModelsResult { Success = false, Error = $"获取 DeepSeek 模型列表失败：{ex.Message}" };
        }
    }

    public async Task<AIBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = CreateRequest(HttpMethod.Get, OpenAICompatibleUrl.GetDeepSeekBalanceUri(_config.ApiEndpoint).ToString());

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new AIBalanceResult
                {
                    Success = false,
                    Error = AIHttpError.Format(ProviderName, response.StatusCode, body)
                };
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            if (json.ValueKind != JsonValueKind.Object)
            {
                return new AIBalanceResult { Success = false, Error = "DeepSeek 余额响应格式无法识别。" };
            }

            var isAvailable = json.TryGetProperty("is_available", out var available)
                && available.ValueKind == JsonValueKind.True;

            var entries = new List<AIBalanceEntry>();
            if (json.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
            {
                foreach (var info in infos.EnumerateArray())
                {
                    if (info.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    entries.Add(new AIBalanceEntry
                    {
                        Currency = ReadString(info, "currency") ?? string.Empty,
                        TotalBalance = ReadString(info, "total_balance") ?? string.Empty,
                        GrantedBalance = ReadString(info, "granted_balance") ?? string.Empty,
                        ToppedUpBalance = ReadString(info, "topped_up_balance") ?? string.Empty
                    });
                }
            }

            return new AIBalanceResult
            {
                Success = true,
                IsAvailable = isAvailable,
                BalanceInfos = entries
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeepSeek balance query failed");
            return new AIBalanceResult { Success = false, Error = $"查询 DeepSeek 余额失败：{ex.Message}" };
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            request.Headers.Add("Authorization", $"Bearer {_config.ApiKey}");
        }

        return request;
    }

    private static StringContent CreateJsonContent(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    /// <summary>DeepSeek accepts low/high/max; medium maps to high on the server.</summary>
    private static string NormalizeEffort(string? effort)
    {
        return (effort ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "low" or "minimal" => "low",
            "max" or "ultra" => "max",
            _ => "high"
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
                ? value
                : null;

    private static IReadOnlyList<string> ReadThinkingLevels(JsonElement item)
    {
        if (!item.TryGetProperty("effort", out var effort)
            || effort.ValueKind != JsonValueKind.Object
            || !effort.TryGetProperty("supported_levels", out var levels)
            || levels.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var level in levels.EnumerateArray())
        {
            if (level.ValueKind == JsonValueKind.String)
            {
                result.Add(level.GetString() ?? string.Empty);
            }
            else if (level.ValueKind == JsonValueKind.Object
                     && level.TryGetProperty("level", out var name)
                     && name.ValueKind == JsonValueKind.String)
            {
                result.Add(name.GetString() ?? string.Empty);
            }
        }

        return result.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
    }
}
