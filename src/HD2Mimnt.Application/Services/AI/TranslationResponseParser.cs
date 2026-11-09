using System.Text.Json;

namespace HD2Mimnt.Application.Services.AI;

/// <summary>
/// 解析批量翻译响应。模型可能返回 JSON 数组、{"translations":[]} 或 JSON Lines；
/// 统一按 id 回填原文，条目不完整时整批失败，避免把半批译文写进缓存。
/// </summary>
internal static class TranslationResponseParser
{
    public static bool TryParse(
        string content,
        IReadOnlyList<string> sources,
        out Dictionary<string, string> translations,
        out string? error)
    {
        translations = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;
        var byId = new Dictionary<int, string>();
        var normalized = StripMarkdownFence(content);

        if (!TryParseDocument(normalized, sources.Count, byId) && !TryParseLines(normalized, sources.Count, byId))
        {
            error = "AI 返回格式不是可解析的翻译结果。";
            return false;
        }

        if (byId.Count != sources.Count)
        {
            error = $"AI 返回的翻译条目不完整（需要 {sources.Count} 条，实际 {byId.Count} 条）。";
            return false;
        }

        for (var id = 0; id < sources.Count; id++)
        {
            translations[sources[id]] = byId[id];
        }

        return true;
    }

    private static bool TryParseDocument(string content, int count, Dictionary<int, string> completed)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                AddArray(root, count, completed);
                return completed.Count > 0;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("translations", out var translations)
                    && translations.ValueKind == JsonValueKind.Array)
                {
                    AddArray(translations, count, completed);
                    return completed.Count > 0;
                }

                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    AddArray(items, count, completed);
                    return completed.Count > 0;
                }
            }
        }
        catch (JsonException)
        {
            // JSON Lines 不是单个 JSON 文档，由逐行解析处理。
        }

        return false;
    }

    private static bool TryParseLines(string content, int count, Dictionary<int, string> completed)
    {
        var parsed = false;
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    AddElement(document.RootElement, count, completed);
                    parsed = true;
                }
            }
            catch (JsonException)
            {
                // 流式拼接可能留下残行；完整条目在最后统一校验。
            }
        }

        return parsed;
    }

    private static void AddArray(JsonElement array, int count, Dictionary<int, string> completed)
    {
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                AddElement(element, count, completed);
            }
        }
    }

    private static void AddElement(JsonElement element, int count, Dictionary<int, string> completed)
    {
        if (!element.TryGetProperty("id", out var idProperty)
            || !idProperty.TryGetInt32(out var id)
            || id < 0
            || id >= count
            || !element.TryGetProperty("text", out var textProperty)
            || textProperty.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var text = textProperty.GetString()?.Trim();
        if (string.IsNullOrEmpty(text) || completed.ContainsKey(id))
            return;

        completed[id] = text;
    }

    private static string StripMarkdownFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLineEnd = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFence > firstLineEnd
            ? trimmed[(firstLineEnd + 1)..lastFence].Trim()
            : trimmed;
    }
}
