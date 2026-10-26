using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.IO;
using HD2Mimnt.Infrastructure.Darktide;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services.Darktide;

public sealed class DarktideManagerService
{
    private readonly SettingsService _settings;
    private readonly DarktideRepository _repository;
    private readonly DarktideGameLocator _locator;
    private readonly DarktideModMetadataReader _metadataReader;
    private readonly DarktideArchiveInspector _archiveInspector;
    private readonly DarktideModInstaller _installer;
    private readonly DarktideStoragePaths _storage;
    private readonly ILogger<DarktideManagerService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DarktideManagerService(SettingsService settings, DarktideRepository repository,
        DarktideGameLocator locator, DarktideModMetadataReader metadataReader,
        DarktideArchiveInspector archiveInspector, DarktideModInstaller installer,
        DarktideStoragePaths storage, ILogger<DarktideManagerService>? logger = null)
    {
        _settings = settings;
        _repository = repository;
        _locator = locator;
        _metadataReader = metadataReader;
        _archiveInspector = archiveInspector;
        _installer = installer;
        _storage = storage;
        _logger = logger;
        _storage.EnsureDirectories();
    }

    public string GamePath => _settings.GetSetting(DarktideSettingsKeys.GamePath, "");
    public DarktideGameState GetGameState() => _locator.Locate(GamePath);
    public IReadOnlyList<string> FindInstalledGames() => _locator.FindInstalledGames();

    public string? GetGameExecutablePath()
    {
        var game = GetGameState();
        return game.RootPath != null && game.HasGameExecutable
            ? DarktidePaths.GameExecutable(game.RootPath)
            : null;
    }

    /// <summary>直接启动可执行文件；Steam 版需要 Steam 已在运行。</summary>
    public DarktideGameLaunchResult LaunchGame()
    {
        var executable = GetGameExecutablePath();
        if (executable == null)
            return new DarktideGameLaunchResult(false, null, "请先在设置中选择有效的 Darktide 游戏目录。");
        if (!File.Exists(executable))
            return new DarktideGameLaunchResult(false, null, "未找到 binaries/Darktide.exe。");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true
            });
            if (process == null)
                return new DarktideGameLaunchResult(false, null, "无法启动 Darktide.exe。");
            _logger?.LogInformation("已启动 Darktide：{Path}（PID {ProcessId}）", executable, process.Id);
            return new DarktideGameLaunchResult(true, process.Id, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "启动 Darktide 失败：{Path}", executable);
            return new DarktideGameLaunchResult(false, null, ex.Message);
        }
    }

    public async Task SaveGamePathAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            await _settings.SetSettingAsync(DarktideSettingsKeys.GamePath, "");
            return;
        }
        var state = _locator.Locate(path);
        if (state.RootPath == null || state.Status == DarktideGameStatus.InvalidPath)
            throw new InvalidOperationException(string.Join(" ", state.Notes.DefaultIfEmpty("所选目录不是有效的 Darktide 安装目录。")));
        await _settings.SetSettingAsync(DarktideSettingsKeys.GamePath, state.RootPath);
    }

    public async Task<DarktideScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ScanCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public DarktideArchiveInspection InspectArchive(string archivePath) => _archiveInspector.Inspect(archivePath);

    public async Task<DarktideInstallResult> ImportAsync(string archivePath, DarktideArchiveCandidate candidate,
        bool allowReplace, bool register, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ImportCoreAsync(archivePath, candidate, allowReplace, register, progress, cancellationToken); }
        finally { _gate.Release(); }
    }

    /// <summary>一次安装压缩包内的多个模组，并按依赖顺序登记到 mod_load_order.txt。</summary>
    public async Task<IReadOnlyList<DarktideInstallResult>> ImportBundleAsync(string archivePath,
        IReadOnlyList<DarktideArchiveCandidate> candidates, bool allowReplace,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0) throw new InvalidOperationException("没有选择要安装的模组。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var results = new List<DarktideInstallResult>();
            var span = 100d / candidates.Count;
            for (var index = 0; index < candidates.Count; index++)
            {
                var child = progress == null ? null : new ScaledProgress(index * span, span, progress);
                results.Add(await ImportCoreAsync(archivePath, candidates[index], allowReplace,
                    register: false, child, cancellationToken));
            }

            var game = RequireLoader();
            var ordered = OrderByDependencies(game.RootPath!, results.Select(result => result.InternalName).ToList());
            var document = LoadOrderFor(game.RootPath!);
            var changed = false;
            foreach (var name in ordered)
                changed |= document.Enable(name);
            if (changed) document.Save(_storage.LoadOrderBackups);
            for (var index = 0; index < results.Count; index++)
                results[index] = results[index] with { Registered = document.IsEnabled(results[index].InternalName) };
            return results;
        }
        finally { _gate.Release(); }
    }

    private async Task<DarktideInstallResult> ImportCoreAsync(string archivePath, DarktideArchiveCandidate candidate,
        bool allowReplace, bool register, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var game = RequireLoader();
        var inspection = _archiveInspector.Inspect(archivePath);
        if (inspection.Error != null) throw new InvalidDataException(inspection.Error);
        if (inspection.Kind == DarktideArchivePackageKind.CustomInstallRequired)
        {
            var sample = inspection.ExtraFiles.FirstOrDefault() ?? "";
            throw new InvalidOperationException(
                $"该压缩包还包含 {inspection.ExtraFiles.Count} 个模组目录之外的文件（例如 {sample}），" +
                "需要作者自己的安装流程。管理器不会只安装其中一部分，请按压缩包内的说明或使用其自带安装脚本。");
        }
        var selected = inspection.Candidates.FirstOrDefault(item =>
            string.Equals(item.RelativeRoot, candidate.RelativeRoot, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.InternalName, candidate.InternalName, StringComparison.OrdinalIgnoreCase));
        if (selected == null) throw new InvalidDataException("所选模组目录不在压缩包中。");
        var isDmf = selected.InternalName.Equals(DarktidePaths.DmfFolderName, StringComparison.OrdinalIgnoreCase);
        if (selected.InternalName.Equals(DarktidePaths.DmlBaseFolderName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DML base 是加载器核心，不能作为模组安装。");
        if (!isDmf && !DarktidePaths.IsSafeModFolderName(selected.InternalName))
            throw new InvalidOperationException($"压缩包中的模组名不安全：{selected.InternalName}");
        var installName = isDmf ? DarktidePaths.DmfFolderName : selected.InternalName;
        var modsRoot = DarktidePaths.ModsDirectory(game.RootPath!);
        var target = isDmf
            ? Path.GetFullPath(Path.Combine(modsRoot, DarktidePaths.DmfFolderName))
            : DarktidePaths.ResolveModFolder(game.RootPath!, installName);
        PathGuard.ResolveInsideRoot(modsRoot, target, "模组安装目标越过了 mods 根目录。");
        if (Directory.Exists(target) && !allowReplace) throw new DarktideInstallConflictException(installName);
        if (Directory.Exists(target) && DarktidePaths.IsReparsePoint(target))
            throw new InvalidOperationException("目标模组目录是符号链接或目录联接，已停止安装。");

        IReadOnlyList<DarktideLibraryEntry> library = isDmf
            ? Array.Empty<DarktideLibraryEntry>()
            : await _repository.GetLibraryAsync(cancellationToken);
        var existingLibrary = library.FirstOrDefault(entry =>
            string.Equals(entry.InternalName, installName, StringComparison.OrdinalIgnoreCase));
        var stagingRoot = Path.Combine(modsRoot, DarktidePaths.StagingPrefix + Guid.NewGuid().ToString("N"));
        var stagedModRoot = Path.Combine(stagingRoot, installName);
        try
        {
            Directory.CreateDirectory(stagingRoot);
            await _archiveInspector.ExtractCandidateAsync(archivePath, selected, stagedModRoot, cancellationToken, progress);
            var (metadata, modFilePresent) = _metadataReader.ReadStagedMod(stagedModRoot, installName);
            if (!modFilePresent) throw new InvalidDataException($"模组缺少 {installName}.mod。");

            if (!isDmf)
            {
                var managedArchive = await CopyArchiveToLibraryAsync(archivePath, cancellationToken);
                await _repository.UpsertLibraryAsync(new DarktideLibraryEntry(
                    installName, metadata.DisplayName, metadata.Version, metadata.Author,
                    metadata.Description, managedArchive.Path, Path.GetFileName(archivePath), managedArchive.Sha256,
                    managedArchive.Size, metadata.RequiredDependencies,
                    existingLibrary?.ImportedAt ?? DateTime.UtcNow, DateTime.UtcNow), cancellationToken);
            }

            var registered = false;
            Func<CancellationToken, Task>? afterCommit = null;
            if (register && !isDmf)
            {
                afterCommit = async _ =>
                {
                    var document = LoadOrderFor(game.RootPath!);
                    if (!document.IsEnabled(installName))
                    {
                        document.Enable(installName);
                        document.Save(_storage.LoadOrderBackups);
                    }
                    registered = true;
                    await Task.CompletedTask;
                };
            }

            var result = await _installer.InstallFromStagingAsync(game.RootPath!, stagedModRoot,
                installName, allowReplace, register && !isDmf, afterCommit, cancellationToken,
                allowDmfFramework: isDmf);
            if (isDmf) _logger?.LogInformation("暗潮 DMF 已安装: {Path}", result.InstalledPath);
            else _logger?.LogInformation("暗潮模组已安装: {Name} -> {Path}", installName, result.InstalledPath);
            return result with { Registered = registered };
        }
        finally
        {
            _installer.CleanupDirectory(stagingRoot);
        }
    }

    public async Task<DarktideInstallResult> ReinstallFromLibraryAsync(string internalName, bool register,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var game = RequireLoader();
            var library = await _repository.GetLibraryAsync(cancellationToken);
            var entry = library.FirstOrDefault(item => string.Equals(item.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("模组库中没有该模组。");
            if (!PathGuard.IsInsideRoot(_storage.Archives, entry.ArchivePath) || !File.Exists(entry.ArchivePath))
                throw new FileNotFoundException("模组库归档文件不存在或不在管理器归档目录中。", entry.ArchivePath);
            var inspection = _archiveInspector.Inspect(entry.ArchivePath);
            if (inspection.Error != null) throw new InvalidDataException(inspection.Error);
            var candidate = inspection.Candidates.FirstOrDefault(item =>
                string.Equals(item.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("归档中没有与模组名匹配的 .mod 文件。");
            var target = DarktidePaths.ResolveModFolder(game.RootPath!, internalName);
            if (Directory.Exists(target) && DarktidePaths.IsReparsePoint(target))
                throw new InvalidOperationException("目标模组目录是符号链接或目录联接，已停止安装。");

            var stagingRoot = Path.Combine(DarktidePaths.ModsDirectory(game.RootPath!), DarktidePaths.StagingPrefix + Guid.NewGuid().ToString("N"));
            var stagedModRoot = Path.Combine(stagingRoot, internalName);
            try
            {
                Directory.CreateDirectory(stagingRoot);
                await _archiveInspector.ExtractCandidateAsync(entry.ArchivePath, candidate, stagedModRoot, cancellationToken, progress);
                var (metadata, modFilePresent) = _metadataReader.ReadStagedMod(stagedModRoot, internalName);
                if (!modFilePresent) throw new InvalidDataException($"模组缺少 {internalName}.mod。");
                var registered = false;
                Func<CancellationToken, Task>? afterCommit = null;
                if (register)
                {
                    afterCommit = async _ =>
                    {
                        var document = LoadOrderFor(game.RootPath!);
                        if (!document.IsEnabled(internalName))
                        {
                            document.Enable(internalName);
                            document.Save(_storage.LoadOrderBackups);
                        }
                        registered = true;
                        await Task.CompletedTask;
                    };
                }
                var result = await _installer.InstallFromStagingAsync(game.RootPath!, stagedModRoot, internalName,
                    allowReplace: true, register, afterCommit, cancellationToken);
                await _repository.UpsertLibraryAsync(entry with
                {
                    DisplayName = metadata.DisplayName, Version = metadata.Version, Author = metadata.Author,
                    Description = metadata.Description, RequiredDependencies = metadata.RequiredDependencies,
                    UpdatedAt = DateTime.UtcNow
                }, cancellationToken);
                return result with { Registered = registered };
            }
            finally { _installer.CleanupDirectory(stagingRoot); }
        }
        finally { _gate.Release(); }
    }

    public async Task SetEnabledAsync(string internalName, bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var game = RequireLoader();
            if (enabled && DarktidePaths.IsFrameworkFolder(internalName))
                throw new InvalidOperationException("DMF/base 不需要登记到 mod_load_order.txt。");
            if (enabled)
            {
                var target = DarktidePaths.ResolveModFolder(game.RootPath!, internalName);
                if (!Directory.Exists(target)) throw new DirectoryNotFoundException("模组目录不存在，无法启用。");
            }
            var document = LoadOrderFor(game.RootPath!);
            if (!document.SetEnabled(internalName, enabled)) return;
            document.Save(_storage.LoadOrderBackups);
        }
        finally { _gate.Release(); }
    }

    public async Task MoveAsync(string internalName, int offset, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var game = RequireLoader();
            var document = LoadOrderFor(game.RootPath!);
            EnsureNoDuplicateRegistrations(document, "移动模组");
            var names = document.ActiveNames.ToList();
            var index = names.FindIndex(name => string.Equals(name, internalName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidOperationException("该模组未在加载顺序中登记。");
            var targetIndex = Math.Clamp(index + offset, 0, names.Count - 1);
            if (targetIndex == index) return;
            (names[index], names[targetIndex]) = (names[targetIndex], names[index]);
            document.ReorderActive(names);
            document.Save(_storage.LoadOrderBackups);
        }
        finally { _gate.Release(); }
    }

    public async Task ReorderAsync(IReadOnlyList<string> orderedNames, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var game = RequireLoader();
            var document = LoadOrderFor(game.RootPath!);
            EnsureNoDuplicateRegistrations(document, "重排加载顺序");
            document.ReorderActive(orderedNames);
            document.Save(_storage.LoadOrderBackups);
        }
        finally { _gate.Release(); }
    }

    public async Task UninstallAsync(string internalName, bool removeRegistration, bool deleteLibraryArchive,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var game = RequireLoader();
            DarktideLoadOrderDocument? document = null;
            var registrationRemoved = false;
            if (removeRegistration && File.Exists(DarktidePaths.LoadOrderFile(game.RootPath!)))
            {
                document = LoadOrderFor(game.RootPath!);
                if (document.Disable(internalName) > 0)
                {
                    document.Save(_storage.LoadOrderBackups);
                    registrationRemoved = true;
                }
            }
            try
            {
                _installer.Uninstall(game.RootPath!, internalName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (registrationRemoved && document != null)
                {
                    document.Enable(internalName);
                    document.Save(_storage.LoadOrderBackups);
                }
                throw;
            }

            if (deleteLibraryArchive) await DeleteLibraryArchiveAsync(internalName, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveFromLibraryAsync(string internalName, bool deleteArchive, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var game = GetGameState();
            if (game.RootPath != null && Directory.Exists(DarktidePaths.ResolveModFolder(game.RootPath, internalName)))
                throw new InvalidOperationException("模组仍安装在游戏目录中，请先卸载。");
            if (deleteArchive) await DeleteLibraryArchiveFileAsync(internalName, cancellationToken);
            await _repository.DeleteLibraryAsync(internalName, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<DarktideProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
        => await _repository.GetProfilesAsync(cancellationToken);

    public async Task CreateOrSaveProfileAsync(string name, bool overwrite, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var trimmed = ValidateProfileName(name);
            var game = RequireLoader();
            var profiles = await _repository.GetProfilesAsync(cancellationToken);
            var existing = profiles.FirstOrDefault(profile => string.Equals(profile.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !overwrite)
                throw new InvalidOperationException($"配置档“{trimmed}”已存在。");
            var scan = await ScanCoreAsync(cancellationToken);
            var document = LoadOrderFor(game.RootPath!);
            EnsureNoDuplicateRegistrations(document, "保存配置档");
            var active = document.ActiveNames.Where(name => !DarktidePaths.IsFrameworkFolder(name)).ToList();
            var installed = scan.Mods.Where(mod => mod.Installed)
                .Select(mod => mod.InternalName)
                .Where(name => !DarktidePaths.IsFrameworkFolder(name))
                .ToList();
            var entries = new List<DarktideProfileEntry>();
            var order = 0;
            foreach (var modName in active)
                entries.Add(new DarktideProfileEntry(modName, true, order++));
            foreach (var modName in installed.Where(name => !active.Contains(name, StringComparer.OrdinalIgnoreCase))
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                entries.Add(new DarktideProfileEntry(modName, false, order++));
            await _repository.UpsertProfileAsync(new DarktideProfile(trimmed,
                existing?.CreatedAt ?? DateTime.UtcNow, DateTime.UtcNow, existing?.IsActive ?? false, entries),
                cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<DarktideProfileApplyPreview> PreviewApplyProfileAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await BuildApplyPreviewAsync(name, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<DarktideProfileApplyResult> ApplyProfileAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var game = RequireLoader();
            var preview = await BuildApplyPreviewAsync(name, cancellationToken);
            var profiles = await _repository.GetProfilesAsync(cancellationToken);
            var profile = profiles.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidOperationException("配置档不存在。");
            var document = LoadOrderFor(game.RootPath!);
            var currentActive = document.ActiveNames.ToList();
            var profileNames = profile.Entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var target = profile.Entries.Where(entry => entry.Enabled).OrderBy(entry => entry.Order)
                .Select(entry => entry.Name)
                .Where(modName => !preview.Missing.Contains(modName, StringComparer.OrdinalIgnoreCase)
                                  && !DarktidePaths.IsFrameworkFolder(modName))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            target.AddRange(currentActive.Where(modName => !profileNames.Contains(modName)
                                                           && !DarktidePaths.IsFrameworkFolder(modName)));
            document.SetActiveOrder(target);
            var save = document.Save(_storage.LoadOrderBackups);
            try
            {
                await _repository.UpsertProfileAsync(profile with
                {
                    IsActive = true, ModifiedAt = DateTime.UtcNow
                }, cancellationToken);
            }
            catch
            {
                if (save.Changed)
                {
                    try { document.SetActiveOrder(currentActive); document.Save(_storage.LoadOrderBackups); }
                    catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        _logger?.LogError(restoreError, "配置档激活失败后恢复加载顺序失败");
                    }
                }
                throw;
            }
            return new DarktideProfileApplyResult(profile.Name, target, preview.Missing, save.BackupPath);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteProfileAsync(string name, CancellationToken cancellationToken = default)
        => await _repository.DeleteProfileAsync(name, cancellationToken);

    private async Task<DarktideProfileApplyPreview> BuildApplyPreviewAsync(string name, CancellationToken cancellationToken)
    {
        var game = RequireLoader();
        var profiles = await _repository.GetProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("配置档不存在。");
        var document = LoadOrderFor(game.RootPath!);
        EnsureNoDuplicateRegistrations(document, "应用配置档");
        var active = document.ActiveNames.ToList();
        var installed = ScanInstalledMods(game).Mods.Select(mod => mod.InternalName)
            .Where(modName => !DarktidePaths.IsFrameworkFolder(modName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desired = profile.Entries.Where(entry => entry.Enabled).OrderBy(entry => entry.Order)
            .Select(entry => entry.Name)
            .Where(modName => !DarktidePaths.IsFrameworkFolder(modName))
            .ToList();
        var missing = desired.Where(modName => !installed.Contains(modName)).ToList();
        var enabled = desired.Where(modName => installed.Contains(modName)).ToList();
        var toEnable = enabled.Where(modName => !active.Contains(modName, StringComparer.OrdinalIgnoreCase)).ToList();
        var disabled = profile.Entries.Where(entry => !entry.Enabled).Select(entry => entry.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toDisable = active.Where(modName => disabled.Contains(modName)).ToList();
        var currentOrder = active.Where(modName => enabled.Contains(modName, StringComparer.OrdinalIgnoreCase)).ToList();
        var orderChanged = !currentOrder.SequenceEqual(enabled, StringComparer.OrdinalIgnoreCase);
        var profileNames = profile.Entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = active.Where(modName => !profileNames.Contains(modName)
                                              && !DarktidePaths.IsFrameworkFolder(modName)).ToList();
        return new DarktideProfileApplyPreview(profile.Name, toEnable, toDisable, missing, orderChanged, unknown);
    }

    private async Task<DarktideScanResult> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var game = GetGameState();
        var library = await _repository.GetLibraryAsync(cancellationToken);
        var diagnostics = new List<DarktideDiagnostic>();
        AddGameDiagnostics(game, diagnostics);

        DarktideLoadOrderDocument? document = null;
        if (game.RootPath != null && game.HasModsDirectory)
        {
            if (!game.HasLoadOrderFile)
                diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Error, DarktideIssueKind.LoadOrderMissing,
                    Detail: "缺少 mods/mod_load_order.txt。"));
            else
            {
                try { document = DarktideLoadOrderDocument.Load(DarktidePaths.LoadOrderFile(game.RootPath)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Error,
                        DarktideIssueKind.LoadOrderUnreadable, Detail: ex.Message));
                }
            }
        }

        var installedScan = ScanInstalledMods(game);
        diagnostics.AddRange(installedScan.Diagnostics);
        var active = document?.ActiveNames.ToList() ?? new List<string>();
        foreach (var duplicate in active.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Warning, DarktideIssueKind.DuplicateRegistration,
                duplicate.Key, Detail: $"重复登记 {duplicate.Count()} 次，未自动去重。"));
        foreach (var framework in active.Where(DarktidePaths.IsFrameworkFolder))
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Warning, DarktideIssueKind.FrameworkRegistration,
                framework, Detail: "DML/DMF 基础目录不需要登记到 mod_load_order.txt。"));

        var installed = installedScan.Mods.ToDictionary(mod => mod.InternalName, StringComparer.OrdinalIgnoreCase);
        var overviews = new Dictionary<string, DarktideModOverview>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in library)
        {
            overviews[entry.InternalName] = new DarktideModOverview(entry.InternalName,
                string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.InternalName : entry.DisplayName,
                entry.Version, entry.Author, entry.Description, true, false,
                active.Contains(entry.InternalName, StringComparer.OrdinalIgnoreCase), true, false, null,
                entry.ArchivePath, entry.ArchiveSize, entry.RequiredDependencies,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<DarktideDiagnostic>());
        }
        foreach (var mod in installed.Values)
        {
            var existing = overviews.GetValueOrDefault(mod.InternalName);
            overviews[mod.InternalName] = new DarktideModOverview(mod.InternalName,
                string.IsNullOrWhiteSpace(mod.Metadata.DisplayName) ? mod.InternalName : mod.Metadata.DisplayName,
                mod.Metadata.Version, mod.Metadata.Author, mod.Metadata.Description,
                existing?.InLibrary ?? false, true, active.Contains(mod.InternalName, StringComparer.OrdinalIgnoreCase),
                mod.FolderNameMatchesModFile, mod.Metadata.HasModFile, mod.FolderPath, existing?.ArchivePath,
                existing?.ArchiveSize ?? 0, mod.Metadata.RequiredDependencies, mod.Metadata.LoadAfter,
                mod.Metadata.LoadBefore, Array.Empty<DarktideDiagnostic>());
        }
        foreach (var name in active.Where(name => !installed.ContainsKey(name) && !DarktidePaths.IsFrameworkFolder(name)))
        {
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Error, DarktideIssueKind.MissingRegisteredMod,
                name, Detail: "加载顺序中的模组目录不存在。"));
            if (overviews.TryGetValue(name, out var libraryOnly))
                overviews[name] = libraryOnly with { Registered = true };
            else
            {
                overviews[name] = new DarktideModOverview(name, name, "", "", "", false, false, true,
                    true, false, null, null, 0, Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<string>(), Array.Empty<DarktideDiagnostic>());
            }
        }
        foreach (var entry in library.Where(entry => !installed.ContainsKey(entry.InternalName)))
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Info, DarktideIssueKind.OrphanLibraryEntry,
                entry.InternalName, Detail: "模组库中有归档，但游戏 mods 目录中未安装。"));
        AddDependencyDiagnostics(installed, active, diagnostics);

        // 模组库返回稳定的名称序身份列表：不跟随 mod_load_order.txt 的位置，启停也不改变位置
        // （与本家配置文件页一致），分组位点按它换算才不会因开关漂移。
        var mods = overviews.Values
            .Where(mod => !DarktidePaths.IsFrameworkFolder(mod.InternalName))
            .OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.InternalName, StringComparer.OrdinalIgnoreCase)
            .Select(mod => mod with
            {
                Diagnostics = diagnostics.Where(diagnostic =>
                    string.Equals(diagnostic.ModName, mod.InternalName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(diagnostic.RelatedName, mod.InternalName, StringComparison.OrdinalIgnoreCase)).ToList()
            }).ToList();

        var disabled = installed.Keys.Where(name => !active.Contains(name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        return new DarktideScanResult(game, mods, active, disabled, diagnostics, DateTime.UtcNow);
    }

    private InstalledScan ScanInstalledMods(DarktideGameState game)
    {
        var mods = new List<DarktideInstalledMod>();
        var diagnostics = new List<DarktideDiagnostic>();
        if (game.RootPath == null || !game.HasModsDirectory) return new InstalledScan(mods, diagnostics);
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(DarktidePaths.ModsDirectory(game.RootPath)))
            {
                var folderName = Path.GetFileName(folder);
                if (DarktidePaths.IsStagingOrBackupFolder(folderName) || DarktidePaths.IsFrameworkFolder(folderName)) continue;
                var mod = _metadataReader.ReadInstalledMod(folder);
                if (mod == null) continue;
                mods.Add(mod);
                if (!mod.FolderNameMatchesModFile)
                    diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Error, DarktideIssueKind.MissingModFile,
                        folderName, Detail: $"缺少 {folderName}.mod；DML 要求 .mod 文件名与目录名一致。"));
                else
                {
                    try
                    {
                        var otherModFile = Directory.EnumerateFiles(folder, "*.mod", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault(path => !string.Equals(Path.GetFileNameWithoutExtension(path), folderName,
                                StringComparison.OrdinalIgnoreCase));
                        if (otherModFile != null)
                            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Warning,
                                DarktideIssueKind.DirectoryNameMismatch, folderName, Detail: $"存在其他 .mod 文件：{Path.GetFileName(otherModFile)}"));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                if (mod.Metadata.MetadataError is { } metadataError)
                    diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Warning, DarktideIssueKind.InvalidMetadata,
                        folderName, Detail: metadataError));
                if (!mod.Metadata.HasScripts)
                    diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Warning, DarktideIssueKind.InvalidMetadata,
                        folderName, Detail: "未找到 scripts 下的 Lua 文件。"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Error, DarktideIssueKind.InvalidGamePath,
                Detail: $"读取 mods 目录失败：{ex.Message}"));
        }
        return new InstalledScan(mods, diagnostics);
    }

    private static void AddDependencyDiagnostics(Dictionary<string, DarktideInstalledMod> installed,
        List<string> active, List<DarktideDiagnostic> diagnostics)
    {
        var positions = active.Select((name, index) => (name, index))
            .GroupBy(item => item.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        var edges = new List<(string Before, string After, string Reason)>();
        foreach (var mod in installed.Values)
        {
            var metadata = mod.Metadata;
            foreach (var dependency in metadata.RequiredDependencies)
            {
                if (!installed.ContainsKey(dependency))
                {
                    diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Error, DarktideIssueKind.MissingDependency,
                        mod.InternalName, dependency, "必需依赖未安装。"));
                    continue;
                }
                if (!positions.ContainsKey(dependency))
                    diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Warning, DarktideIssueKind.DisabledDependency,
                        mod.InternalName, dependency, "必需依赖当前未登记加载。"));
                edges.Add((dependency, mod.InternalName, "required"));
            }
            foreach (var related in metadata.LoadAfter)
                if (installed.ContainsKey(related)) edges.Add((related, mod.InternalName, "self_after"));
            foreach (var related in metadata.LoadBefore)
                if (installed.ContainsKey(related)) edges.Add((mod.InternalName, related, "self_before"));
        }

        foreach (var edge in edges)
        {
            if (!positions.TryGetValue(edge.Before, out var before) || !positions.TryGetValue(edge.After, out var after))
                continue;
            if (before < after) continue;
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Warning, DarktideIssueKind.DependencyOrder,
                edge.After, edge.Before));
        }

        var reportedCycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adjacency = installed.Keys.ToDictionary(name => name, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges)
            if (adjacency.ContainsKey(edge.Before) && adjacency.ContainsKey(edge.After))
                adjacency[edge.Before].Add(edge.After);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        void Visit(string node)
        {
            state[node] = 1;
            stack.Add(node);
            foreach (var next in adjacency[node])
            {
                if (!state.TryGetValue(next, out var nextState))
                {
                    Visit(next);
                }
                else if (nextState == 1)
                {
                    var index = stack.FindIndex(name => string.Equals(name, next, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) continue;
                    var cycle = stack.Skip(index).Concat(new[] { next }).ToList();
                    var key = string.Join("|", cycle.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
                    if (reportedCycles.Add(key))
                        diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Error, DarktideIssueKind.DependencyCycle,
                            cycle[0], cycle[^1], string.Join(" → ", cycle)));
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }
        foreach (var node in adjacency.Keys)
            if (!state.ContainsKey(node)) Visit(node);
    }

    private static void AddGameDiagnostics(DarktideGameState game, List<DarktideDiagnostic> diagnostics)
    {
        if (game.Status == DarktideGameStatus.NotConfigured)
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Error, DarktideIssueKind.MissingGamePath,
                Detail: "尚未设置 Darktide 游戏目录。"));
        else if (game.Status == DarktideGameStatus.InvalidPath)
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Error, DarktideIssueKind.InvalidGamePath,
                Detail: string.Join(" ", game.Notes)));
        else if (game.Status == DarktideGameStatus.GameWithoutLoader)
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Warning, DarktideIssueKind.LoaderNotInstalled,
                Detail: "游戏目录中没有完整的 Darktide Mod Loader。"));
        else if (game.Status == DarktideGameStatus.LoaderIncomplete)
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Error, DarktideIssueKind.LoaderNotInstalled,
                Detail: "Darktide Mod Loader 文件不完整。"));
        if ((game.Status is DarktideGameStatus.LoaderInstalled or DarktideGameStatus.LoaderIncomplete) && !game.HasDmf)
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Warning, DarktideIssueKind.DmfMissing,
                Detail: "未检测到 mods/dmf/dmf.mod。"));
        if (game.AlternativeLoader != null)
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Warning, DarktideIssueKind.AlternativeLoader,
                game.AlternativeLoader, Detail: "标准 mod_load_order.txt 可能不是实际加载顺序。"));
        if (game.Status == DarktideGameStatus.LoaderInstalled)
            diagnostics.Add(new DarktideDiagnostic(DarktideIssueSeverity.Info, DarktideIssueKind.PatchStateUnknown,
                Detail: "无法仅凭文件可靠判断游戏补丁是否启用；请通过官方 toggle 脚本确认。"));
    }

    private DarktideGameState RequireLoader()
    {
        var game = GetGameState();
        if (game.RootPath == null || game.Status == DarktideGameStatus.InvalidPath)
            throw new InvalidOperationException("请先在设置中选择有效的 Darktide 游戏目录。");
        if (!game.HasModsDirectory || !game.HasLoadOrderFile)
            throw new InvalidOperationException("未检测到完整的 Darktide Mod Loader（mods 与 mod_load_order.txt）。");
        return game;
    }

    private static DarktideLoadOrderDocument LoadOrderFor(string gameRoot)
    {
        var path = DarktidePaths.LoadOrderFile(gameRoot);
        if (!File.Exists(path)) throw new FileNotFoundException("mod_load_order.txt 不存在。", path);
        return DarktideLoadOrderDocument.Load(path);
    }

    private static void EnsureNoDuplicateRegistrations(DarktideLoadOrderDocument document, string action)
    {
        var duplicates = document.ActiveNames.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        if (duplicates.Count == 0) return;
        throw new InvalidOperationException(
            $"{action}前请先修复 mod_load_order.txt 中的重复登记：{string.Join(", ", duplicates)}。管理器不会自动去重。");
    }

    private async Task<(string Path, string Sha256, long Size)> CopyArchiveToLibraryAsync(string archivePath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storage.Archives);
        string sha256;
        await using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         bufferSize: 81920, useAsync: true))
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        var name = SanitizeFileName(Path.GetFileName(archivePath));
        var managedPath = Path.GetFullPath(Path.Combine(_storage.Archives, $"{sha256[..12]}-{name}"));
        PathGuard.ResolveInsideRoot(_storage.Archives, managedPath, "模组库归档路径越界。");
        if (File.Exists(managedPath)) return (managedPath, sha256, new FileInfo(managedPath).Length);
        await using (var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         bufferSize: 81920, useAsync: true))
        await using (var destination = new FileStream(managedPath, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, bufferSize: 81920, useAsync: true))
            await source.CopyToAsync(destination, cancellationToken);
        return (managedPath, sha256, new FileInfo(managedPath).Length);
    }

    private async Task DeleteLibraryArchiveAsync(string internalName, CancellationToken cancellationToken)
    {
        await DeleteLibraryArchiveFileAsync(internalName, cancellationToken);
        await _repository.DeleteLibraryAsync(internalName, cancellationToken);
    }

    private async Task DeleteLibraryArchiveFileAsync(string internalName, CancellationToken cancellationToken)
    {
        var library = await _repository.GetLibraryAsync(cancellationToken);
        var entry = library.FirstOrDefault(item => string.Equals(item.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;
        if (PathGuard.IsInsideRoot(_storage.Archives, entry.ArchivePath) && File.Exists(entry.ArchivePath))
            File.Delete(entry.ArchivePath);
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = "mod.zip";
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name : name + ".zip";
    }

    /// <summary>按 info.json 声明排序：被依赖者在前；存在循环时退回原顺序，交给诊断提示。</summary>
    private List<string> OrderByDependencies(string gameRoot, IReadOnlyList<string> names)
    {
        var metadata = names.ToDictionary(
            name => name,
            name => _metadataReader.ReadInstalledMod(DarktidePaths.ResolveModFolder(gameRoot, name))?.Metadata,
            StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var remaining = names.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(candidate => !HasUnresolvedPredecessor(candidate, remaining, metadata))
                        ?? remaining[0];
            ordered.Add(next);
            remaining.Remove(next);
        }
        return ordered;
    }

    private static bool HasUnresolvedPredecessor(string name, IReadOnlyList<string> remaining,
        Dictionary<string, DarktideModMetadata?> metadata)
    {
        var info = metadata[name];
        foreach (var other in remaining)
        {
            if (string.Equals(other, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (info != null && (info.RequiredDependencies.Contains(other, StringComparer.OrdinalIgnoreCase)
                                 || info.LoadAfter.Contains(other, StringComparer.OrdinalIgnoreCase)))
                return true;
            if (metadata[other]?.LoadBefore.Contains(name, StringComparer.OrdinalIgnoreCase) == true)
                return true;
        }
        return false;
    }

    // ── 模组库分组：只组织库列表显示，不写 mod_load_order.txt，也不是可安装模组。
    // sequence 是当前库列表的稳定内部名顺序，分隔符的 Order/MemberCount 全部按它换算。

    public async Task<IReadOnlyList<DarktideModSeparator>> GetModSeparatorsAsync(
        CancellationToken cancellationToken = default)
        => await _repository.GetModSeparatorsAsync(cancellationToken);

    public async Task AddModSeparatorAsync(string name, int order, IReadOnlyList<string> sequence,
        string color = "", CancellationToken cancellationToken = default)
    {
        var trimmed = ValidateSeparatorName(name);
        var normalizedColor = NormalizeSeparatorColor(color);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadSeparatorsAsync(cancellationToken);
            if (current.Any(item => string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已存在同名分组。");
            // MemberCount 留空：落在已有组内时，原组以新分隔符为边界，等于把原组切成两半。
            current.Add(new DarktideModSeparator(Guid.NewGuid(), trimmed,
                Math.Clamp(order, 0, sequence.Count), null, false, normalizedColor));
            await SaveSeparatorsAsync(current, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateModSeparatorAsync(Guid id, string name, string? color,
        CancellationToken cancellationToken = default)
    {
        var trimmed = ValidateSeparatorName(name);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadSeparatorsAsync(cancellationToken);
            var index = current.FindIndex(item => item.Id == id);
            if (index < 0) throw new InvalidOperationException("分组不存在。");
            if (current.Any(item => item.Id != id
                         && string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已存在同名分组。");
            var target = current[index];
            current[index] = target with
            {
                Name = trimmed,
                Color = color == null ? target.Color : NormalizeSeparatorColor(color)
            };
            await SaveSeparatorsAsync(current, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SetModSeparatorCollapsedAsync(Guid id, bool collapsed, IReadOnlyList<string> sequence,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadSeparatorsAsync(cancellationToken);
            var index = current.FindIndex(item => item.Id == id);
            if (index < 0) throw new InvalidOperationException("分组不存在。");
            var target = current[index];
            if (target.IsCollapsed == collapsed) return;
            // 收起前先固化边界：边界为空的组以“下一个分隔符”为终点，收起后成员会整组消失。
            current[index] = target with
            {
                MemberCount = target.MemberCount ?? MemberCountOf(current, index, sequence.Count),
                IsCollapsed = collapsed
            };
            await SaveSeparatorsAsync(current, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveModSeparatorAsync(Guid id, IReadOnlyList<string> sequence,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadSeparatorsAsync(cancellationToken);
            var index = current.FindIndex(item => item.Id == id);
            if (index < 0) return;
            var absorbed = current[index].MemberCount ?? MemberCountOf(current, index, sequence.Count);
            current.RemoveAt(index);
            // 被删组的成员并入上一组；上一组边界为空时会自动吸收，改写反而会限制它。
            if (index > 0 && current[index - 1].MemberCount is { } previousMembers)
                current[index - 1] = current[index - 1] with { MemberCount = previousMembers + absorbed };
            await SaveSeparatorsAsync(current, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<DarktideModSeparator>> LoadSeparatorsAsync(CancellationToken cancellationToken)
        => (await _repository.GetModSeparatorsAsync(cancellationToken))
            .OrderBy(item => item.Order).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private Task SaveSeparatorsAsync(List<DarktideModSeparator> separators, CancellationToken cancellationToken)
        => _repository.SaveModSeparatorsAsync(
            separators.OrderBy(item => item.Order).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            cancellationToken);

    private static int MemberCountOf(IReadOnlyList<DarktideModSeparator> separators, int index, int totalCount)
    {
        var separator = separators[index];
        var next = index + 1 < separators.Count ? separators[index + 1].Order : totalCount;
        return Math.Clamp(Math.Min(next, totalCount) - Math.Min(separator.Order, totalCount), 0, totalCount);
    }

    private static string ValidateSeparatorName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("分组名称不能为空。");
        if (trimmed.Length > 60) throw new ArgumentException("分组名称不能超过 60 个字符。");
        if (trimmed.Any(char.IsControl)) throw new ArgumentException("分组名称不能包含控制字符。");
        return trimmed;
    }

    private static string NormalizeSeparatorColor(string color)
    {
        var trimmed = color.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (trimmed.Length != 7 || trimmed[0] != '#' || !trimmed.Skip(1).All(Uri.IsHexDigit))
            throw new ArgumentException("颜色须为 #RRGGBB 格式。");
        return trimmed.ToUpperInvariant();
    }

    private static string ValidateProfileName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("配置档名称不能为空。");
        if (trimmed.Length > 80) throw new ArgumentException("配置档名称不能超过 80 个字符。");
        if (trimmed.Any(char.IsControl)) throw new ArgumentException("配置档名称不能包含控制字符。");
        return trimmed;
    }

    private sealed record InstalledScan(IReadOnlyList<DarktideInstalledMod> Mods,
        IReadOnlyList<DarktideDiagnostic> Diagnostics);

    private sealed class ScaledProgress(double offset, double span, IProgress<double> target) : IProgress<double>
    {
        public void Report(double value) => target.Report(offset + Math.Clamp(value, 0, 100) / 100 * span);
    }
}
