using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.AI;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public sealed record AiTargetLanguageChoice(string Code, string DisplayName);

/// <summary>
/// 设置页“AI 翻译”分类：复用 AIProviderConfigs 里的单一活动配置，密钥仍由数据层用 DPAPI 加密。
/// </summary>
public partial class AiTranslationSettingsViewModel : ObservableObject
{
    private const string ProviderId = "default";

    private readonly IAIService _ai;
    private readonly IAIProviderConfigRepository _repository;
    private readonly SettingsService _settings;
    private bool _loading;

    [ObservableProperty] private string _providerType = nameof(AIProviderType.OpenAI);
    [ObservableProperty] private string _endpoint = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private bool _hasSavedApiKey;
    [ObservableProperty] private AiTargetLanguageChoice _selectedTargetLanguage = TargetLanguages[0];
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public static IReadOnlyList<string> ProviderTypes { get; } =
        [nameof(AIProviderType.OpenAI), nameof(AIProviderType.Claude), nameof(AIProviderType.LocalLLM), nameof(AIProviderType.Custom)];

    public static IReadOnlyList<AiTargetLanguageChoice> TargetLanguages { get; } = new[]
    {
        new AiTargetLanguageChoice("zh-CN", "简体中文"),
        new AiTargetLanguageChoice("zh-TW", "繁體中文"),
        new AiTargetLanguageChoice("en-US", "English"),
        new AiTargetLanguageChoice("ja-JP", "日本語"),
        new AiTargetLanguageChoice("ko-KR", "한국어")
    };

    public string ApiKeyHint => HasSavedApiKey
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
    }

    partial void OnProviderTypeChanged(string value)
    {
        if (_loading) return;
        ApplyDefaults(value);
    }

    partial void OnHasSavedApiKeyChanged(bool value) => OnPropertyChanged(nameof(ApiKeyHint));

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
                HasSavedApiKey = !string.IsNullOrWhiteSpace(config.ApiKey);
            }
            else
            {
                ApplyDefaults(ProviderType);
                HasSavedApiKey = false;
            }

            ApiKey = string.Empty;
            var code = _settings.GetSetting(
                SettingKeys.AiTranslationTargetLanguage, OptionTranslationService.DefaultTargetLanguage);
            SelectedTargetLanguage = TargetLanguages.FirstOrDefault(
                language => language.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? TargetLanguages[0];
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

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            StatusMessage = UiLocalization.Current["Settings.AiTranslation.InvalidEndpoint"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            StatusMessage = UiLocalization.Current["Settings.AiTranslation.InvalidModel"];
            return false;
        }

        var type = Enum.TryParse<AIProviderType>(ProviderType, out var parsed)
            ? parsed
            : AIProviderType.OpenAI;
        if (type is AIProviderType.OpenAI or AIProviderType.Claude
            && string.IsNullOrWhiteSpace(ApiKey) && !HasSavedApiKey)
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
                    Name = "AI 翻译",
                    Type = type,
                    ApiEndpoint = endpoint.ToString(),
                    ApiKey = ApiKey.Trim(),
                    Model = Model.Trim(),
                    IsEnabled = true,
                    MaxTokens = 4000,
                    Temperature = 0.2,
                    CreatedAt = DateTime.UtcNow
                };
                await _repository.AddAsync(config, cancellationToken);
            }
            else
            {
                existing.Name = "AI 翻译";
                existing.Type = type;
                existing.ApiEndpoint = endpoint.ToString();
                existing.Model = Model.Trim();
                existing.IsEnabled = true;
                existing.Temperature = 0.2;
                if (!string.IsNullOrWhiteSpace(ApiKey))
                    existing.ApiKey = ApiKey.Trim();
                await _repository.UpdateAsync(existing, cancellationToken);
            }

            await _repository.SetActiveProviderAsync(ProviderId, cancellationToken);
            await _settings.SetSettingAsync(
                SettingKeys.AiTranslationTargetLanguage, SelectedTargetLanguage.Code);

            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                HasSavedApiKey = true;
                ApiKey = string.Empty;
            }

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
            var ok = await _ai.TestConnectionAsync();
            StatusMessage = UiLocalization.Current[
                ok ? "Settings.AiTranslation.TestSucceeded" : "Settings.AiTranslation.TestFailed"];
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
            _loading = false;
            ApplyDefaults(ProviderType);
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

    private void ApplyDefaults(string providerType)
    {
        switch (providerType)
        {
            case nameof(AIProviderType.Claude):
                Endpoint = "https://api.anthropic.com/v1/messages";
                Model = "claude-3-5-sonnet-20241022";
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
