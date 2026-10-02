using HD2Mimnt.Application.Services.AI;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

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

    public AIService(
        IAIProviderConfigRepository configRepository,
        IModRepository modRepository,
        ILogger<AIService> logger,
        ILogger<OpenAIProvider> openAILogger,
        ILogger<ClaudeProvider> claudeLogger,
        ILogger<LocalLLMProvider> localLLMLogger)
    {
        _configRepository = configRepository;
        _modRepository = modRepository;
        _logger = logger;
        _openAILogger = openAILogger;
        _claudeLogger = claudeLogger;
        _localLLMLogger = localLLMLogger;
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
            AIProviderType.LocalLLM => new LocalLLMProvider(config, _localLLMLogger),
            AIProviderType.Custom => new LocalLLMProvider(config, _localLLMLogger), // Treat custom as local LLM format
            _ => throw new NotSupportedException($"AI provider type not supported: {config.Type}")
        };
    }
}
