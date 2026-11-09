using HD2Mimnt.PluginSDK;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Implementation of IPluginContext that provides all plugin services
/// </summary>
public class PluginContext : IPluginContext
{
    public IPluginLogger Logger { get; }
    public IPluginStorage Storage { get; }
    public IPluginModAccess ModAccess { get; }
    public IPluginConfiguration Configuration { get; }
    public IPluginAiClient AI { get; }

    public PluginContext(
        IPluginLogger logger,
        IPluginStorage storage,
        IPluginModAccess modAccess,
        IPluginConfiguration configuration,
        IPluginAiClient? ai = null)
    {
        Logger = logger;
        Storage = storage;
        ModAccess = modAccess;
        Configuration = configuration;
        AI = ai ?? DisabledPluginAiClient.Instance;
    }

    /// <summary>未接入 AI 中介时的占位实现：明确拒绝，不产生任何网络请求。</summary>
    private sealed class DisabledPluginAiClient : IPluginAiClient
    {
        public static readonly DisabledPluginAiClient Instance = new();

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<PluginAiResult> CompleteAsync(
            string prompt,
            PluginAiOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PluginAiResult.Fail("AI 能力未启用"));
    }
}
