namespace HD2Mimnt.Core.Enums;

/// <summary>
/// AI provider type
/// </summary>
public enum AIProviderType
{
    /// <summary>
    /// No AI provider
    /// </summary>
    None = 0,

    /// <summary>
    /// OpenAI (ChatGPT)
    /// </summary>
    OpenAI = 1,

    /// <summary>
    /// Anthropic Claude
    /// </summary>
    Claude = 2,

    /// <summary>
    /// Local LLM (Ollama, llama.cpp, etc.)
    /// </summary>
    LocalLLM = 3,

    /// <summary>
    /// Custom API endpoint
    /// </summary>
    Custom = 99
}

/// <summary>
/// AI analysis context type
/// </summary>
public enum AIContextType
{
    /// <summary>
    /// Translation task
    /// </summary>
    Translation,

    /// <summary>
    /// Mod analysis and explanation
    /// </summary>
    ModAnalysis,

    /// <summary>
    /// Log/error explanation
    /// </summary>
    LogExplanation,

    /// <summary>
    /// Changelog summary
    /// </summary>
    ChangelogSummary,

    /// <summary>
    /// Mod conflict explanation
    /// </summary>
    ConflictExplanation,

    /// <summary>
    /// Security check
    /// </summary>
    SecurityCheck
}
