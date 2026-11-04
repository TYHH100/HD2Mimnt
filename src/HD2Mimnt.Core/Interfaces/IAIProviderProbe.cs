using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Optional capability for providers that can validate credentials and list models.
/// </summary>
public interface IAIProviderProbe
{
    /// <summary>Validate the configured endpoint and key; returns a detailed error on failure.</summary>
    Task<AIResponse> TestConnectionDetailedAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetch the provider's available models.</summary>
    Task<AIProviderModelsResult> ListModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for providers that expose an account balance API.
/// </summary>
public interface IAIProviderBalance
{
    /// <summary>Query the account balance.</summary>
    Task<AIBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default);
}
