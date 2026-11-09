using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HD2Mimnt.Application.Services.AI;

/// <summary>
/// Claude (Anthropic) provider implementation
/// </summary>
public class ClaudeProvider : IAIProvider
{
    private readonly AIProviderConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ClaudeProvider> _logger;

    public ClaudeProvider(AIProviderConfig config, ILogger<ClaudeProvider> logger)
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
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _config.ApiEndpoint);
            request.Headers.Add("x-api-key", _config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            var payload = new
            {
                model = _config.Model ?? "claude-3-sonnet-20240229",
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

            var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude connection test failed");
            return false;
        }
    }

    public async Task<AIResponse> SendRequestAsync(AIAnalysisContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var prompt = BuildPrompt(context);

            var request = new HttpRequestMessage(HttpMethod.Post, _config.ApiEndpoint);
            request.Headers.Add("x-api-key", _config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            var payload = new Dictionary<string, object?>
            {
                ["model"] = _config.Model ?? "claude-3-sonnet-20240229",
                ["messages"] = new[] { new { role = "user", content = prompt } },
                ["max_tokens"] = _config.MaxTokens,
                ["system"] = "You are a helpful assistant for Helldivers 2 mod management."
            };

            if (_config.EnableThinking && _config.MaxTokens > 1024)
            {
                var requestedBudget = (_config.ThinkingEffort ?? string.Empty).Trim().ToLowerInvariant() switch
                {
                    "low" => 1024,
                    "high" or "max" => 4096,
                    _ => 2048
                };
                var budget = Math.Min(requestedBudget, _config.MaxTokens - 1);
                payload["thinking"] = new { type = "enabled", budget_tokens = budget };
            }
            else
            {
                // 开启扩展思考时 Claude 要求温度为 1，直接省略以使用默认值。
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
                    Error = AIHttpError.Format("Claude", response.StatusCode, errorContent)
                };
            }

            var responseJson = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            // 扩展思考会在 content 中先返回 thinking 块，只取第一个 text 块。
            var content = string.Empty;
            if (responseJson.TryGetProperty("content", out var contentBlocks)
                && contentBlocks.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentBlocks.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object
                        && block.TryGetProperty("type", out var blockType)
                        && blockType.GetString() == "text"
                        && block.TryGetProperty("text", out var text))
                    {
                        content = text.GetString() ?? string.Empty;
                        break;
                    }
                }
            }

            var inputTotal = 0;
            var cachedInput = 0;
            var outputTokens = 0;
            if (responseJson.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                cachedInput = ReadInt32(usage, "cache_read_input_tokens");
                // 缓存写入与未命中输入都会计费，统一算入输入总量。
                inputTotal = ReadInt32(usage, "input_tokens")
                             + cachedInput
                             + ReadInt32(usage, "cache_creation_input_tokens");
                outputTokens = ReadInt32(usage, "output_tokens");
            }

            var elapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            using (_logger.BeginScope(AIUsageParser.LogFields(_config.Model ?? "claude-3-sonnet-20240229", inputTotal,
                       cachedInput, outputTokens, inputTotal + outputTokens, elapsedMs)))
            {
                _logger.LogDebug("AI 请求完成");
            }

            return new AIResponse
            {
                Success = true,
                Content = content,
                TokensUsed = inputTotal + outputTokens,
                InputTokens = inputTotal,
                CachedInputTokens = cachedInput
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude request failed");
            return new AIResponse
            {
                Success = false,
                Error = $"Request failed: {ex.Message}"
            };
        }
    }

    private static int ReadInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static string BuildPrompt(AIAnalysisContext context)
    {
        return context.ContextType switch
        {
            AIContextType.Translation => BuildTranslationPrompt(context),
            AIContextType.BatchTranslation => BuildBatchTranslationPrompt(context),
            AIContextType.ModAnalysis => BuildModAnalysisPrompt(context),
            AIContextType.LogExplanation => BuildLogExplanationPrompt(context),
            AIContextType.ChangelogSummary => BuildChangelogSummaryPrompt(context),
            AIContextType.ConflictExplanation => BuildConflictExplanationPrompt(context),
            AIContextType.SecurityCheck => BuildSecurityCheckPrompt(context),
            _ => throw new ArgumentException($"Unknown context type: {context.ContextType}")
        };
    }

    private static string BuildTranslationPrompt(AIAnalysisContext context)
    {
        var text = context.Data.GetValueOrDefault("text", "");
        var targetLang = context.TargetLanguage ?? "en";

        return $@"Translate the following text to {targetLang}.
Keep technical terms unchanged.
Preserve markdown formatting.

Text:
{text}";
    }

    private static string BuildBatchTranslationPrompt(AIAnalysisContext context)
    {
        var items = context.Data.GetValueOrDefault("items", "{\"items\":[]}");
        var targetLang = context.TargetLanguage ?? "en";

        return $@"Translate every items[].text into {targetLang}.
Return one compact JSON object per line using exactly {{""id"":0,""text"":""translation""}}.
Keep every input id exactly once and in order. Preserve placeholders, file paths, IDs, and line breaks.
Escape line breaks inside text as \\n. Do not return a JSON array, explanations, or markdown fences.

Input:
{items}";
    }

    private static string BuildModAnalysisPrompt(AIAnalysisContext context)
    {
        var modName = context.Data.GetValueOrDefault("modName", "");
        var fileList = context.Data.GetValueOrDefault("files", "");
        var structure = context.Data.GetValueOrDefault("structure", "");

        return $@"Analyze the following Helldivers 2 mod:

Name: {modName}
Files: {fileList}
Structure: {structure}

Explain:
1. What this mod does
2. Which game aspects it modifies
3. Potential compatibility concerns
4. Installation requirements";
    }

    private static string BuildLogExplanationPrompt(AIAnalysisContext context)
    {
        var logContent = context.Data.GetValueOrDefault("log", "");

        return $@"Explain the following error log from a Helldivers 2 mod manager:

{logContent}

Provide:
1. What went wrong
2. Possible causes
3. Suggested solutions";
    }

    private static string BuildChangelogSummaryPrompt(AIAnalysisContext context)
    {
        var changelog = context.Data.GetValueOrDefault("changelog", "");

        return $@"Summarize the following changelog in a concise way:

{changelog}

Provide a brief summary highlighting the most important changes.";
    }

    private static string BuildConflictExplanationPrompt(AIAnalysisContext context)
    {
        var modList = context.Data.GetValueOrDefault("mods", "");

        return $@"Analyze potential conflicts between these Helldivers 2 mods:

{modList}

Explain:
1. Which mods might conflict
2. What types of conflicts (file overwrites, incompatible changes)
3. Recommended load order or resolution";
    }

    private static string BuildSecurityCheckPrompt(AIAnalysisContext context)
    {
        var files = context.Data.GetValueOrDefault("files", "");
        var scripts = context.Data.GetValueOrDefault("scripts", "");

        return $@"Analyze the security aspects of this Helldivers 2 mod:

Files: {files}
Scripts: {scripts}

Check for:
1. Suspicious file operations
2. Network connections
3. System modifications
4. Unusual permissions

Provide a safety assessment.";
    }
}
