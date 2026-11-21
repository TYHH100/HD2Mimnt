using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Application.Cli;

internal static partial class CliHandlers
{
    /// <summary>
    /// settings 扩容域：SettingKeys 全量键白名单（类型校验 + 分组）。
    /// 外观类键（Theme/Font*/Glass*/Background* 等）保存后只对 UI 生效，CLI 侧标注提示。
    /// </summary>
    internal static class SettingsExt
    {
        private static readonly Dictionary<string, string> KeyTypes = new(StringComparer.Ordinal)
        {
            ["Game.Path"] = "path", ["Game.AutoDetect"] = "bool", ["Game.Mode"] = "enum:Official,HellForge",
            ["Game.HellForgePath"] = "path", ["IAS.InstallLocation"] = "path",
            ["ProfilesAutoSwitchOnSelect"] = "bool", ["ProfilesAutoCheckConflicts"] = "bool",
            ["ProfilesLockLoaders"] = "bool", ["ProfilesFreeLoaderOrder"] = "bool",
            ["ProfilesAutoAddImportedMods"] = "bool", ["ProfilesChooseGroupWhenAdding"] = "bool",
            ["ProfilesPinExpandedGroups"] = "bool",
            ["DefaultDeploymentMethod"] = "enum:Copy,Symlink,Hardlink", ["DefaultDeploymentOrder"] = "enum:TopToBottom,BottomToTop",
            ["DeploymentCheckBeforeDeploy"] = "bool", ["DeploymentAutoBackup"] = "bool",
            ["DeploymentOverwriteConflicts"] = "bool", ["DeploymentDeleteUnmanagedPatches"] = "bool",
            ["SearchFuzzy"] = "bool", ["SearchPinyin"] = "bool",
            ["ShowModThumbnails"] = "bool", ["ShowGpuUsage"] = "bool", ["ImportAutoClose"] = "bool",
            ["KeepModVersions"] = "bool", ["AllowDuplicateImports"] = "bool", ["UpdateSameGuidOnImport"] = "bool",
            ["LuaAutoExtract"] = "bool", ["ModNoticeStyle"] = "string", ["ModNoticeDismiss"] = "string",
            ["CompatibilityCheckOnStartup"] = "bool", ["CompatibilityEnableBatchRepair"] = "bool",
            ["CompatibilityAutoCheckImport"] = "bool",
            ["UpdatesCheckOnStartup"] = "bool", ["DownloadParallelSegments"] = "int",
            ["DownloadSegmentSizeMiB"] = "int", ["DownloadMaxRetries"] = "int",
            ["DownloadRetryDelayMs"] = "int", ["DownloadEnableResume"] = "bool",
            ["Hd2ClExportDirectory"] = "path",
            ["Language"] = "string", ["Theme"] = "string", ["ListDensity"] = "string",
            ["BackgroundImagePath"] = "string", ["BackgroundImageOpacity"] = "int",
            ["BackgroundBlur"] = "bool", ["CardOpacity"] = "int", ["GlassIntensity"] = "int",
            ["GlassBlur"] = "int", ["GlassLiveBackdrop"] = "bool", ["EnableAnimations"] = "bool",
            ["FontSource"] = "string", ["FontFamilyName"] = "string", ["FontFilePath"] = "string",
            ["AiTranslationTargetLanguage"] = "string", ["ForceManifestV1"] = "bool",
            ["ConcurrentImports"] = "int", ["ImageCacheSize"] = "int", ["VirtualizationThreshold"] = "int"
        };

        private static readonly HashSet<string> UiOnlyKeys = new(StringComparer.Ordinal)
        {
            "Language", "Theme", "ListDensity", "BackgroundImagePath", "BackgroundImageOpacity", "BackgroundBlur",
            "CardOpacity", "GlassIntensity", "GlassBlur", "GlassLiveBackdrop", "EnableAnimations",
            "FontSource", "FontFamilyName", "FontFilePath", "WindowWidth", "WindowHeight", "WindowMaximized",
            "ShowModThumbnails", "ShowGpuUsage", "ModNoticeStyle", "ModNoticeDismiss", "VirtualizationThreshold"
        };

        public static async Task<CliCommandResponse> ListAllAsync(CliCommandContext context, CancellationToken ct)
        {
            var settings = context.Get<SettingsService>();
            var entries = new List<object>();
            foreach (var key in KeyTypes.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                var value = settings.GetSetting<object>(key, null);
                entries.Add(new
                {
                    key,
                    type = KeyTypes[key],
                    value,
                    uiOnly = UiOnlyKeys.Contains(key)
                });
            }
            return CliCommandResponse.Ok(
                $"共 {entries.Count} 个可读写设置键（{UiOnlyKeys.Count} 个仅对 UI 立即生效）。", entries);
        }

        public static async Task<CliCommandResponse> GetAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: settings get <键>");
            var key = positional[0];
            if (!KeyTypes.ContainsKey(key))
                return CliCommandResponse.Fail($"设置键 {key} 不在白名单内。用 settings list 查看可用键。");
            var value = context.Get<SettingsService>().GetSetting<object>(key, null);
            return CliCommandResponse.Ok($"{key} = {value ?? "(未设置)"}", new { key, value });
        }

        public static async Task<CliCommandResponse> SetAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2) return CliCommandResponse.Usage("用法: settings set <键> <值>");
            var (key, rawValue) = (positional[0], positional[1]);
            if (!KeyTypes.TryGetValue(key, out var type))
                return CliCommandResponse.Fail($"设置键 {key} 不在白名单内。用 settings list 查看可用键。");

            object value;
            try
            {
                value = type switch
                {
                    "bool" => ParseBool(key, rawValue),
                    "int" => int.TryParse(rawValue, out var parsed) && parsed >= 0
                        ? parsed : throw new ArgumentException($"{key} 需要非负整数。"),
                    "path" => Directory.Exists(rawValue)
                        ? rawValue : throw new ArgumentException($"{key} 的目录不存在：{rawValue}"),
                    _ when type.StartsWith("enum:", StringComparison.Ordinal) => ParseEnum(key, type["enum:".Length..], rawValue),
                    _ => rawValue
                };
            }
            catch (ArgumentException ex)
            {
                return CliCommandResponse.Fail(ex.Message);
            }

            await context.Get<SettingsService>().SetSettingAsync(key, value);
            var hint = UiOnlyKeys.Contains(key) ? "（该键仅对 UI 生效，需重启界面或重新渲染才可见）" : "";
            return CliCommandResponse.Ok($"{key} 已设为 {value}{hint}", new { key, value });
        }

        public static async Task<CliCommandResponse> ResetAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            var category = positional.Count == 0 ? null : positional[0];
            var settings = context.Get<SettingsService>();
            if (category == null)
            {
                await settings.ResetToDefaultAsync();
                return CliCommandResponse.Ok("已恢复全部设置到默认值。");
            }
            var prefix = category.TrimEnd('.').ToLowerInvariant() + ".";
            var keys = KeyTypes.Keys.Where(key => key.ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (keys.Count == 0)
                return CliCommandResponse.Fail($"没有匹配前缀 {category} 的设置键；可用前缀：game / profiles / deployment / search / download / compatibility。");
            await settings.ResetCategoriesAsync(keys.ToArray());
            return CliCommandResponse.Ok($"已把 {keys.Count} 个 {category}* 键恢复默认。", new { keys });
        }

        private static object ParseBool(string key, string value) => value.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => throw new ArgumentException($"{key} 需要布尔值（true/false）。")
        };

        private static object ParseEnum(string key, string allowed, string value)
        {
            var options = allowed.Split(',');
            var match = options.FirstOrDefault(option =>
                string.Equals(option, value, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new ArgumentException($"{key} 可选值：{string.Join(" / ", options)}。");
            return match;
        }
    }

    /// <summary>game 域：启动游戏、自动检测、目录校验与版本查询。</summary>
    internal static class Game
    {
        public static async Task<CliCommandResponse> LaunchAsync(CliCommandContext context, CancellationToken ct)
        {
            var kind = await context.Get<GameDetectionService>().LaunchGameAsync(ct);
            return CliCommandResponse.Ok($"游戏启动请求已发出（{kind}）。", new { kind = kind.ToString() });
        }

        public static async Task<CliCommandResponse> DetectAsync(CliCommandContext context, CancellationToken ct)
        {
            await context.Get<GameDetectionService>().AutoDetectAsync(ct);
            var path = context.Get<SettingsService>().GetEffectiveGamePath();
            return CliCommandResponse.Ok(string.IsNullOrWhiteSpace(path)
                ? "自动检测未找到游戏目录。"
                : $"已检测到游戏目录：{path}", new { gamePath = path });
        }

        public static async Task<CliCommandResponse> ValidateAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: game validate <目录>");
            var valid = await context.Get<GameDetectionService>().ValidateGameDirectoryAsync(positional[0], ct);
            return valid
                ? CliCommandResponse.Ok($"{positional[0]} 是有效的游戏安装目录。", new { valid = true })
                : CliCommandResponse.Fail($"{positional[0]} 不是有效的游戏安装目录（缺可执行文件或目录结构不符）。");
        }

        public static async Task<CliCommandResponse> VersionAsync(CliCommandContext context, CancellationToken ct)
        {
            var gamePath = context.Get<SettingsService>().GetEffectiveGamePath();
            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                return CliCommandResponse.Fail($"游戏目录无效：{gamePath ?? "(未设置)"}。");
            var version = await context.Get<GameDetectionService>().GetGameVersionAsync(gamePath, ct);
            return CliCommandResponse.Ok($"游戏版本：{version ?? "(未知)"}", new { gamePath, version });
        }
    }

    /// <summary>backup 域：数据库备份的创建、列出、恢复与删除。</summary>
    internal static class Backup
    {
        public static async Task<CliCommandResponse> ListAsync(CliCommandContext context, CancellationToken ct)
        {
            var backups = await context.Get<BackupService>().ListBackupsAsync(ct);
            var data = backups.Select(entry => new
            {
                name = entry.Name,
                path = entry.FullPath,
                lastWriteTimeUtc = entry.LastWriteTimeUtc,
                sizeBytes = entry.SizeBytes
            }).ToList();
            var message = backups.Count == 0 ? "还没有备份。" : $"共 {backups.Count} 份备份。";
            return CliCommandResponse.Ok(message, data);
        }

        public static async Task<CliCommandResponse> CreateAsync(CliCommandContext context, CancellationToken ct)
        {
            var backup = await context.Get<BackupService>().CreateBackupAsync(ct);
            return CliCommandResponse.Ok($"已创建备份 {backup.Name}（{backup.SizeBytes} 字节）。", new { name = backup.Name, path = backup.FullPath });
        }

        public static async Task<CliCommandResponse> RestoreAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: backup restore <备份路径>（backup list 获取路径）");
            await context.Get<BackupService>().RestoreBackupAsync(positional[0], ct);
            return CliCommandResponse.Ok($"已从备份恢复：{positional[0]}。重启应用后生效。", new { path = positional[0] });
        }

        public static async Task<CliCommandResponse> DeleteAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: backup delete <备份路径>");
            await context.Get<BackupService>().DeleteBackupAsync(positional[0], ct);
            return CliCommandResponse.Ok($"已删除备份 {positional[0]}。", new { path = positional[0] });
        }
    }

    /// <summary>app 域：应用自更新检查与下载暂存。</summary>
    internal static class App
    {
        public static async Task<CliCommandResponse> UpdateCheckAsync(CliCommandContext context, CancellationToken ct)
        {
            var update = await context.Get<IAppUpdateService>().CheckAsync(ct);
            return update.HasUpdate
                ? CliCommandResponse.Ok($"发现新版本 {update.Info?.Version}（当前 {update.CurrentVersion}）。用 app update-apply 下载。",
                    new { hasUpdate = true, current = update.CurrentVersion, latest = update.Info?.Version, changelog = update.Info?.ChangelogBody })
                : CliCommandResponse.Ok($"已是最新版本（{update.CurrentVersion}）。", new { hasUpdate = false });
        }

        public static async Task<CliCommandResponse> UpdateApplyAsync(CliCommandContext context, CancellationToken ct)
        {
            var appUpdate = context.Get<IAppUpdateService>();
            var update = await appUpdate.CheckAsync(ct);
            if (!update.HasUpdate)
                return CliCommandResponse.Ok($"已是最新版本（{update.CurrentVersion}），无需更新。");
            var session = context.Session;
            var staging = await appUpdate.PrepareAsync(update.Info!, ToDownloadProgress(session), ct);
            return CliCommandResponse.Ok(
                $"新版本已下载暂存：{staging.StagingDirectory}。退出应用后运行安装脚本，或用 GUI 的“立即重启安装”。",
                new { version = staging.Version, stagingDirectory = staging.StagingDirectory, installerScript = staging.InstallerScriptPath });
        }

        private static IProgress<DownloadProgress>? ToDownloadProgress(CliCommandSession session)
            => new Progress<DownloadProgress>(value =>
                session.Progress.Report(new CliProgressEvent("下载更新", value.Percentage, null, value.StatusMessage)));
    }
}
