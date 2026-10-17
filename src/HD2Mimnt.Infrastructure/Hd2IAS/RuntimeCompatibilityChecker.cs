using System.Security.Cryptography;
using HD2Mimnt.Core.Hd2IAS;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

public sealed class RuntimeCompatibilityChecker(Hd2IASDeploymentStore? deploymentStore = null) : IHd2IASRuntimeInspector
{
    public Task<Hd2IASInstalledBuild> ReadInstalledAsync(string? gamePath, CancellationToken cancellationToken = default)
        => InstalledBuildReader.ReadAsync(gamePath, cancellationToken);

    public async Task<Hd2IASInspection> InspectAsync(Hd2IASPackage package, string? gamePath,
        Hd2IASRoute selectedRoute, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(gamePath))
            return new(package, selectedRoute, Hd2IASInstallationState.NotChecked, null, null, [], [], []);

        var build = await ReadInstalledAsync(gamePath, cancellationToken);
        var recognized = deploymentStore == null ? [] : await deploymentStore.ReadRecognizedFilesAsync(gamePath, cancellationToken);
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var runtime = package.ForRoute(selectedRoute);
        var game = new List<Hd2IASFileCheck>
        {
            await CheckAsync("bin/helldivers2.exe", runtime.GameExeSha256, []),
            await CheckAsync("data/bundle_database.data", runtime.BundleDatabaseSha256, []),
            await CheckAsync("bin/lua51.dll", runtime.Lua51Sha256, [])
        };
        var selected = await CheckRuntimeAsync(runtime);
        var other = await CheckRuntimeAsync(package.ForRoute(selectedRoute == Hd2IASRoute.Lua ? Hd2IASRoute.Native : Hd2IASRoute.Lua));
        Hd2IASRoute? installed = null;
        // 完整匹配、且只匹配一条路线时才报告版本；旧哈希不能证明旧版本或路线。
        var selectedMatches = selected.All(file => file.State == Hd2IASFileState.Match);
        var otherMatches = other.All(file => file.State == Hd2IASFileState.Match);
        if (package.IsValid && selectedMatches != otherMatches)
            installed = selectedMatches ? selectedRoute : selectedRoute == Hd2IASRoute.Lua ? Hd2IASRoute.Native : Hd2IASRoute.Lua;
        var foundation = new List<Hd2IASFileCheck>();
        foreach (var file in package.Foundation.Files)
            foundation.Add(await CheckAsync(file.Target!, file.Sha256, RecognizedHashes(file)));
        var state = installed != null ? Hd2IASInstallationState.RuntimeMatches
            : selected.Concat(other).All(file => file.State == Hd2IASFileState.Missing)
                ? Hd2IASInstallationState.Absent : Hd2IASInstallationState.IncompleteOrUnknown;
        var plan = new List<Hd2IASFilePlan>();
        foreach (var check in selected.Concat(foundation))
        {
            var action = InstalledBuildReader.PreserveExisting(check.Path) && check.State != Hd2IASFileState.Missing
                ? Hd2IASFileAction.PreserveSettings
                : check.State == Hd2IASFileState.Match ? Hd2IASFileAction.SkipUnchanged
                : !package.IsValid ? Hd2IASFileAction.BlockUnknown
                : check.State == Hd2IASFileState.Missing ? Hd2IASFileAction.Create
                : check.State == Hd2IASFileState.RecognizedPrevious
                    ? string.IsNullOrEmpty(check.ExpectedSha256) ? Hd2IASFileAction.RemoveRecognized : Hd2IASFileAction.ReplaceRecognized
                : Hd2IASFileAction.BlockUnknown;
            plan.Add(new(check.Path, action, check.ActualSha256, check.ExpectedSha256));
        }
        foreach (var config in build.ConfigurationFiles.Where(file => file.Kind != Hd2IASConfigKind.SystemCatalog))
            if (!plan.Any(file => file.Path.Equals(config.Path, StringComparison.OrdinalIgnoreCase)))
                plan.Add(new(config.Path, Hd2IASFileAction.PreserveSettings, null, null));
        return new(package, selectedRoute, state, installed, installed == null ? null : package.Version, game, selected, foundation)
            { InstalledBuild = build, FilePlan = plan };

        async Task<List<Hd2IASFileCheck>> CheckRuntimeAsync(Hd2IASRuntimeManifest manifest)
        {
            var checks = new List<Hd2IASFileCheck>();
            foreach (var file in manifest.Files.Where(file => file.Target != null))
                checks.Add(await CheckAsync(file.Target!, file.Sha256, RecognizedHashes(file)));
            foreach (var file in manifest.RetiredFiles)
            {
                var check = await CheckAsync(file.Target!, "", file.PreviousSha256);
                if (check.State != Hd2IASFileState.Missing) checks.Add(check);
            }
            return checks;
        }

        IReadOnlyList<string> RecognizedHashes(Hd2IASFile file) => file.PreviousSha256.Concat(recognized
            .Where(known => known.Target.Equals(file.Target, StringComparison.OrdinalIgnoreCase)).Select(known => known.Sha256))
            .Concat(package.Lua.Files.Concat(package.Native.Files)
                .Where(other => other.Target != null && other.Target.Equals(file.Target, StringComparison.OrdinalIgnoreCase))
                .Select(other => other.Sha256)).ToArray();

        async Task<Hd2IASFileCheck> CheckAsync(string relative, string expected, IReadOnlyList<string> previous)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(PackageManifestReader.SafePath(relative), gamePath);
            if (!hashes.TryGetValue(path, out var hash))
            {
                hash = null;
                if (File.Exists(path))
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                }
                hashes.Add(path, hash);
            }
            return new(relative, hash == null ? Hd2IASFileState.Missing : hash.Equals(expected, StringComparison.OrdinalIgnoreCase) ? Hd2IASFileState.Match
                : previous.Contains(hash, StringComparer.OrdinalIgnoreCase) ? Hd2IASFileState.RecognizedPrevious : Hd2IASFileState.Unknown,
                hash, expected);
        }
    }
}
