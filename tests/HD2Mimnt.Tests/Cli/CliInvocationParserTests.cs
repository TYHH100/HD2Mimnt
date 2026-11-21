using HD2Mimnt.Cli;
using Xunit;

namespace HD2Mimnt.Tests.Cli;

/// <summary>GNU 常规选项解析器：双形式、任意位置、短选项、-- 字面量与未知选项透传。</summary>
public sealed class CliInvocationParserTests
{
    [Fact]
    public void Parse_LongAndShortOptions_AreEquivalent()
    {
        var longForm = CliInvocationParser.Parse(["mod", "delete", "某模组", "--yes"]);
        var shortForm = CliInvocationParser.Parse(["mod", "delete", "某模组", "-y"]);
        Assert.True(longForm.Yes);
        Assert.True(shortForm.Yes);
        Assert.Equal(longForm.Tokens, shortForm.Tokens);
    }

    [Fact]
    public void Parse_OptionsAnywhere_AreStrippedFromTokens()
    {
        var invocation = CliInvocationParser.Parse(["--json", "mod", "list", "--limit", "20", "--yes"]);
        Assert.True(invocation.Json);
        Assert.True(invocation.Yes);
        Assert.Equal(new[] { "mod", "list", "--limit", "20" }, invocation.Tokens);
    }

    [Fact]
    public void Parse_EqualsForm_IsSupported()
    {
        var invocation = CliInvocationParser.Parse(["deploy", "--profile=季票", "--timeout=30"]);
        Assert.Equal(30, invocation.TimeoutSeconds);
        // 全局解析器只拆自己认识的选项；子命令选项的 --flag=值 双形式由 CliArgs 在 handler 内解析。
        Assert.Equal(new[] { "deploy", "--profile=季票" }, invocation.Tokens);
    }

    [Fact]
    public void Parse_SeparatedValueForm_ConsumesNextToken()
    {
        var invocation = CliInvocationParser.Parse(["deploy", "--timeout", "120"]);
        Assert.Equal(120, invocation.TimeoutSeconds);
        Assert.Equal(new[] { "deploy" }, invocation.Tokens);
    }

    [Fact]
    public void Parse_DoubleDash_MakesRestLiteral()
    {
        var invocation = CliInvocationParser.Parse(["--json", "--", "mod", "import", "--yes", "--weird"]);
        Assert.True(invocation.Json);
        Assert.False(invocation.Yes);
        // -- 之后全部按字面位置参数，不再解析为全局选项。
        Assert.Equal(new[] { "mod", "import", "--yes", "--weird" }, invocation.Tokens);
    }

    [Fact]
    public void Parse_HelpAndVersion_Requests()
    {
        Assert.True(CliInvocationParser.Parse(["--help"]).HelpRequested);
        Assert.True(CliInvocationParser.Parse(["-h"]).HelpRequested);
        Assert.True(CliInvocationParser.Parse(["--version"]).VersionRequested);
        var withCommand = CliInvocationParser.Parse(["mod", "import", "-h"]);
        Assert.True(withCommand.HelpRequested);
        Assert.Equal(new[] { "mod", "import" }, withCommand.Tokens);
    }

    [Fact]
    public void Parse_StreamAndAsync_Flags()
    {
        var invocation = CliInvocationParser.Parse(["--stream", "--async", "deploy"]);
        Assert.True(invocation.Stream);
        Assert.True(invocation.Async);
    }

    [Fact]
    public void Parse_InvalidTimeout_Throws()
    {
        Assert.Throws<ArgumentException>(() => CliInvocationParser.Parse(["--timeout", "abc"]));
        Assert.Throws<ArgumentException>(() => CliInvocationParser.Parse(["--timeout=0"]));
    }
}
