using System.Text.Json;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Infrastructure.ModelConversion.Mmd;

namespace HD2Mimnt.Worker;

/// <summary>
/// 在独立进程检查/转换 MMD 原始资源包：读取压缩包、解码 90 骨骼动作与媒体都在子进程完成，
/// 主程序只负责结果入库与配置开关。
/// </summary>
public sealed class MmdAdaptationWorkerTask : IWorkerTask
{
    public async Task ExecuteAsync(IWorkerTaskHost host, ExecuteWorkerCommand command,
        CancellationToken cancellationToken)
    {
        var arguments = command.Arguments;
        var action = Required(arguments, "Action");
        if (action is not ("inspect" or "convert"))
            throw new ArgumentException("MMD 适配动作无效：" + action);
        var source = Required(arguments, "Source");
        var option = Optional(arguments, "SourceOption");
        var idlePlayback = Optional(arguments, "IdlePlayback");
        var metadata = Optional(arguments, "Metadata") is { } json
            ? JsonSerializer.Deserialize<MmdSourceMetadata>(json)
            : null;

        var reporter = new WorkerProgressReporter(host, cancellationToken);
        var progress = new InlineProgress<string>(message => reporter.Report(0, message, message: message));
        string payload;
        string message;
        try
        {
            var (plan, inspection) = MmdAdapter.Inspect(source, option, metadata, idlePlayback,
                progress.Report, cancellationToken);
            if (action == "inspect")
            {
                message = inspection.Name;
                payload = JsonSerializer.Serialize(inspection);
            }
            else
            {
                var output = Required(arguments, "OutputDirectory");
                var result = MmdAdapter.Convert(plan, output, progress.Report, cancellationToken);
                message = result.Title;
                payload = JsonSerializer.Serialize(result);
            }
        }
        finally
        {
            try { reporter.Flush(); } catch { }
        }

        await host.SendCompletedAsync(true, message, cancellationToken: cancellationToken, data: payload);
    }

    private static string Required(IReadOnlyDictionary<string, string> arguments, string key)
        => arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("MMD 适配缺少参数：" + key);

    private static string? Optional(IReadOnlyDictionary<string, string> arguments, string key)
        => arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
