using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// AI provider interface for different AI backends
/// </summary>
public interface IAIProvider
{
    /// <summary>
    /// Send a request to the AI provider
    /// </summary>
    Task<AIResponse> SendRequestAsync(AIAnalysisContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Test connection to the AI provider
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get provider configuration
    /// </summary>
    AIProviderConfig GetConfiguration();
}
