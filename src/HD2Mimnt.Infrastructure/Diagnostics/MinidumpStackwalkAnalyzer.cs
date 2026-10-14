using System.Diagnostics;
using System.Buffers.Binary;
using System.Text;
using HD2Mimnt.Core.Diagnostics;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Diagnostics;

/// <summary>离线运行内置工具；转储只读，输出和符号临时目录在每次结束时清理。</summary>
public sealed class MinidumpStackwalkAnalyzer(string executablePath, string tempPath,
    ILogger<MinidumpStackwalkAnalyzer>? logger = null) : IMinidumpAnalyzer
{
    public bool IsAvailable => OperatingSystem.IsWindows() && File.Exists(executablePath);

    public async Task<CrashDumpAnalysis> AnalyzeAsync(string dumpPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) throw new FileNotFoundException("Bundled minidump-stackwalk.exe is unavailable.", executablePath);
        dumpPath = Path.GetFullPath(dumpPath);
        // 不按扩展名决定格式；先检查 minidump 文件头，避免把压缩包/文本交给工具。
        DateTime? capturedUtc = null;
        await using (var input = new FileStream(dumpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var header = new byte[24];
            if (input.Length < 32)
                throw new InvalidDataException("The selected file is too short to be a Windows minidump.");
            await input.ReadExactlyAsync(header, cancellationToken);
            if (!header.AsSpan(0, 4).SequenceEqual("MDMP"u8))
                throw new InvalidDataException("The selected file is not a Windows minidump (MDMP).");
            var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
            if (timestamp > 0) capturedUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
        }
        var workPath = Path.Combine(tempPath, "crash-diagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workPath);
        try
        {
            var output = Path.Combine(workPath, "stackwalk.json");
            var start = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workPath
            };
            foreach (var argument in new[] { "--json", "--no-interactive", "--no-color", "--output-file", output,
                "--symbols-cache", workPath, "--symbols-tmp", workPath, dumpPath }) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            process.Start();
            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("minidump-stackwalk exceeded the two-minute limit.");
            }
            await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidDataException($"minidump-stackwalk exited with code {process.ExitCode}: {errors}");
            if (!File.Exists(output) || new FileInfo(output).Length > 32 * 1024 * 1024)
                throw new InvalidDataException("The stackwalk JSON report is missing or exceeds 32 MiB.");
            var json = await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false);
            return MinidumpJsonReader.Read(json) with { CapturedUtc = capturedUtc };
        }
        finally
        {
            try { Directory.Delete(workPath, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { logger?.LogWarning(error, "清理崩溃分析临时目录失败: {Path}", workPath); }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            if (text.Length < 16384) text.Append(buffer, 0, Math.Min(count, 16384 - text.Length));
        return text.ToString();
    }
}
