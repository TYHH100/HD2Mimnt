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
    /// Get active AI provider configuration
    /// </summary>
    Task<AIProviderConfig?> GetActiveProviderAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Test connection to active AI provider
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}
