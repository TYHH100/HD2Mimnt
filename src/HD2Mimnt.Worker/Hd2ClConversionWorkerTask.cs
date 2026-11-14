using System.Text.Json;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.Infrastructure.ModelConversion;

namespace HD2Mimnt.Worker;

/// <summary>
/// 在独立进程执行 HD2CL 转换的扫描、编译与打包。
/// 主程序只负责入库和配置开关，转换期间的高 CPU 与大内存都停留在子进程，任务结束后随进程释放。
/// </summary>
public sealed class Hd2ClConversionWorkerTask : IWorkerTask
{
    public async Task ExecuteAsync(IWorkerTaskHost host, ExecuteWorkerCommand command,
        CancellationToken cancellationToken)
    {
        var arguments = command.Arguments;
        var components = Optional(arguments, "Components")?
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var optionGroups = Optional(arguments, "OptionGroups") is { } optionJson
            ? JsonSerializer.Deserialize<List<Hd2ClOptionGroup>>(optionJson)
            : null;
        var request = new Hd2ClArtifactRequest(
            Required(arguments, "Seed"),
            Required(arguments, "Name"),
            Optional(arguments, "Author") ?? string.Empty,
            Optional(arguments, "Description") ?? string.Empty,
            Required(arguments, "ModPath"),
            Required(arguments, "GameDataRoot"),
            Required(arguments, "OutputDirectory"),
            Optional(arguments, "ThumbnailPath"),
            Optional(arguments, "ManifestIconPath"),
            components,
            optionGroups);

        var reporter = new WorkerProgressReporter(host, cancellationToken);
        var progress = new InlineProgress<Hd2ClBuildProgress>(value =>
            reporter.Report(value.Percent, value.Stage, message: value.Message));
        Hd2ClArtifactResult result;
        try
        {
            result = await Hd2ClArtifactBuilder.BuildAsync(request, progress, cancellationToken);
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
            : throw new ArgumentException("HD2CL 转换缺少参数：" + key);

    private static string? Optional(IReadOnlyDictionary<string, string> arguments, string key)
        => arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
