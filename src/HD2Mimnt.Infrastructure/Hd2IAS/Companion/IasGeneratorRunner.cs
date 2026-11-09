using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace HD2Mimnt.Infrastructure.Hd2IAS.Companion;

/// <summary>
/// 调用上游签名 Python 生成器（python.exe -I -B -m runtime_generation --request …）。
/// metadata 流程不会发起资源请求；若 stdout 出现请求行说明请求构造有误，立即失败。
/// stderr 行式 JSON（HD2GenerationProgress1）转为进度，其余累积用于失败诊断。
/// </summary>
public static class IasGeneratorRunner
{
    private const int MaxErrorLength = 32 * 1024;

    public static async Task RunAsync(string generatorRoot, string pythonExe, JsonObject request,
        string requestPath, IProgress<string>? progress, CancellationToken token)
    {
        await File.WriteAllTextAsync(requestPath, request.ToJsonString(), token);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = generatorRoot,
        };
        foreach (var argument in new[] { "-I", "-B", "-X", "utf8", "-m", "runtime_generation", "--request", requestPath })
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start()) throw new InvalidOperationException("生成器未能启动。");
        try
        {
            // 生成器只在原版/Mod 资源流程读 stdin；metadata 流程不请求，直接关闭。
            process.StandardInput.Close();

            var errors = new StringBuilder();
            var errorTask = PumpErrorsAsync(process, errors, progress, token);
            var outputTask = PumpOutputAsync(process, token);
            await Task.WhenAll(errorTask, outputTask).WaitAsync(token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"护甲配套生成失败。\n{errors.ToString().TrimEnd()}");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task PumpOutputAsync(Process process, CancellationToken token)
    {
        var stdout = process.StandardOutput;
        while (await stdout.ReadLineAsync(token) is { } line)
        {
            if (line.Length == 0) continue;
            // metadata 流程不应有任何请求；出现即视为请求构造错误（与上游语义一致）。
            throw new InvalidOperationException("Mod 配套生成器请求了额外资源，请求构造有误：" + Truncate(line));
        }
    }

    private static async Task PumpErrorsAsync(Process process, StringBuilder errors,
        IProgress<string>? progress, CancellationToken token)
    {
        var stderr = process.StandardError;
        while (await stderr.ReadLineAsync(token) is { } line)
        {
            if (errors.Length < MaxErrorLength)
            {
                lock (errors) errors.AppendLine(line);
            }
            if (!line.StartsWith('{')) continue;
            try
            {
                var node = JsonNode.Parse(line);
                if (node?["schema"]?.GetValue<string>() == "HD2GenerationProgress1"
                    && node["stage"] is JsonValue stage && stage.TryGetValue<string>(out var name))
                    progress?.Report("IAS · " + name);
            }
            catch (System.Text.Json.JsonException) { }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException) { }
    }

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512] + "…";
}
