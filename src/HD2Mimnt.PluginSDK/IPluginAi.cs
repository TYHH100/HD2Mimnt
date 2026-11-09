namespace HD2Mimnt.PluginSDK;

/// <summary>
/// 受限的 AI 能力入口。
///
/// 设计边界（务必遵守）：
/// 1. 插件只能提交提示词、拿到文本结果，永远拿不到 API Key、请求头、端点凭据；
/// 2. 请求由主程序代为发出，插件进程内不存在任何密钥材料；
/// 3. 提示词会在主程序侧被扫描，一旦出现密钥或疑似外传密钥的内容，
///    宿主进程会被立即终止，插件被停用。
///
/// 该能力需要 manifest 声明 "ai.use" 权限并由用户显式授予。
/// </summary>
public interface IPluginAiClient
{
    /// <summary>
    /// 当前是否已配置可用的 AI 服务。插件应在调用前先做判断，
    /// 未配置时给出友好提示而不是直接失败。
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交一次文本补全请求。只返回模型输出，不返回任何请求细节。
    /// </summary>
    /// <param name="prompt">用户提示词。不得包含密钥、令牌或其它敏感凭据。</param>
    /// <param name="options">可选的系统提示词与输出上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PluginAiResult> CompleteAsync(
        string prompt,
        PluginAiOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// AI 请求的可选参数。
/// </summary>
public sealed class PluginAiOptions
{
    /// <summary>
    /// 系统提示词，用于约束模型行为。主程序会在其前面追加安全约束。
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// 期望的最大输出 token 数；为空时使用主程序配置。
    /// </summary>
    public int? MaxOutputTokens { get; set; }
}

/// <summary>
/// AI 请求结果。失败时只给出可读原因，不含供应商原始错误中的凭据片段。
/// </summary>
public sealed class PluginAiResult
{
    public bool Success { get; init; }

    public string Content { get; init; } = string.Empty;

    public string? Error { get; init; }

    public int TokensUsed { get; init; }

    public static PluginAiResult Ok(string content, int tokensUsed = 0) => new()
    {
        Success = true,
        Content = content,
        TokensUsed = tokensUsed
    };

    public static PluginAiResult Fail(string error) => new()
    {
        Success = false,
        Error = error
    };
}
