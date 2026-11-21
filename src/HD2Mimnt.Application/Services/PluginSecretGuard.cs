using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
/// 3. 对日志、命令结果、AI 提示词这些“插件可控文本”做统一扫描与脱敏；
/// 4. 对文本中的 base64/hex 编码 token 解码后二次匹配——明文子串匹配
///    对 “c2stYW50...” 这类编码外传形同虚设，这是最后一道闸。
///
/// 边界：本类只做检测与脱敏，真正“掐死”插件由 PluginHostManager 执行；
/// 跨消息分段外传不在单条扫描范围内（需有状态聚合，误报代价高，依赖调用方按会话聚合）。
/// </summary>
public sealed class PluginSecretGuard : IPluginSecretGuard
{
    private const int MinSecretLength = 8;

    /// <summary>候选编码 token 的最短长度：8 字节密钥 base64 后 ≥ 12 字符、hex 后 16 字符。</summary>
    private const int MinEncodedTokenLength = 12;
    /// <summary>超长 token 不做解码（正常出口文本不会携带 4KB 级编码串，控制扫描成本）。</summary>
    private const int MaxEncodedTokenLength = 4096;

    private readonly ConcurrentDictionary<string, byte> _knownSecrets = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (Regex Pattern, string Label)[] CredentialPatterns =
    {
        (new Regex(@"sk-ant-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled), "Anthropic API Key"),
        (new Regex(@"sk-[A-Za-z0-9_\-]{16,}", RegexOptions.Compiled), "OpenAI 风格 API Key"),
        (new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled), "AWS Access Key"),
        (new Regex(@"gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "GitHub Token"),
        (new Regex(@"Bearer\s+[A-Za-z0-9\-._~+/]{20,}=*", RegexOptions.Compiled), "Bearer Token")
    };

    /// <summary>文本中的候选编码 token：base64/base64url/hex 字符集且长度落在扫描区间。</summary>
    private static readonly Regex EncodedTokenPattern =
        new(@"[A-Za-z0-9+/\-_]{12,}", RegexOptions.Compiled);

    private static readonly Regex HexTokenPattern = new(@"^[0-9a-fA-F]+$", RegexOptions.Compiled);

    public void Register(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinSecretLength)
        {
            return;
        }

        _knownSecrets.TryAdd(secret, 0);
        // 同步登记 base64 与 hex 变体：出口文本里密钥以编码形式出现时仍能精确命中
        _knownSecrets.TryAdd(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), 0);
        _knownSecrets.TryAdd(Convert.ToHexString(Encoding.UTF8.GetBytes(secret)).ToLowerInvariant(), 0);
    }

    public SecretScanResult Scan(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return SecretScanResult.Clean;
        }

        foreach (var secret in _knownSecrets.Keys)
        {
            if (text.Contains(secret, StringComparison.OrdinalIgnoreCase))
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

        return ScanEncoded(text, out _);
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
            if (result.Contains(secret, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(secret, "***", StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var (pattern, _) in CredentialPatterns)
        {
            result = pattern.Replace(result, "***");
        }

        // 编码变体命中的 token 整体脱敏；逐个替换直到收敛（每次替换消掉一个 token）
        while (ScanEncoded(result, out var offendingToken).IsViolation && offendingToken is not null)
        {
            result = result.Replace(offendingToken, "***", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// 对文本里的 base64/hex 候选 token 解码后二次匹配。
    /// <paramref name="offendingToken"/> 返回命中的原始 token，供 Redact 整体替换。
    /// </summary>
    private SecretScanResult ScanEncoded(string text, out string? offendingToken)
    {
        offendingToken = null;
        foreach (Match match in EncodedTokenPattern.Matches(text))
        {
            var token = match.Value;
            if (token.Length > MaxEncodedTokenLength) continue;

            foreach (var decoded in DecodeCandidates(token))
            {
                if (decoded is null || decoded.Length < MinSecretLength) continue;

                foreach (var secret in _knownSecrets.Keys)
                {
                    if (decoded.Contains(secret, StringComparison.OrdinalIgnoreCase))
                    {
                        offendingToken = token;
                        return new SecretScanResult(SecretMatchKind.KnownSecret, "文本包含编码形式的当前 API 密钥");
                    }
                }

                foreach (var (pattern, label) in CredentialPatterns)
                {
                    if (pattern.IsMatch(decoded))
                    {
                        offendingToken = token;
                        return new SecretScanResult(SecretMatchKind.CredentialPattern, $"文本包含疑似编码凭据：{label}");
                    }
                }
            }
        }

        return SecretScanResult.Clean;
    }

    /// <summary>枚举 token 的解码结果：标准/URL-safe base64 与 hex（全 hex 且偶数长度时）。</summary>
    private static IEnumerable<string?> DecodeCandidates(string token)
    {
        // hex：全十六进制字符且偶数长度
        if (token.Length % 2 == 0 && token.Length >= MinSecretLength * 2 && HexTokenPattern.IsMatch(token))
        {
            yield return Encoding.UTF8.GetString(Convert.FromHexString(token));
        }

        // base64：URL-safe 字符集还原为标准字符集并补齐 padding
        var standard = token.Replace('-', '+').Replace('_', '/');
        var padding = (4 - standard.Length % 4) % 4;
        if (standard.Length % 4 == 1) yield break; // 不可能是合法 base64
        standard += new string('=', padding);
        var buffer = new byte[(standard.Length / 4) * 3];
        if (Convert.TryFromBase64String(standard, buffer, out var written) && written > 0)
        {
            yield return Encoding.UTF8.GetString(buffer, 0, written);
        }
    }
}
