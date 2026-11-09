using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services.AI;

/// <summary>
/// 模组选项的 AI 翻译：按目标语言缓存原文到译文的映射，未命中项分批请求当前 AI 服务。
/// 译文只写缓存，不修改作者 manifest；更换目标语言后旧语言缓存仍可复用。
/// </summary>
public sealed class OptionTranslationService
{
    public const string DefaultTargetLanguage = "zh-CN";

    private const int MaxBatchItems = 24;
    private const int MaxBatchCharacters = 8_000;
    private const int MaxConcurrentBatches = 3;

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh-CN"] = "Simplified Chinese",
        ["zh-TW"] = "Traditional Chinese",
        ["en-US"] = "English",
        ["ja-JP"] = "Japanese",
        ["ko-KR"] = "Korean",
        ["ru-RU"] = "Russian",
        ["fr-FR"] = "French",
        ["de-DE"] = "German",
        ["es-ES"] = "Spanish"
    };

    private readonly IAIService _ai;
    private readonly SettingsService _settings;
    private readonly StorageSettingsService? _storage;
    private readonly ILogger<OptionTranslationService>? _logger;
    private readonly string? _cacheDirectory;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public OptionTranslationService(
        IAIService ai,
        SettingsService settings,
        StorageSettingsService? storage = null,
        ILogger<OptionTranslationService>? logger = null)
    {
        _ai = ai;
        _settings = settings;
        _storage = storage;
        _logger = logger;
    }

    internal OptionTranslationService(
        IAIService ai,
        SettingsService settings,
        string cacheDirectory,
        ILogger<OptionTranslationService>? logger = null)
        : this(ai, settings, storage: null, logger)
    {
        _cacheDirectory = cacheDirectory;
    }

    public string TargetLanguageCode =>
        _settings.GetSetting(SettingKeys.AiTranslationTargetLanguage, DefaultTargetLanguage);

    public string TargetLanguageName =>
        LanguageNames.GetValueOrDefault(TargetLanguageCode, TargetLanguageCode);

    public async Task<IReadOnlyDictionary<string, string>> GetCachedTranslationsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        var distinct = Normalize(texts);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (distinct.Length == 0)
            return result;

        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            var cache = await LoadCacheAsync(cancellationToken);
            foreach (var source in distinct)
            {
                if (cache.TryGetValue(BuildCacheKey(source), out var translated))
                    result[source] = translated;
            }
        }
        finally
        {
            _cacheGate.Release();
        }

        return result;
    }

    public async Task<OptionTranslationResult> TranslateAsync(
        IEnumerable<string> texts,
        bool forceRefresh = false,
        IProgress<OptionTranslationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var distinct = Normalize(texts);
        if (distinct.Length == 0)
            return new OptionTranslationResult(
                new Dictionary<string, string>(StringComparer.Ordinal), 0, 0, 0, 0, 0);

        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            var cache = await LoadCacheAsync(cancellationToken);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var pending = new List<(string Source, string Key)>();
            var localHits = 0;

            foreach (var source in distinct)
            {
                var key = BuildCacheKey(source);
                if (!forceRefresh && cache.TryGetValue(key, out var cached))
                {
                    result[source] = cached;
                    localHits++;
                    progress?.Report(new OptionTranslationProgress(
                        source, cached, result.Count, distinct.Length, true));
                    continue;
                }

                pending.Add((source, key));
            }

            if (pending.Count == 0)
                return new OptionTranslationResult(result, localHits, 0, 0, 0, 0);

            var language = TargetLanguageName;
            var batches = BuildBatches(pending);
            var apiCount = 0;
            var tokensUsed = 0;
            var inputTokens = 0;
            var cachedInputTokens = 0;

            for (var offset = 0; offset < batches.Count; offset += MaxConcurrentBatches)
            {
                var wave = batches.Skip(offset).Take(MaxConcurrentBatches).ToArray();
                var tasks = wave
                    .Select(batch => _ai.TranslateBatchAsync(
                        batch.Select(item => item.Source).ToArray(), language, cancellationToken))
                    .ToArray();

                for (var i = 0; i < tasks.Length; i++)
                {
                    var batchResult = await tasks[i];
                    if (!batchResult.Success)
                        throw new InvalidOperationException(batchResult.Error ?? "AI 翻译失败");

                    tokensUsed += batchResult.TokensUsed;
                    inputTokens += batchResult.InputTokens;
                    cachedInputTokens += batchResult.CachedInputTokens;
                    foreach (var (source, key) in wave[i])
                    {
                        if (!batchResult.Translations.TryGetValue(source, out var translation))
                            continue;

                        result[source] = translation;
                        cache[key] = translation;
                        apiCount++;
                        progress?.Report(new OptionTranslationProgress(
                            source, translation, result.Count, distinct.Length, false));
                    }

                    await SaveCacheAsync(cache, cancellationToken);
                }
            }

            if (inputTokens > 0)
            {
                _logger?.LogInformation(
                    "AI 翻译完成：本地缓存 {LocalHits} 条，API {ApiTexts} 条，输入 {InputTokens} tokens（缓存命中 {CachedTokens}，{CacheRate:P1}）",
                    localHits, apiCount, inputTokens, cachedInputTokens,
                    (double)cachedInputTokens / inputTokens);
            }

            return new OptionTranslationResult(
                result, localHits, apiCount, tokensUsed, inputTokens, cachedInputTokens);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static string[] Normalize(IEnumerable<string> texts) => texts
        .Where(text => !string.IsNullOrWhiteSpace(text))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private string BuildCacheKey(string source)
    {
        var material = $"{TargetLanguageCode}\n{source}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static List<List<(string Source, string Key)>> BuildBatches(List<(string Source, string Key)> pending)
    {
        var batches = new List<List<(string Source, string Key)>>();
        var current = new List<(string Source, string Key)>(MaxBatchItems);
        var characters = 0;

        foreach (var item in pending)
        {
            if (current.Count > 0
                && (current.Count >= MaxBatchItems || characters + item.Source.Length > MaxBatchCharacters))
            {
                batches.Add(current);
                current = new List<(string Source, string Key)>(MaxBatchItems);
                characters = 0;
            }

            current.Add(item);
            characters += item.Source.Length;
        }

        if (current.Count > 0)
            batches.Add(current);

        return batches;
    }

    private string CachePath => Path.Combine(
        _cacheDirectory
            ?? Path.Combine(
                _storage?.Current.AppDataPath ?? RuntimeDataPaths.GetDefaultApplicationDataPath(),
                "AiTranslation"),
        "cache.json");

    private async Task<Dictionary<string, string>> LoadCacheAsync(CancellationToken cancellationToken)
    {
        var path = CachePath;
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                       stream, cancellationToken: cancellationToken)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger?.LogWarning(ex, "AI 翻译缓存读取失败，按空缓存继续");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task SaveCacheAsync(Dictionary<string, string> cache, CancellationToken cancellationToken)
    {
        var path = CachePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream, cache, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
            }

            File.Move(tempPath, path, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}

public sealed record OptionTranslationResult(
    IReadOnlyDictionary<string, string> Translations,
    int LocalCacheHits,
    int ApiTextCount,
    int TokensUsed,
    int InputTokens,
    int CachedInputTokens);

public sealed record OptionTranslationProgress(
    string Source,
    string Translation,
    int Completed,
    int Total,
    bool FromLocalCache);
