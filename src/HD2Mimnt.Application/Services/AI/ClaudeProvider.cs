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

            var payload = new
            {
                model = _config.Model ?? "claude-3-sonnet-20240229",
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                max_tokens = _config.MaxTokens,
                temperature = _config.Temperature,
                system = "You are a helpful assistant for Helldivers 2 mod management."
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                return new AIResponse
                {
                    Success = false,
                    Error = $"Claude API error: {response.StatusCode} - {errorContent}"
                };
            }

            var responseJson = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var content = responseJson.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
            var tokensUsed = responseJson.GetProperty("usage").GetProperty("input_tokens").GetInt32() +
                           responseJson.GetProperty("usage").GetProperty("output_tokens").GetInt32();

            return new AIResponse
            {
                Success = true,
                Content = content,
                TokensUsed = tokensUsed
            };
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

    private static string BuildPrompt(AIAnalysisContext context)
    {
        return context.ContextType switch
        {
            AIContextType.Translation => BuildTranslationPrompt(context),
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
