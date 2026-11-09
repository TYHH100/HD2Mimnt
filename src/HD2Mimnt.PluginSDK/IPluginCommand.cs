namespace HD2Mimnt.PluginSDK;

/// <summary>
/// Describes a command contributed by a plugin.
/// Declared in the manifest so the host application can present it before the plugin process starts.
/// </summary>
public class PluginCommandDescriptor
{
    /// <summary>
    /// Stable command identifier. Must match the IPluginCommand implementation's Descriptor.Name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// User-facing label shown in the host UI.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Optional explanation of what the command does.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional permission required before the host will invoke this command.
    /// </summary>
    public string? RequiredPermission { get; set; }

    /// <summary>
    /// 命令出现的位置，取值见 <see cref="PluginCommandPlacements"/>。
    /// 默认 <c>pluginPage</c>（仅插件管理页）；<c>modContextMenu</c> 会出现在模组卡片右键菜单。
    /// </summary>
    public string Placement { get; set; } = PluginCommandPlacements.PluginPage;
}

/// <summary>
/// Context handed to a plugin command invocation.
/// </summary>
public class PluginCommandContext
{
    public PluginCommandContext(
        IPluginLogger logger,
        IPluginStorage storage,
        IPluginConfiguration configuration,
        string? arguments = null,
        IPluginAiClient? ai = null)
    {
        Logger = logger;
        Storage = storage;
        Configuration = configuration;
        Arguments = arguments;
        AI = ai ?? UnavailablePluginAiClient.Instance;
    }

    /// <summary>
    /// Logger routed back to the host application.
    /// </summary>
    public IPluginLogger Logger { get; }

    /// <summary>
    /// Plugin-scoped file storage.
    /// </summary>
    public IPluginStorage Storage { get; }

    /// <summary>
    /// Plugin-scoped configuration.
    /// </summary>
    public IPluginConfiguration Configuration { get; }

    /// <summary>
    /// Raw JSON arguments supplied by the caller, if any.
    /// </summary>
    public string? Arguments { get; }

    /// <summary>
    /// 受限 AI 能力；宿主未提供时返回“不可用”实现，调用不会抛异常。
    /// </summary>
    public IPluginAiClient AI { get; }
}

/// <summary>
/// 未启用 AI 能力时的占位实现，避免插件因空引用崩溃。
/// </summary>
internal sealed class UnavailablePluginAiClient : IPluginAiClient
{
    public static readonly UnavailablePluginAiClient Instance = new();

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<PluginAiResult> CompleteAsync(
        string prompt,
        PluginAiOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PluginAiResult.Fail("AI 能力未启用：请在 manifest 声明 ai.use 权限并由用户授权。"));
}

/// <summary>
/// A single executable capability contributed by a plugin.
/// The host discovers every implementation in the plugin assembly and exposes it by Descriptor.Name.
/// </summary>
public interface IPluginCommand
{
    /// <summary>
    /// Command metadata. Name must be unique within the plugin.
    /// </summary>
    PluginCommandDescriptor Descriptor { get; }

    /// <summary>
    /// Execute the command and return a result to the host.
    /// </summary>
    Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default);
}
