using System.Text.Json;
using HD2Mimnt.Core.IPC;
using Xunit;

namespace HD2Mimnt.Tests.Cli;

/// <summary>CLI 管道帧协议：类型标记、控制帧与管道名派生。</summary>
public sealed class CliPipeProtocolTests
{
    [Fact]
    public void ControlFrame_SerializesWithTypeTag()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CliPipeControl("shutdown"),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var document = JsonDocument.Parse(payload);
        Assert.Equal("control", document.RootElement.GetProperty("t").GetString());
        Assert.Equal("shutdown", document.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void ProgressFrame_SerializesWithTypeTag()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CliPipeProgress("部署", 50.0, "某模组", "进行中"),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var document = JsonDocument.Parse(payload);
        Assert.Equal("progress", document.RootElement.GetProperty("t").GetString());
        Assert.Equal(50.0, document.RootElement.GetProperty("percent").GetDouble());
    }

    [Fact]
    public void PromptResponseFrame_SerializesWithTypeTag()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CliPipePromptResponse("p1", null, Confirmed: false, Cancelled: true),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var document = JsonDocument.Parse(payload);
        Assert.Equal("prompt-response", document.RootElement.GetProperty("t").GetString());
    }

    [Fact]
    public void AgentPipeName_IsDerivedFromUiPipeName()
    {
        var ui = CliPipeProtocol.GetUiPipeName();
        var agent = CliPipeProtocol.GetAgentPipeName();
        Assert.StartsWith(ui, agent, StringComparison.Ordinal);
        Assert.NotEqual(ui, agent);
        Assert.StartsWith("HD2Mimnt-Cli-", ui, StringComparison.Ordinal);
    }

    [Fact]
    public void PipeProbe_ReturnsFalseForMissingPipe()
    {
        // 不存在的管道名必须立即返回 false（亚毫秒），这是"服务端未运行"判定不付出秒级等待的关键。
        Assert.False(PipeProbe.Exists("HD2Mimnt-CLI-Test-Nonexistent-Pipe-XYZ"));
    }

    [Fact]
    public void PipeProbe_ReturnsTrueWhileServerListens()
    {
        // 命名管道监听期间 GetFileAttributes 应成功；断开后立即消失。
        Assert.False(PipeProbe.Exists("HD2Mimnt-CLI-Test-Probe-Pipe-XYZ"));
        using var server = new System.IO.Pipes.NamedPipeServerStream("HD2Mimnt-CLI-Test-Probe-Pipe-XYZ",
            System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        Assert.True(PipeProbe.Exists("HD2Mimnt-CLI-Test-Probe-Pipe-XYZ"));
        server.Dispose();
        Assert.False(PipeProbe.Exists("HD2Mimnt-CLI-Test-Probe-Pipe-XYZ"));
    }
}
