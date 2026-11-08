using System.Collections.Concurrent;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 一次插件 AI 请求的处理结果。
/// </summary>
public sealed class PluginAiOutcome
{
    public bool Success { get; init; }

    public string Content { get; init; } = string.Empty;

    public string? Error { get; init; }

    public int TokensUsed { get; init; }

    /// <summary>是否触发致命安全策略（需要终止宿主并停用插件）。</summary>
    public bool Fatal { get; init; }

    public string? FatalReason { get; init; }

    public static PluginAiOutcome Ok(string content, int tokensUsed) => new()
    {
        Success = true,
        Content = content,
        TokensUsed = tokensUsed
    };

    public static PluginAiOutcome Fail(string error) => new() { Success = false, Error = error };

    public static PluginAiOutcome Violation(string reason) => new()
    {
        Success = false,
        Error = reason,
        Fatal = true,
        FatalReason = reason
    };
}

public interface IPluginAiBroker
{
    /// <summary>
    /// 代插件发起一次 AI 请求。密钥始终留在主进程：
    /// 插件只能提交提示词并拿到文本，任何命中真实密钥的内容都会被拦截。
    /// </summary>
    Task<PluginAiOutcome> HandleAsync(PluginAiRequest request, string pluginId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 插件 AI 中介：权限校验 → 频率限制 → 密钥登记与提示词扫描 → 代发请求 → 结果脱敏。
///
/// 这是“插件可以用 AI，但永远不知道 API Key 是什么”的唯一通道。
/// 插件进程里不存在密钥；提示词中若出现真实密钥，会被判定为外传尝试并触发终止。
/// </summary>
public sealed class PluginAiBroker : IPluginAiBroker
{
    /// <summary>manifest 中申请 AI 能力的权限标识。</summary>
    public const string AiPermission = "ai.use";

    private const int MaxPromptChars = 16_000;
    private const int MaxSystemPromptChars = 4_000;
    private const int MaxCallsPerMinute = 20;

    private readonly IAIService _aiService;
    private readonly IPluginRepository _pluginRepository;
    private readonly IPluginPermissionRepository _permissionRepository;
    private readonly IPluginSecretGuard _secretGuard;
    private readonly IPluginGrantSession? _grantSession;
    private readonly ILogger<PluginAiBroker>? _logger;

    private readonly ConcurrentDictionary<string, Queue<long>> _callLog = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _quotaLocks = new(StringComparer.Ordinal);

    public PluginAiBroker(
        IAIService aiService,
        IPluginRepository pluginRepository,
        IPluginPermissionRepository permissionRepository,
        IPluginSecretGuard secretGuard,
        ILogger<PluginAiBroker>? logger = null,
        IPluginGrantSession? grantSession = null)
    {
        _aiService = aiService;
        _pluginRepository = pluginRepository;
        _permissionRepository = permissionRepository;
        _secretGuard = secretGuard;
        _logger = logger;
        _grantSession = grantSession;
    }

    public async Task<PluginAiOutcome> HandleAsync(
        PluginAiRequest request,
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        var prompt = request.Prompt ?? string.Empty;
        var systemPrompt = request.SystemPrompt;

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return PluginAiOutcome.Fail("提示词不能为空");
        }

        if (prompt.Length > MaxPromptChars)
        {
            return PluginAiOutcome.Fail($"提示词超过 {MaxPromptChars} 字符上限");
        }

        if (systemPrompt is { Length: > MaxSystemPromptChars })
        {
            return PluginAiOutcome.Fail($"系统提示词超过 {MaxSystemPromptChars} 字符上限");
        }

        var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
        if (plugin is null)
        {
            return PluginAiOutcome.Fail("插件未安装");
        }

        if (!plugin.Enabled)
        {
            return PluginAiOutcome.Fail("插件已停用");
        }

        if (!await HasAiPermissionAsync(plugin, cancellationToken))
        {
            return PluginAiOutcome.Fail($"缺少权限 '{AiPermission}'，请在插件权限中显式授权");
        }

        if (!TryConsumeQuota(pluginId))
        {
            return PluginAiOutcome.Fail($"AI 调用过于频繁，每分钟最多 {MaxCallsPerMinute} 次");
        }

        var activeProvider = await _aiService.GetActiveProviderAsync(cancellationToken);
        if (activeProvider is null)
        {
            return PluginAiOutcome.Fail("未配置 AI 服务，请先在设置中填写并保存。");
        }

        // 把真实密钥登记进守卫，之后所有插件可控文本都能精确匹配
        _secretGuard.Register(activeProvider.ApiKey);

        var promptScan = _secretGuard.Scan(prompt);
        if (promptScan.IsFatal)
        {
            return PluginAiOutcome.Violation($"插件试图向 AI 提交真实密钥：{promptScan.Reason}");
        }
        if (promptScan.IsViolation)
        {
            return PluginAiOutcome.Fail($"{promptScan.Reason}，请求已被拒绝");
        }

        var systemScan = _secretGuard.Scan(systemPrompt);
        if (systemScan.IsFatal)
        {
            return PluginAiOutcome.Violation($"插件试图在系统提示词中提交真实密钥：{systemScan.Reason}");
        }
        if (systemScan.IsViolation)
        {
            return PluginAiOutcome.Fail($"{systemScan.Reason}，请求已被拒绝");
        }

        var response = await _aiService.CompleteAsync(
            prompt,
            systemPrompt,
            request.MaxOutputTokens,
            cancellationToken);

        if (!response.Success)
        {
            // 供应商错误可能回显请求头或密钥片段，先脱敏再交给插件
            return PluginAiOutcome.Fail(_secretGuard.Redact(response.Error ?? "AI 请求失败"));
        }

        var content = response.Content ?? string.Empty;
        var contentScan = _secretGuard.Scan(content);
        if (contentScan.IsFatal)
        {
            // 模型被诱导说出了真实密钥：不能把内容交给插件
            return PluginAiOutcome.Violation($"AI 返回内容包含真实密钥：{contentScan.Reason}");
        }

        _logger?.LogDebug("插件 {PluginId} AI 请求完成，tokens={Tokens}", pluginId, response.TokensUsed);
        return PluginAiOutcome.Ok(_secretGuard.Redact(content), response.TokensUsed);
    }

    private async Task<bool> HasAiPermissionAsync(Plugin plugin, CancellationToken cancellationToken)
    {
        // 会话级授权（安装时的“仅本次允许”）只存在内存，优先判断
        if (_grantSession?.IsGranted(plugin.Id, AiPermission) == true)
        {
            return true;
        }

        if (string.Equals(plugin.PermissionMode, "FullAccess", StringComparison.Ordinal))
        {
            return true;
        }

        var permissions = await _permissionRepository.GetByPluginIdAsync(plugin.Id, cancellationToken);
        return permissions.Any(p =>
            string.Equals(p.Permission, AiPermission, StringComparison.OrdinalIgnoreCase) && p.Granted);
    }

    /// <summary>滑动一分钟窗口，避免单个插件把账号额度打爆。</summary>
    private bool TryConsumeQuota(string pluginId)
    {
        var now = DateTime.UtcNow.Ticks;
        var windowStart = now - TimeSpan.FromMinutes(1).Ticks;
        var sync = _quotaLocks.GetOrAdd(pluginId, _ => new object());

        lock (sync)
        {
            var calls = _callLog.GetOrAdd(pluginId, _ => new Queue<long>());
            while (calls.Count > 0 && calls.Peek() < windowStart)
            {
                calls.Dequeue();
            }

            if (calls.Count >= MaxCallsPerMinute)
            {
                return false;
            }

            calls.Enqueue(now);
            return true;
        }
    }
}
