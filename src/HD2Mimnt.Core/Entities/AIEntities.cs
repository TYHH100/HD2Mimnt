using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// AI provider configuration entity
/// </summary>
public class AIProviderConfig
{
    /// <summary>
    /// Unique identifier for the provider configuration
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the provider
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Type of AI provider
    /// </summary>
    public AIProviderType Type { get; set; }

    /// <summary>
    /// API endpoint URL
    /// </summary>
    public string ApiEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// API key (encrypted in storage)
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Model name (e.g., gpt-4, claude-3-opus)
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Whether this provider is enabled
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Maximum tokens per request
    /// </summary>
    public int MaxTokens { get; set; } = 4000;

    /// <summary>
    /// Temperature for generation (0.0 - 2.0)
    /// </summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// Created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last modified timestamp
    /// </summary>
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>
/// AI analysis context
/// </summary>
public class AIAnalysisContext
{
    /// <summary>
    /// Type of analysis context
    /// </summary>
    public AIContextType ContextType { get; set; }

    /// <summary>
    /// Context data (key-value pairs)
    /// </summary>
    public Dictionary<string, string> Data { get; set; } = new();

    /// <summary>
    /// Target language for translation (ISO 639-1 code)
    /// </summary>
    public string? TargetLanguage { get; set; }

    /// <summary>
    /// User language preference
    /// </summary>
    public string Language { get; set; } = "en";
}

/// <summary>
/// AI response
/// </summary>
public class AIResponse
{
    /// <summary>
    /// Whether the request succeeded
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Response content
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Number of tokens used
    /// </summary>
    public int TokensUsed { get; set; }

    /// <summary>
    /// Response timestamp
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
