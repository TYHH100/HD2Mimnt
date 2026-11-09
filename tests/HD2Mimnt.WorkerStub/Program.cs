using System.IO.Pipes;
using System.Text.Json;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.WorkerStub;

/// <summary>
/// Worker 进程替身：只实现管道握手，便于测试管理器对崩溃、挂起和取消的判定。
/// 行为来自 Command 的 <c>Arguments["Behavior"]</c>：crash / hang / graceful / complete。
/// </summary>
internal static class Program
{
    private const string ConnectionArgument = "--pipe-name";

    private static async Task<int> Main(string[] args)
    {
        var tracePath = Environment.GetEnvironmentVariable("HD2MIMNT_STUB_TRACE");
        void Trace(string message)
        {
            if (string.IsNullOrEmpty(tracePath)) return;
            try { File.AppendAllText(tracePath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}"); } catch { }
        }

        Trace("started");
        var pipeName = ReadArgument(args, ConnectionArgument);
        if (string.IsNullOrEmpty(pipeName)) return 2;

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000);
        }
        catch
        {
            return 3;
        }

        Trace("connected");
        var command = await ReceiveCommandAsync(pipe);
        var behavior = command?.Arguments.GetValueOrDefault("Behavior") ?? "complete";
        Trace($"command:{behavior}");

        switch (behavior)
        {
            case "crash":
                // 不写终态直接退出：管理器必须按“管道关闭 + 进程退出”判为崩溃。
                Console.Error.WriteLine("stub worker intentionally crashed");
                return 4;

            case "hang":
                // 永不响应 Cancel，验证管理器宽限期后强制结束。
                Trace("hanging");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;

            case "graceful":
                Trace("waiting cancel");
                if (await WaitForCancelAsync(pipe))
                {
                    Trace("cancel received");
                    await SendAsync(pipe, "Cancelled", null);
                }
                return 0;

            default:
                await SendAsync(pipe, "Progress", JsonSerializer.Serialize(new WorkerProgressResponse
                {
                    Progress = 50, Stage = "Stub", Message = "halfway"
                }));
                await SendAsync(pipe, "Completed", JsonSerializer.Serialize(new WorkerCompletedResponse
                {
                    Success = true, Result = "stub done"
                }));
                return 0;
        }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == name) return args[index + 1];
        }
        return null;
    }

    private static async Task<ExecuteWorkerCommand?> ReceiveCommandAsync(Stream stream)
    {
        var json = await PipeFrame.ReceiveAsync(stream, CancellationToken.None);
        if (json is null) return null;
        var message = JsonSerializer.Deserialize<WorkerMessage>(json);
        if (message is null || message.MessageType != "Command") return null;
        return JsonSerializer.Deserialize<ExecuteWorkerCommand>(message.Payload ?? "{}");
    }

    private static async Task<bool> WaitForCancelAsync(Stream stream)
    {
        var json = await PipeFrame.ReceiveAsync(stream, CancellationToken.None);
        if (json is null) return false;
        var message = JsonSerializer.Deserialize<WorkerMessage>(json);
        return message?.MessageType == "Cancel";
    }

    private static Task SendAsync(Stream stream, string messageType, string? payload)
        => PipeFrame.SendAsync(stream, JsonSerializer.Serialize(new WorkerMessage
        {
            MessageType = messageType,
            Payload = payload
        }), CancellationToken.None);
}