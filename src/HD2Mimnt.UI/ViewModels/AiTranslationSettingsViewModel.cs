using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.AI;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Localization;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

public sealed record AiTargetLanguageChoice(string Code, string DisplayName);

public sealed record AiThinkingEffortChoice(string Value, string DisplayName);

/// <summary>
/// 设置页“AI 服务”分类：复用 AIProviderConfigs 里的单一活动配置，密钥仍由数据层用 DPAPI 加密。
/// </summary>
public partial class AiTranslationSettingsViewModel : ObservableObject
{
    private const string ProviderId = "default";
    private const string ProviderDisplayName = "AI 服务";

    private readonly IAIService _ai;
    private readonly IAIProviderConfigRepository _repository;
    private readonly SettingsService _settings;
    private string? _savedApiKey;
    private string? _loadedProviderType;
    private bool _loading;

    [ObservableProperty] private string _providerType = nameof(AIProviderType.OpenAI);
    [ObservableProperty] private string _endpoint = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private bool _hasSavedApiKey;
    [ObservableProperty] private bool _enableThinking;
    [ObservableProperty] private string _thinkingEffort = "medium";
    [ObservableProperty] private string _balanceText = string.Empty;
    [ObservableProperty] private AiTargetLanguageChoice _selectedTargetLanguage = TargetLanguages[0];
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public static IReadOnlyList<string> ProviderTypes { get; } =
    [
        nameof(AIProviderType.OpenAI),
        nameof(AIProviderType.Claude),
        nameof(AIProviderType.DeepSeek),
        nameof(AIProviderType.LocalLLM),
        nameof(AIProviderType.Custom)
    ];

    public static IReadOnlyList<AiTargetLanguageChoice> TargetLanguages { get; } = new[]
    {
        new AiTargetLanguageChoice("zh-CN", "简体中文"),
        new AiTargetLanguageChoice("zh-TW", "繁體中文"),
        new AiTargetLanguageChoice("en-US", "English"),
        new AiTargetLanguageChoice("ja-JP", "日本語"),
        new AiTargetLanguageChoice("ko-KR", "한국어")
    };

    public ObservableCollection<string> AvailableModels { get; } = new();

    public ObservableCollection<AiThinkingEffortChoice> ThinkingEfforts { get; } = new();

    public bool HasAvailableModels => AvailableModels.Count > 0;

    public bool ShowThinking => ParseProviderType() != AIProviderType.LocalLLM;

    public bool ShowThinkingEffort => EnableThinking && ThinkingEfforts.Count > 0;

    public bool ShowBalance => ProviderType == nameof(AIProviderType.DeepSeek);

    public string ApiKeyHint => HasSavedApiKey && IsSameProviderAsSaved
        ? UiLocalization.Current["Settings.AiTranslation.ApiKeySaved"]
        : UiLocalization.Current["Settings.AiTranslation.ApiKeyHint"];

    public AiTranslationSettingsViewModel(
        IAIService ai,
        IAIProviderConfigRepository repository,
        SettingsService settings)
    {
        _ai = ai;
        _repository = repository;
        _settings = settings;
        ApplyDefaults(ProviderType);
        UpdateThinkingChoices();
    }

    partial void OnProviderTypeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowBalance));
        OnPropertyChanged(nameof(ApiKeyHint));
        if (_loading) return;
        ApplyDefaults(value);
        UpdateThinkingChoices();
        ResetProbeResults();
    }

    partial void OnHasSavedApiKeyChanged(bool value) => OnPropertyChanged(nameof(ApiKeyHint));

    partial void OnEnableThinkingChanged(bool value) => OnPropertyChanged(nameof(ShowThinkingEffort));

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _loading = true;
        IsBusy = true;
        try
        {
            var config = await _repository.GetByIdAsync(ProviderId, cancellationToken);
            if (config != null)
            {
                ProviderType = config.Type.ToString();
                Endpoint = config.ApiEndpoint;
                Model = config.Model ?? string.Empty;
                EnableThinking = config.EnableThinking;
                ThinkingEffort = config.ThinkingEffort;
                _savedApiKey = config.ApiKey;
                _loadedProviderType = config.Type.ToString();
                HasSavedApiKey = !string.IsNullOrWhiteSpace(config.ApiKey);
            }
            else
            {
                ApplyDefaults(ProviderType);
                EnableThinking = false;
                ThinkingEffort = "medium";
                _savedApiKey = null;
                _loadedProviderType = null;
                HasSavedApiKey = false;
            }

            UpdateThinkingChoices();
            ApiKey = string.Empty;
            ResetProbeResults();
            OnPropertyChanged(nameof(ShowBalance));
            OnPropertyChanged(nameof(ApiKeyHint));

            var code = _settings.GetSetting(
                SettingKeys.AiTranslationTargetLanguage, OptionTranslationService.DefaultTargetLanguage);
            SelectedTargetLanguage = TargetLanguages.FirstOrDefault(
                language => language.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? TargetLanguages[0];
            StatusMessage = string.Empty;

            if (!string.IsNullOrWhiteSpace(_savedApiKey))
            {
                _ = RefreshProbeDataQuietlyAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!ValidateEndpoint())
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            StatusMessage = UiLocalization.Current["Settings.AiTranslation.InvalidModel"];
            return false;
        }

        var type = ParseProviderType();
        ThinkingEffort = NormalizeThinkingEffort(type, ThinkingEffort);
        if (RequiresApiKey(type) && string.IsNullOrWhiteSpace(ResolveApiKey()))
        {
            StatusMessage = UiLocalization.Current["Settings.AiTranslation.MissingApiKey"];
            return false;
        }

        IsBusy = true;
        try
        {
            var existing = await _repository.GetByIdAsync(ProviderId, cancellationToken);
            if (existing == null)
            {
                var config = new AIProviderConfig
                {
                    Id = ProviderId,
                    Name = ProviderDisplayName,
                    Type = type,
                    ApiEndpoint = Endpoint.Trim(),
                    ApiKey = ApiKey.Trim(),
                    Model = Model.Trim(),
                    IsEnabled = true,
                    MaxTokens = 4000,
                    Temperature = 0.2,
                    EnableThinking = EnableThinking,
                    ThinkingEffort = ThinkingEffort,
                    CreatedAt = DateTime.UtcNow
                };
                await _repository.AddAsync(config, cancellationToken);
            }
            else
            {
                existing.Name = ProviderDisplayName;
                existing.Type = type;
                existing.ApiEndpoint = Endpoint.Trim();
                existing.Model = Model.Trim();
                existing.IsEnabled = true;
                existing.Temperature = 0.2;
                existing.EnableThinking = EnableThinking;
                existing.ThinkingEffort = ThinkingEffort;
                if (!string.IsNullOrWhiteSpace(ApiKey))
                    existing.ApiKey = ApiKey.Trim();
                await _repository.UpdateAsync(existing, cancellationToken);
            }

            await _repository.SetActiveProviderAsync(ProviderId, cancellationToken);
            await _settings.SetSettingAsync(
                SettingKeys.AiTranslationTargetLanguage, SelectedTargetLanguage.Code);

            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                _savedApiKey = ApiKey.Trim();
                HasSavedApiKey = true;
                ApiKey = string.Empty;
            }

            _loadedProviderType = ProviderType;
            OnPropertyChanged(nameof(ApiKeyHint));
            StatusMessage = UiLocalization.Current["Settings.AiTranslation.Saved"];
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (!await SaveAsync())
            return;

        IsBusy = true;
        try
        {
            var result = await _ai.TestConnectionDetailedAsync();
            if (!result.Success)
            {
                StatusMessage = string.IsNullOrWhiteSpace(result.Error)
                    ? UiLocalization.Current["Settings.AiTranslation.TestFailed"]
                    : result.Error;
                return;
            }

            StatusMessage = UiLocalization.Current["Settings.AiTranslation.TestSucceeded"];
            await FetchModelsCoreAsync(updateStatus: false);
            await RefreshBalanceCoreAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = UiLocalization.Current.Format("Settings.AiTranslation.TestFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (!ValidateEndpoint())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ResolveApiKey()))
        {
            StatusMessage = UiLocalization.Current["Settings.AiTranslation.MissingApiKey"];
            return;
        }

        IsBusy = true;
        try
        {
            await FetchModelsCoreAsync(updateStatus: true);
        }
        catch (Exception ex)
        {
            StatusMessage = UiLocalization.Current.Format("Settings.AiTranslation.FetchModelsFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshBalanceAsync()
    {
        if (!ShowBalance)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ResolveApiKey()))
        {
            StatusMessage = UiLocalization.Current["Settings.AiTranslation.MissingApiKey"];
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshBalanceCoreAsync();
        }
        catch (Exception ex)
        {
            BalanceText = UiLocalization.Current.Format("Settings.AiTranslation.BalanceFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await _repository.DeleteAsync(ProviderId, cancellationToken);
            await _settings.SetSettingAsync(
                SettingKeys.AiTranslationTargetLanguage, OptionTranslationService.DefaultTargetLanguage);
            _loading = true;
            ProviderType = nameof(AIProviderType.OpenAI);
            SelectedTargetLanguage = TargetLanguages[0];
            ApiKey = string.Empty;
            HasSavedApiKey = false;
            EnableThinking = false;
            ThinkingEffort = "medium";
            _savedApiKey = null;
            _loadedProviderType = null;
            _loading = false;
            ApplyDefaults(ProviderType);
            UpdateThinkingChoices();
            ResetProbeResults();
            OnPropertyChanged(nameof(ShowBalance));
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    private async Task FetchModelsCoreAsync(bool updateStatus)
    {
        var result = await _ai.ListModelsAsync(BuildProbeConfig());
        if (!result.Success)
        {
            if (updateStatus)
            {
                StatusMessage = string.IsNullOrWhiteSpace(result.Error)
                    ? UiLocalization.Current["Settings.AiTranslation.FetchModelsFailed"]
                    : result.Error;
            }

            return;
        }

        SetAvailableModels(result.Models.Select(model => model.Id));
        if (string.IsNullOrWhiteSpace(Model) && AvailableModels.Count > 0)
        {
            Model = AvailableModels[0];
        }

        if (updateStatus)
        {
            StatusMessage = AvailableModels.Count == 0
                ? UiLocalization.Current["Settings.AiTranslation.ModelsEmpty"]
                : UiLocalization.Current.Format("Settings.AiTranslation.ModelsFetched", AvailableModels.Count);
        }
    }

    private async Task RefreshBalanceCoreAsync()
    {
        if (!ShowBalance)
        {
            return;
        }

        var result = await _ai.GetBalanceAsync(BuildProbeConfig());
        if (!result.Success)
        {
            BalanceText = string.IsNullOrWhiteSpace(result.Error)
                ? UiLocalization.Current["Settings.AiTranslation.BalanceFailed"]
                : result.Error;
            return;
        }

        var localization = UiLocalization.Current;
        var lines = result.BalanceInfos
            .Select(info => localization.Format(
                "Settings.AiTranslation.BalanceLine",
                info.Currency,
                info.TotalBalance,
                info.GrantedBalance,
                info.ToppedUpBalance))
            .ToArray();

        var text = lines.Length == 0
            ? localization["Settings.AiTranslation.BalanceEmpty"]
            : string.Join("；", lines);
        if (!result.IsAvailable)
        {
            text = $"{text} · {localization["Settings.AiTranslation.BalanceInsufficient"]}";
        }

        BalanceText = text;
    }

    /// <summary>页面加载后静默补齐模型列表和余额；失败时不打断用户。</summary>
    private async Task RefreshProbeDataQuietlyAsync()
    {
        var provider = ProviderType;
        try
        {
            var models = await _ai.ListModelsAsync(BuildProbeConfig());
            if (provider == ProviderType && models.Success)
            {
                SetAvailableModels(models.Models.Select(model => model.Id));
            }

            if (provider == ProviderType && ShowBalance)
            {
                await RefreshBalanceCoreAsync();
            }
        }
        catch
        {
            // 静默刷新失败；用户仍可手动点击“获取模型”或“刷新”。
        }
    }

    private void SetAvailableModels(IEnumerable<string> models)
    {
        AvailableModels.Clear();
        foreach (var id in models
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AvailableModels.Add(id);
        }

        // 当前模型可能不在服务返回的列表里（自定义/别名），保留它以便下拉框能显示选中值。
        if (!string.IsNullOrWhiteSpace(Model)
            && !AvailableModels.Any(id => id.Equals(Model, StringComparison.OrdinalIgnoreCase)))
        {
            AvailableModels.Insert(0, Model);
        }

        OnPropertyChanged(nameof(HasAvailableModels));
    }

    private void ResetProbeResults()
    {
        AvailableModels.Clear();
        OnPropertyChanged(nameof(HasAvailableModels));
        BalanceText = string.Empty;
    }

    private AIProviderConfig BuildProbeConfig()
    {
        return new AIProviderConfig
        {
            Id = ProviderId,
            Name = ProviderDisplayName,
            Type = ParseProviderType(),
            ApiEndpoint = Endpoint.Trim(),
            ApiKey = ResolveApiKey(),
            Model = Model.Trim(),
            IsEnabled = true,
            MaxTokens = 4000,
            Temperature = 0.2,
            EnableThinking = EnableThinking,
            ThinkingEffort = ThinkingEffort,
            CreatedAt = DateTime.UtcNow
        };
    }

    private bool IsSameProviderAsSaved
        => _loadedProviderType == null
           || string.Equals(ProviderType, _loadedProviderType, StringComparison.Ordinal);

    /// <summary>切换提供商后不复用旧提供商保存的密钥，避免把 A 服务的 Key 发给 B 服务。</summary>
    private string? ResolveApiKey()
        => !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim() : (IsSameProviderAsSaved ? _savedApiKey : null);

    private AIProviderType ParseProviderType()
        => Enum.TryParse<AIProviderType>(ProviderType, out var parsed) ? parsed : AIProviderType.OpenAI;

    private static bool RequiresApiKey(AIProviderType type)
        => type is AIProviderType.OpenAI or AIProviderType.Claude or AIProviderType.DeepSeek;

    private bool ValidateEndpoint()
    {
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            StatusMessage = UiLocalization.Current["Settings.AiTranslation.InvalidEndpoint"];
            return false;
        }

        return true;
    }

    private void UpdateThinkingChoices()
    {
        var provider = ParseProviderType();
        var values = provider switch
        {
            AIProviderType.DeepSeek => new[] { "low", "high", "max" },
            AIProviderType.LocalLLM => Array.Empty<string>(),
            _ => new[] { "low", "medium", "high" }
        };

        ThinkingEfforts.Clear();
        foreach (var value in values)
        {
            var key = value switch
            {
                "low" => "Settings.AiTranslation.ThinkingEffort.Low",
                "high" => "Settings.AiTranslation.ThinkingEffort.High",
                "max" => "Settings.AiTranslation.ThinkingEffort.Max",
                _ => "Settings.AiTranslation.ThinkingEffort.Medium"
            };
            ThinkingEfforts.Add(new AiThinkingEffortChoice(value, UiLocalization.Current[key]));
        }

        var normalized = NormalizeThinkingEffort(provider, ThinkingEffort);
        if (!string.Equals(normalized, ThinkingEffort, StringComparison.Ordinal))
        {
            ThinkingEffort = normalized;
        }

        OnPropertyChanged(nameof(ShowThinking));
        OnPropertyChanged(nameof(ShowThinkingEffort));
    }

    private static string NormalizeThinkingEffort(AIProviderType provider, string? effort)
    {
        var value = (effort ?? string.Empty).Trim().ToLowerInvariant();
        return provider switch
        {
            AIProviderType.DeepSeek => value switch
            {
                "low" or "minimal" => "low",
                "max" or "ultra" => "max",
                _ => "high"
            },
            AIProviderType.LocalLLM => "medium",
            _ => value switch
            {
                "low" or "minimal" => "low",
                "high" or "max" or "xhigh" or "ultra" => "high",
                _ => "medium"
            }
        };
    }

    private void ApplyDefaults(string providerType)
    {
        switch (providerType)
        {
            case nameof(AIProviderType.Claude):
                Endpoint = "https://api.anthropic.com/v1/messages";
                Model = "claude-3-5-sonnet-20241022";
                break;
            case nameof(AIProviderType.DeepSeek):
                Endpoint = "https://api.deepseek.com/chat/completions";
                Model = "deepseek-flash";
                break;
            case nameof(AIProviderType.LocalLLM):
                Endpoint = "http://127.0.0.1:11434/api/generate";
                Model = "llama3.1";
                break;
            case nameof(AIProviderType.Custom):
                if (string.IsNullOrWhiteSpace(Endpoint))
                {
                    Endpoint = "https://api.openai.com/v1/chat/completions";
                    Model = "gpt-4o-mini";
                }
                break;
            default:
                Endpoint = "https://api.openai.com/v1/chat/completions";
                Model = "gpt-4o-mini";
                break;
        }
    }
}
