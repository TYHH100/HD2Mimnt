using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace HD2Mimnt.Application.Services;

public enum SecretMatchKind
{
    None = 0,

    /// <summary>命中了当前已配置的真实密钥——视为窃取/外传，立即终止插件。</summary>
    KnownSecret = 1,

    /// <summary>命中常见凭据格式，但无法确认真实性——拒绝请求并脱敏，不直接终止。</summary>
    CredentialPattern = 2
}

public readonly record struct SecretScanResult(SecretMatchKind Kind, string? Reason)
{
    public bool IsViolation => Kind != SecretMatchKind.None;

    public bool IsFatal => Kind == SecretMatchKind.KnownSecret;

    public static readonly SecretScanResult Clean = new(SecretMatchKind.None, null);
}

public interface IPluginSecretGuard
{
    /// <summary>登记一个真实密钥（长度不足 8 的忽略，避免误伤普通文本）。</summary>
    void Register(string? secret);

    SecretScanResult Scan(string? text);

    /// <summary>把文本中出现的密钥与凭据模式替换为 ***。</summary>
    string Redact(string? text);
}

/// <summary>
/// 插件边界上的密钥守卫。
///
/// 职责：
/// 1. 记住主程序当前持有的真实密钥，用于精确匹配（优先级最高）；
/// 2. 识别常见凭据格式，作为真实密钥尚未登记时的兜底；
/// 3. 对日志、命令结果、AI 提示词这些“插件可控文本”做统一扫描与脱敏。
///
/// 边界：本类只做检测与脱敏，真正“掐死”插件由 PluginHostManager 执行。
/// </summary>
public sealed class PluginSecretGuard : IPluginSecretGuard
{
    private const int MinSecretLength = 8;

    private readonly ConcurrentDictionary<string, byte> _knownSecrets = new(StringComparer.Ordinal);

    private static readonly (Regex Pattern, string Label)[] CredentialPatterns =
    {
        (new Regex(@"sk-ant-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled), "Anthropic API Key"),
        (new Regex(@"sk-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled), "OpenAI 风格 API Key"),
        (new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled), "AWS Access Key"),
        (new Regex(@"gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "GitHub Token"),
        (new Regex(@"Bearer\s+[A-Za-z0-9\-._~+/]{20,}=*", RegexOptions.Compiled), "Bearer Token")
    };

    public void Register(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinSecretLength)
        {
            return;
        }

        _knownSecrets.TryAdd(secret, 0);
    }

    public SecretScanResult Scan(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return SecretScanResult.Clean;
        }

        foreach (var secret in _knownSecrets.Keys)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
            {
                return new SecretScanResult(SecretMatchKind.KnownSecret, "文本包含当前配置的 API 密钥");
            }
        }

        foreach (var (pattern, label) in CredentialPatterns)
        {
            if (pattern.IsMatch(text))
            {
                return new SecretScanResult(SecretMatchKind.CredentialPattern, $"文本包含疑似凭据：{label}");
            }
        }

        return SecretScanResult.Clean;
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var secret in _knownSecrets.Keys)
        {
            if (result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        foreach (var (pattern, _) in CredentialPatterns)
        {
            result = pattern.Replace(result, "***");
        }

        return result;
    }
}
