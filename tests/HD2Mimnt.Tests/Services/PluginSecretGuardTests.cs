using HD2Mimnt.Application.Services;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public class PluginSecretGuardTests
{
    private const string RealKey = "sk-live-abcdef1234567890abcdef";

    [Fact]
    public void Scan_TextContainingRegisteredSecret_IsFatal()
    {
        var guard = new PluginSecretGuard();
        guard.Register(RealKey);

        var result = guard.Scan($"please log this {RealKey} for me");

        Assert.True(result.IsViolation);
        Assert.True(result.IsFatal);
        Assert.Equal(SecretMatchKind.KnownSecret, result.Kind);
    }

    [Fact]
    public void Scan_TextWithUnknownCredentialPattern_IsViolationButNotFatal()
    {
        var guard = new PluginSecretGuard();

        var result = guard.Scan("token=ghp_0123456789abcdefghijABCDEF");

        Assert.True(result.IsViolation);
        Assert.False(result.IsFatal);
        Assert.Equal(SecretMatchKind.CredentialPattern, result.Kind);
    }

    [Fact]
    public void Scan_PlainModDescription_IsClean()
    {
        var guard = new PluginSecretGuard();

        Assert.False(guard.Scan("This mod adds a new stratagem and two weapons.").IsViolation);
    }

    [Fact]
    public void Register_ShortValue_IsIgnoredToAvoidFalsePositives()
    {
        var guard = new PluginSecretGuard();
        guard.Register("abc");

        Assert.False(guard.Scan("abc appears in normal text").IsViolation);
    }

    [Fact]
    public void Redact_RemovesRegisteredSecretAndPatterns()
    {
        var guard = new PluginSecretGuard();
        guard.Register(RealKey);

        var redacted = guard.Redact($"key={RealKey}; other=sk-abcdefghij0123456789");

        Assert.DoesNotContain(RealKey, redacted);
        Assert.DoesNotContain("sk-abcdefghij0123456789", redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public void Scan_Base64EncodedKnownSecret_IsFatal()
    {
        var guard = new PluginSecretGuard();
        guard.Register(RealKey);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(RealKey));

        var result = guard.Scan($"here is the payload: {encoded}");

        Assert.True(result.IsFatal);
        Assert.Equal(SecretMatchKind.KnownSecret, result.Kind);
    }

    [Fact]
    public void Scan_HexEncodedKnownSecret_IsFatal()
    {
        var guard = new PluginSecretGuard();
        guard.Register(RealKey);
        var encoded = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(RealKey)).ToLowerInvariant();

        var result = guard.Scan($"dump: {encoded}");

        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Scan_Base64EncodedUnknownCredentialPattern_IsViolation()
    {
        var guard = new PluginSecretGuard();
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("sk-proj-0123456789abcdef"));

        var result = guard.Scan($"payload: {encoded}");

        Assert.True(result.IsViolation);
        Assert.False(result.IsFatal);
        Assert.Equal(SecretMatchKind.CredentialPattern, result.Kind);
    }

    [Fact]
    public void Redact_StripsAllEncodedSecretTokens()
    {
        var guard = new PluginSecretGuard();
        guard.Register(RealKey);
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(RealKey));
        var hex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(RealKey)).ToLowerInvariant();

        var redacted = guard.Redact($"a={b64} b={hex}");

        Assert.DoesNotContain(b64, redacted);
        Assert.DoesNotContain(hex, redacted);
        Assert.Equal(2, redacted.Split("***").Length - 1);
    }

    [Fact]
    public void Scan_RegisteredBase64VariantCaughtEvenWhenTokenIsUrlSafe()
    {
        var guard = new PluginSecretGuard();
        var bytes = System.Text.Encoding.UTF8.GetBytes("sk-proj-abcdefgh12345678");
        var standard = Convert.ToBase64String(bytes);
        var urlSafe = standard.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        guard.Register("sk-proj-abcdefgh12345678");

        Assert.True(guard.Scan($"token: {urlSafe}").IsViolation);
    }

    [Fact]
    public void Scan_Base64GarbageThatDecodesToNothingSensitive_IsClean()
    {
        var guard = new PluginSecretGuard();
        guard.Register(RealKey);

        // 长 base64 串解码后是普通文字，不应误报
        Assert.False(guard.Scan("checksum: SGVsbG8gV29ybGQgdGhpcyBpcyBub3QgYSBzZWNyZXQ=").IsViolation);
    }
}
