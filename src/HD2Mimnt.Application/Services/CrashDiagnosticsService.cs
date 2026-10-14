using System.Text.Json;
using System.Text.Json.Serialization;
using HD2Mimnt.Core.Diagnostics;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

public sealed class CrashDiagnosticsService(IMinidumpAnalyzer analyzer, IDeploymentRepository deployments,
    IModRepository mods, CrashDeploymentHistoryService history, ILogger<CrashDiagnosticsService>? logger = null)
{
    public bool IsAvailable => analyzer.IsAvailable;
    private static readonly HashSet<string> ProxyNames = new(StringComparer.OrdinalIgnoreCase)
        { "dxgi.dll", "d3d9.dll", "d3d11.dll", "dinput8.dll", "winmm.dll", "version.dll", "reshade64.dll", "reshade32.dll" };

    public Task<IReadOnlyList<CrashDumpFile>> FindDumpsAsync(string? gamePath, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<CrashDumpFile>>(() =>
        {
            var arrowhead = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arrowhead", "Helldivers2");
            var roots = new List<(string Path, string Pattern, bool Nested)>
            {
                (Path.Combine(arrowhead, "dumps"), "*.dmp", true),
                (Path.Combine(arrowhead, "crash_data"), "*.dmp", true),
                (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"), "helldivers2*.dmp", false)
            };
            if (!string.IsNullOrWhiteSpace(gamePath))
            {
                roots.Add((gamePath, "*.dmp", false));
                roots.Add((Path.Combine(gamePath, "bin"), "*.dmp", false));
            }
            var found = new List<CrashDumpFile>();
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root.Path)) continue;
                try
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = root.Nested, MaxRecursionDepth = 3,
                        IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in new DirectoryInfo(root.Path).EnumerateFiles(root.Pattern, options).Take(2000))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        found.Add(new(file.FullName, file.LastWriteTimeUtc, file.Length));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { logger?.LogWarning(error, "读取崩溃转储目录失败: {Path}", root.Path); }
            }
            return found.DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(file => file.ModifiedUtc).Take(100).ToArray();
        }, cancellationToken);

    public Task<CrashDiagnosisReport> AnalyzeAsync(string dumpPath, string? gamePath, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var analysis = await analyzer.AnalyzeAsync(dumpPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var isGame = FileName(analysis.MainModule).Equals("helldivers2.exe", StringComparison.OrdinalIgnoreCase);
            CrashDeploymentContext? context = null;
            string? contextError = null;
            var records = new List<Core.Entities.DeploymentRecord>();
            var names = new Dictionary<Guid, string>();
            try
            {
                records = (await deployments.GetLatestDeploymentsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                if (records.Count > 0)
                {
                    names = (await mods.GetByIdsAsync(records.Select(record => record.ModId).Distinct(), cancellationToken)
                        .ConfigureAwait(false)).ToDictionary(mod => mod.Id, mod => mod.Name);
                    context = new(records[0].SessionId, records.Min(record => record.StartTime),
                        records.Select(record => names.GetValueOrDefault(record.ModId, record.ModId.ToString())).Distinct().ToArray());
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { contextError = error.Message; logger?.LogWarning(error, "读取崩溃排查的部署上下文失败"); }
            // 最新部署只是当前上下文。比转储更晚的部署不能归因给旧崩溃。
            var afterDump = analysis.CapturedUtc is { } captured && records.Any(record => record.StartTime > captured);
            var clues = new List<CrashClue>();
            CrashDeploymentComparison? comparison = null;
            if (isGame && analysis.CapturedUtc is { } capture && !string.IsNullOrWhiteSpace(gamePath))
            {
                try { comparison = await history.CompareAsync(capture, gamePath, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                { contextError = error.Message; logger?.LogWarning(error, "读取部署时间线失败"); }
            }
            if (isGame)
            {
                foreach (var group in analysis.Modules.Where(module => !string.IsNullOrWhiteSpace(module.Name))
                    .GroupBy(module => FileName(module.Name), StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frames = analysis.Frames.Where(frame => FileName(frame.Module).Equals(group.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var matchingNames = !afterDump && analysis.CapturedUtc != null && !string.IsNullOrWhiteSpace(gamePath)
                        ? records.Where(record => record.Status == "Completed" && record.Files.Any(file =>
                            FileName(file.TargetPath).Equals(group.Key, StringComparison.OrdinalIgnoreCase)
                            && IsWithin(file.TargetPath, gamePath) && File.Exists(file.TargetPath)))
                            .Select(record => names.GetValueOrDefault(record.ModId, record.ModId.ToString())).Distinct().ToArray()
                        : [];
                    var ambiguous = group.Count() > 1;
                    var approximate = frames.Any(frame => frame.Trust == "scan");
                    if (matchingNames.Length > 0)
                        clues.Add(new(frames.Length > 0 ? CrashClueKind.ManagedModuleInStack : CrashClueKind.ManagedModuleLoaded,
                            group.Key, matchingNames, ambiguous, approximate));
                    else if (ProxyNames.Contains(group.Key))
                        clues.Add(new(CrashClueKind.PossibleHookLoaded, group.Key, [], ambiguous, approximate));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var assessment = clues.Any(clue => clue.Kind == CrashClueKind.ManagedModuleInStack && !clue.Ambiguous && !clue.ApproximateStack)
                ? CrashAssessment.ModClues : CrashAssessment.Inconclusive;
            return new CrashDiagnosisReport(Path.GetFullPath(dumpPath), DateTime.UtcNow, analysis, isGame, assessment,
                clues, context, afterDump, contextError, comparison);
        }, cancellationToken);

    public async Task ExportAsync(CrashDiagnosisReport report, string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(path, report.DumpPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The report must not overwrite the source minidump.");
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(output, report, options, cancellationToken).ConfigureAwait(false);
    }

    private static string FileName(string path) => Path.GetFileName(path.Replace('\\', '/'));
    private static bool IsWithin(string path, string root)
    {
        try { return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }
}
