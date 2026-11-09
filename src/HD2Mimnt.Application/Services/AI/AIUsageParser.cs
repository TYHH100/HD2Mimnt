using System.Text.Json;

namespace HD2Mimnt.Application.Services.AI;

internal readonly record struct AIUsage(int TotalTokens, int InputTokens, int CachedInputTokens);

/// <summary>
/// Reads token usage from OpenAI-compatible responses. DeepSeek reports
/// prompt_cache_hit_tokens / prompt_cache_miss_tokens; OpenAI reports
/// prompt_tokens_details.cached_tokens.
/// </summary>
internal static class AIUsageParser
{
    public static AIUsage ParseOpenAiCompatible(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        var total = ReadInt32(usage, "total_tokens");
        var input = ReadInt32(usage, "prompt_tokens");
        var cached = 0;

        var hit = ReadOptionalInt32(usage, "prompt_cache_hit_tokens");
        var miss = ReadOptionalInt32(usage, "prompt_cache_miss_tokens");
        if (hit.HasValue || miss.HasValue)
        {
            cached = Math.Max(0, hit ?? 0);
            input = cached + Math.Max(0, miss ?? 0);
        }
        else if (usage.TryGetProperty("prompt_tokens_details", out var details)
                 && details.ValueKind == JsonValueKind.Object)
        {
            cached = Math.Max(0, ReadInt32(details, "cached_tokens"));
        }

        if (total <= 0)
        {
            total = input + ReadInt32(usage, "completion_tokens");
        }

        return new AIUsage(total, input, cached);
    }

    /// <summary>
    /// 三个提供者共用的日志作用域字段：字段名统一后，不同提供商的用量才能横向比较。
    /// 输出形如 model=deepseek-chat in=244 cached=0 out=207 total=451 elapsed=1932ms。
    /// </summary>
    public static Dictionary<string, object?> LogFields(
        string? model, int inputTokens, int cachedInputTokens, int outputTokens, int totalTokens, long elapsedMs) => new()
    {
        ["model"] = model,
        ["in"] = inputTokens,
        ["cached"] = cachedInputTokens,
        ["out"] = outputTokens,
        ["total"] = totalTokens,
        ["elapsed"] = $"{elapsedMs}ms",
    };

    private static int ReadInt32(JsonElement element, string propertyName)
        => ReadOptionalInt32(element, propertyName) ?? 0;
    private static int? ReadOptionalInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt32(out var value)
            ? value
            : null;
}
