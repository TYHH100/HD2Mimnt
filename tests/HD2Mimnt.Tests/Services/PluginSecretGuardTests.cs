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
}
