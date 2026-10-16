using System.Diagnostics;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Archive;

/// <summary>通过随程序分发的 7za 写入 LZMA2 固实压缩包。</summary>
public static class SevenZipArchiveWriter
{
    public static async Task CreateAsync(string archivePath, string source, string? sourceList,
        string metadataDirectory, ModArchiveCompression compression, CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Tools", "7zip", "7za.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Creator.SevenZipMissing", executable);
        if (sourceList != null)
            await AddAsync(executable, archivePath, source, ["@" + sourceList], compression, token);
        // 新增两份清单形成独立的小块，无需重压源文件的固实块，也不复制源模组。
        await AddAsync(executable, archivePath, metadataDirectory, ["manifest.json", "hd2mimnt.json"], compression, token);
    }

    private static async Task AddAsync(string executable, string archivePath, string workingDirectory,
        string[] inputs, ModArchiveCompression compression, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "a", "-t7z", $"-mx={(int)compression}", "-mmt=2", "-scsUTF-8", "-sccUTF-8",
                     "-bso0", "-bsp0", "-bse2", "-y", "-spd" }) start.ArgumentList.Add(argument);
        if (compression != ModArchiveCompression.Store)
        {
            start.ArgumentList.Add("-m0=LZMA2"); start.ArgumentList.Add("-ms=on");
            start.ArgumentList.Add(compression switch
            {
                ModArchiveCompression.Fast => "-md=16m", ModArchiveCompression.Normal => "-md=32m",
                ModArchiveCompression.High => "-md=64m", _ => "-md=128m"
            });
        }
        start.ArgumentList.Add(archivePath);
        foreach (var input in inputs) start.ArgumentList.Add(input);
        using var process = Process.Start(start) ?? throw new IOException("Could not start 7za.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            throw;
        }
        await Task.WhenAll(output, errors);
        token.ThrowIfCancellationRequested();
        // 7-Zip 的警告也可能表示跳过文件，不能当作完整导出发布。
        if (process.ExitCode != 0) throw new IOException($"7-Zip ({process.ExitCode}): {(await errors).Trim()}");
    }
}
