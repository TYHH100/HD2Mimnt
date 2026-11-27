using System.Text.Json;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Infrastructure.ModelConversion;

namespace HD2Mimnt.Worker;

public sealed class ModelToIasConversionWorkerTask : IWorkerTask
{
    public async Task ExecuteAsync(IWorkerTaskHost host, ExecuteWorkerCommand command,
        CancellationToken cancellationToken)
    {
        var arguments = command.Arguments;
        var branches = Optional(arguments, "Branches") is { } branchJson
            ? JsonSerializer.Deserialize<List<ModelToIasBranch>>(branchJson)
            : null;
        IasBinary.Require(branches is { Count: > 0 }, "普通模型转 IAS 缺少选项分支");
        var request = new IasModelToIasRequest(
            Required(arguments, "Seed"), Required(arguments, "Name"),
            Optional(arguments, "Author") ?? string.Empty,
            Optional(arguments, "Description") ?? string.Empty,
            Required(arguments, "ModPath"), Required(arguments, "GameDataRoot"),
            Required(arguments, "OutputDirectory"), branches!,
            Optional(arguments, "ThumbnailPath"), Optional(arguments, "ManifestIconPath"));
        var reporter = new WorkerProgressReporter(host, cancellationToken);
        var progress = new InlineProgress<IasModelToIasProgress>(value =>
            reporter.Report(value.Percent, value.Stage, message: value.Message));
        IasModelToIasResult result;
        try
        {
            result = await IasModelToIasConverter.BuildAsync(request, progress, cancellationToken);
        }
        finally
        {
            try { reporter.Flush(); } catch { }
        }
        await host.SendCompletedAsync(true, result.Name, cancellationToken: cancellationToken,
            data: JsonSerializer.Serialize(result));
    }

    private static string Required(IReadOnlyDictionary<string, string> arguments, string key)
        => arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("普通模型转 IAS 缺少参数：" + key);

    private static string? Optional(IReadOnlyDictionary<string, string> arguments, string key)
        => arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
