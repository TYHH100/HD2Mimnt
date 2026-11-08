using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Service interface for AI functionality
/// </summary>
public interface IAIService
{
    /// <summary>
    /// Check if AI is available and configured
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Translate text to target language
    /// </summary>
    Task<AIResponse> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Translate multiple short texts in one request; returns source text to translation mappings.
    /// </summary>
    Task<AIBatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze a mod and provide explanation
    /// </summary>
    Task<AIResponse> AnalyzeModAsync(string modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explain error log content
    /// </summary>
    Task<AIResponse> ExplainLogAsync(string logContent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Summarize changelog or README content
    /// </summary>
    Task<AIResponse> SummarizeChangelogAsync(string changelog, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explain potential conflicts between mods
    /// </summary>
    Task<AIResponse> ExplainConflictAsync(List<string> modIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform security check on a mod
    /// </summary>
    Task<AIResponse> CheckSecurityAsync(string modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 通用文本补全。供插件等受控调用方使用：
    /// 请求在本进程内完成，API Key 不会传给调用方，也不会写入返回内容。
    /// </summary>
    /// <param name="prompt">用户提示词。</param>
    /// <param name="systemPrompt">可选的系统提示，会与内置安全约束合并。</param>
    /// <param name="maxOutputTokens">可选的输出上限，超过配置上限时以配置为准。</param>
    Task<AIResponse> CompleteAsync(
        string prompt,
        string? systemPrompt = null,
        int? maxOutputTokens = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get active AI provider configuration
    /// </summary>
    Task<AIProviderConfig?> GetActiveProviderAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Test connection to active AI provider
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Test connection and return the provider's error detail when it fails
    /// </summary>
    Task<AIResponse> TestConnectionDetailedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch models from a provider configuration (may be an unsaved draft)
    /// </summary>
    Task<AIProviderModelsResult> ListModelsAsync(AIProviderConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query the account balance for a provider configuration (may be an unsaved draft)
    /// </summary>
    Task<AIBalanceResult> GetBalanceAsync(AIProviderConfig config, CancellationToken cancellationToken = default);
}
