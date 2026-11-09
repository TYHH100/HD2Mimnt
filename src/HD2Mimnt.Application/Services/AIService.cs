using HD2Mimnt.Application.Services.AI;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// AI service implementation
/// </summary>
public class AIService : IAIService
{
    private readonly IAIProviderConfigRepository _configRepository;
    private readonly IModRepository _modRepository;
    private readonly ILogger<AIService> _logger;
    private readonly ILogger<OpenAIProvider> _openAILogger;
    private readonly ILogger<ClaudeProvider> _claudeLogger;
    private readonly ILogger<LocalLLMProvider> _localLLMLogger;
    private readonly ILogger<DeepSeekProvider> _deepSeekLogger;

    public AIService(
        IAIProviderConfigRepository configRepository,
        IModRepository modRepository,
        ILogger<AIService> logger,
        ILogger<OpenAIProvider> openAILogger,
        ILogger<ClaudeProvider> claudeLogger,
        ILogger<LocalLLMProvider> localLLMLogger,
        ILogger<DeepSeekProvider> deepSeekLogger)
    {
        _configRepository = configRepository;
        _modRepository = modRepository;
        _logger = logger;
        _openAILogger = openAILogger;
        _claudeLogger = claudeLogger;
        _localLLMLogger = localLLMLogger;
        _deepSeekLogger = deepSeekLogger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configRepository.GetActiveProviderAsync(cancellationToken);
        return config != null;
    }

    public async Task<AIProviderConfig?> GetActiveProviderAsync(CancellationToken cancellationToken = default)
    {
        return await _configRepository.GetActiveProviderAsync(cancellationToken);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configRepository.GetActiveProviderAsync(cancellationToken);
        if (config == null)
        {
            return false;
        }

        var provider = CreateProvider(config);
        return await provider.TestConnectionAsync(cancellationToken);
    }

    public async Task<AIResponse> TestConnectionDetailedAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configRepository.GetActiveProviderAsync(cancellationToken);
        if (config == null)
        {
            return new AIResponse { Success = false, Error = "未配置 AI 服务，请先在设置中填写并保存。" };
        }

        var provider = CreateProvider(config);
        if (provider is IAIProviderProbe probe)
        {
            return await probe.TestConnectionDetailedAsync(cancellationToken);
        }

        var ok = await provider.TestConnectionAsync(cancellationToken);
        return ok
            ? new AIResponse { Success = true, Content = "OK" }
            : new AIResponse { Success = false, Error = "连接测试失败，请检查 API 端点、密钥和模型。" };
    }

    public Task<AIProviderModelsResult> ListModelsAsync(AIProviderConfig config, CancellationToken cancellationToken = default)
    {
        var provider = CreateProvider(config);
        return provider is IAIProviderProbe probe
            ? probe.ListModelsAsync(cancellationToken)
            : Task.FromResult(new AIProviderModelsResult { Success = false, Error = "当前服务不支持自动获取模型列表。" });
    }

    public Task<AIBalanceResult> GetBalanceAsync(AIProviderConfig config, CancellationToken cancellationToken = default)
    {
        var provider = CreateProvider(config);
        return provider is IAIProviderBalance balance
            ? balance.GetBalanceAsync(cancellationToken)
            : Task.FromResult(new AIBalanceResult { Success = false, Error = "当前服务不支持查询余额。" });
    }

    public async Task<AIResponse> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken = default)
    {
        var provider = await GetActiveProviderOrFail(cancellationToken);
        if (provider == null)
        {
            return new AIResponse
            {
                Success = false,
                Error = "No AI provider configured"
            };
        }

        var context = new AIAnalysisContext
        {
            ContextType = AIContextType.Translation,
            TargetLanguage = targetLanguage,
            Data = new Dictionary<string, string>
            {
                ["text"] = text
            }
        };

        return await provider.SendRequestAsync(context, cancellationToken);
    }

    public async Task<AIBatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var distinct = texts
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length == 0)
        {
            return new AIBatchTranslationResult { Success = true };
        }

        var provider = await GetActiveProviderOrFail(cancellationToken);
        if (provider == null)
        {
            return new AIBatchTranslationResult { Error = "未配置 AI 服务，请先在设置中配置。" };
        }

        var payload = JsonSerializer.Serialize(new
        {
            items = distinct.Select((text, id) => new { id, text })
        });
        var context = new AIAnalysisContext
        {
            ContextType = AIContextType.BatchTranslation,
            TargetLanguage = targetLanguage,
            Data = new Dictionary<string, string> { ["items"] = payload }
        };

        var response = await provider.SendRequestAsync(context, cancellationToken);
        if (!response.Success)
        {
            return new AIBatchTranslationResult { Error = response.Error ?? "AI 请求失败" };
        }

        if (!TranslationResponseParser.TryParse(response.Content, distinct, out var translations, out var parseError))
        {
            return new AIBatchTranslationResult { Error = parseError };
        }

        return new AIBatchTranslationResult
        {
            Success = true,
            Translations = translations,
            TokensUsed = response.TokensUsed,
            InputTokens = response.InputTokens,
            CachedInputTokens = response.CachedInputTokens
        };
    }

    public async Task<AIResponse> AnalyzeModAsync(string modId, CancellationToken cancellationToken = default)
    {
        var provider = await GetActiveProviderOrFail(cancellationToken);
        if (provider == null)
        {
            return new AIResponse
            {
                Success = false,
                Error = "No AI provider configured"
            };
        }

        if (!Guid.TryParse(modId, out var modGuid))
        {
            return new AIResponse
            {
                Success = false,
                Error = $"Invalid mod ID format: {modId}"
            };
        }

        var mod = await _modRepository.GetByIdAsync(modGuid, cancellationToken);
        if (mod == null)
        {
            return new AIResponse
            {
                Success = false,
                Error = $"Mod not found: {modId}"
            };
        }

        // Get file list from mod path
        var files = Directory.Exists(mod.Path)
            ? string.Join(", ", Directory.GetFiles(mod.Path, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Take(50)) // Limit to avoid huge prompts
            : "No files found";

        var context = new AIAnalysisContext
        {
            ContextType = AIContextType.ModAnalysis,
            Data = new Dictionary<string, string>
            {
                ["modName"] = mod.Name,
                ["files"] = files,
                ["structure"] = mod.WorkflowType ?? "Unknown"
            }
        };

        return await provider.SendRequestAsync(context, cancellationToken);
    }

    public async Task<AIResponse> ExplainLogAsync(string logContent, CancellationToken cancellationToken = default)
    {
        var provider = await GetActiveProviderOrFail(cancellationToken);
        if (provider == null)
        {
            return new AIResponse
            {
                Success = false,
                Error = "No AI provider configured"
            };
        }

        var context = new AIAnalysisContext
        {
            ContextType = AIContextType.LogExplanation,
            Data = new Dictionary<string, string>
            {
                ["log"] = logContent
            }
        };

        return await provider.SendRequestAsync(context, cancellationToken);
    }

    public async Task<AIResponse> SummarizeChangelogAsync(string changelog, CancellationToken cancellationToken = default)
    {
        var provider = await GetActiveProviderOrFail(cancellationToken);
        if (provider == null)
        {
            return new AIResponse
            {
                Success = false,
                Error = "No AI provider configured"
            };
        }

        var context = new AIAnalysisContext
        {
            ContextType = AIContextType.ChangelogSummary,
            Data = new Dictionary<string, string>
            {
                ["changelog"] = changelog
            }
        };

        return await provider.SendRequestAsync(context, cancellationToken);
    }

    public async Task<AIResponse> ExplainConflictAsync(List<string> modIds, CancellationToken cancellationToken = default)
    {
        var provider = await GetActiveProviderOrFail(cancellationToken);
        if (provider == null)
        {
            return new AIResponse
            {
                Success = false,
                Error = "No AI provider configured"
            };
        }

        var mods = new List<string>();
        foreach (var modId in modIds)
        {
            if (Guid.TryParse(modId, out var modGuid))
            {
                var mod = await _modRepository.GetByIdAsync(modGuid, cancellationToken);
                if (mod != null)
                {
                    mods.Add($"{mod.Name} (ID: {modId})");
                }
            }
        }

        var context = new AIAnalysisContext
        {
            ContextType = AIContextType.ConflictExplanation,
            Data = new Dictionary<string, string>
            {
                ["mods"] = string.Join("\n", mods)
            }
        };

        return await provider.SendRequestAsync(context, cancellationToken);
    }

    public async Task<AIResponse> CheckSecurityAsync(string modId, CancellationToken cancellationToken = default)
    {
        var provider = await GetActiveProviderOrFail(cancellationToken);
        if (provider == null)
        {
            return new AIResponse
            {
                Success = false,
                Error = "No AI provider configured"
            };
        }

        if (!Guid.TryParse(modId, out var modGuid))
        {
            return new AIResponse
            {
                Success = false,
                Error = $"Invalid mod ID format: {modId}"
            };
        }

        var mod = await _modRepository.GetByIdAsync(modGuid, cancellationToken);
        if (mod == null)
        {
            return new AIResponse
            {
                Success = false,
                Error = $"Mod not found: {modId}"
            };
        }

        var files = Directory.Exists(mod.Path)
            ? string.Join(", ", Directory.GetFiles(mod.Path, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Take(50))
            : "No files found";

        var scripts = Directory.Exists(mod.Path)
            ? string.Join(", ", Directory.GetFiles(mod.Path, "*.lua", SearchOption.AllDirectories)
                .Select(Path.GetFileName))
            : "No scripts found";

        var context = new AIAnalysisContext
        {
            ContextType = AIContextType.SecurityCheck,
            Data = new Dictionary<string, string>
            {
                ["files"] = files,
                ["scripts"] = scripts
            }
        };

        return await provider.SendRequestAsync(context, cancellationToken);
    }

    /// <summary>
    /// 通用文本补全。插件等受控调用方只能拿到文本结果：
    /// 配置（含 API Key）始终留在本进程，返回内容由调用方再做一次脱敏。
    /// </summary>
    public async Task<AIResponse> CompleteAsync(
        string prompt,
        string? systemPrompt = null,
        int? maxOutputTokens = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new AIResponse { Success = false, Error = "提示词不能为空" };
        }

        var config = await _configRepository.GetActiveProviderAsync(cancellationToken);
        if (config == null)
        {
            return new AIResponse { Success = false, Error = "未配置 AI 服务，请先在设置中填写并保存。" };
        }

        // 仓库每次返回新对象，可安全就地收紧上限；不允许超过用户配置。
        if (maxOutputTokens is > 0 and var requested && requested < config.MaxTokens)
        {
            config.MaxTokens = requested;
        }

        var context = new AIAnalysisContext
        {
            ContextType = AIContextType.PluginRequest,
            Data = new Dictionary<string, string>
            {
                ["prompt"] = prompt,
                ["system"] = systemPrompt ?? string.Empty
            }
        };

        return await CreateProvider(config).SendRequestAsync(context, cancellationToken);
    }

    private async Task<IAIProvider?> GetActiveProviderOrFail(CancellationToken cancellationToken)
    {
        var config = await _configRepository.GetActiveProviderAsync(cancellationToken);
        if (config == null)
        {
            _logger.LogWarning("No active AI provider configured");
            return null;
        }

        return CreateProvider(config);
    }

    private IAIProvider CreateProvider(AIProviderConfig config)
    {
        return config.Type switch
        {
            AIProviderType.OpenAI => new OpenAIProvider(config, _openAILogger),
            AIProviderType.Claude => new ClaudeProvider(config, _claudeLogger),
            AIProviderType.DeepSeek => new DeepSeekProvider(config, _deepSeekLogger),
            AIProviderType.LocalLLM => new LocalLLMProvider(config, _localLLMLogger),
            // 自定义端点通常兼容 OpenAI Chat Completions；Ollama 专用格式只留给 LocalLLM。
            AIProviderType.Custom => new OpenAIProvider(config, _openAILogger),
            _ => throw new NotSupportedException($"AI provider type not supported: {config.Type}")
        };
    }
}
