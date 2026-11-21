using HD2Mimnt.Core.Entities;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services.AI;

/// <summary>
/// Agnes AI（apihub.agnes-ai.com）提供商：OpenAI 兼容 Chat Completions + Bearer 认证。
/// 思考模式用 vLLM 风格的 chat_template_kwargs.enable_thinking 开关（不同于 OpenAI 的 reasoning_effort），
/// 文档未定义 effort 等级，开启思考时不下发采样档位。
/// </summary>
public class AgnesProvider : OpenAIProvider
{
    protected override string ProviderName => "Agnes";

    protected override string FallbackModel => "agnes-3.0-flash";

    public AgnesProvider(AIProviderConfig config, ILogger<OpenAIProvider> logger, HttpClient? httpClient = null)
        : base(config, logger, httpClient)
    {
    }

    /// <summary>关闭思考时显式禁用并保留温度，避免思考文本混进批量翻译的 JSON 结果。</summary>
    protected override void ApplySamplingOptions(Dictionary<string, object?> payload)
    {
        payload["chat_template_kwargs"] = new Dictionary<string, object?>
        {
            ["enable_thinking"] = _config.EnableThinking
        };

        if (!_config.EnableThinking)
        {
            payload["temperature"] = _config.Temperature;
        }
    }
}
