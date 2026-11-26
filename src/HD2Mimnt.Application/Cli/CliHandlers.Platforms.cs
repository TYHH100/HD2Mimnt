using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;

namespace HD2Mimnt.Application.Cli;

internal static partial class CliHandlers
{
    /// <summary>browse 域：平台切换、列表/搜索、详情与直接下载导入。</summary>
    internal static class Browse
    {
        public static async Task<CliCommandResponse> PlatformsAsync(CliCommandContext context, CancellationToken ct)
        {
            var browse = context.Get<ModBrowseService>();
            var platforms = context.Get<IPlatformRegistry>().GetAllPlatforms();
            var data = platforms.Select(platform => new { id = platform.Id, name = platform.Name }).ToList();
            return CliCommandResponse.Ok(
                "可用平台：" + string.Join("、", platforms.Select(platform => platform.Id)), data);
        }

        public static async Task<CliCommandResponse> SelectAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: browse select <平台ID>（browse platforms 查看）");
            context.Get<ModBrowseService>().SelectPlatform(positional[0]);
            return CliCommandResponse.Ok($"当前平台已切换为 {positional[0]}。", new { platform = positional[0] });
        }

        public static async Task<CliCommandResponse> SearchAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var page = int.TryParse(args.Option("--page"), out var parsedPage) ? Math.Max(1, parsedPage) : 1;
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: browse search <关键字> [--page 页码]");
            var result = await context.Get<ModBrowseService>().SearchAsync(positional[0], page, ct);
            var data = result.Mods.Select(mod => new
            {
                modId = mod.Id,
                name = mod.Name,
                description = mod.Description
            }).ToList();
            var message = data.Count == 0
                ? $"没有匹配“{positional[0]}”的结果（第 {page} 页）。"
                : $"第 {page} 页共 {data.Count} 条结果：\n  " + string.Join("\n  ", data.Select(item => $"{item.name} [{item.modId}]"));
            return CliCommandResponse.Ok(message, data);
        }

        public static async Task<CliCommandResponse> DetailsAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: browse details <平台模组ID或链接>");
            var browse = context.Get<ModBrowseService>();
            var modId = browse.ParseModId(positional[0]);
            var details = await browse.GetDetailsAsync(modId, ct);
            var data = new
            {
                modId,
                name = details.Mod.Name,
                description = details.Mod.Description,
                pageUrl = browse.GetModPageUrl(modId),
                files = details.Files.Select(file => new
                {
                    fileId = file.Id,
                    name = file.Name,
                    version = file.Version,
                    sizeBytes = file.Size
                })
            };
            return CliCommandResponse.Ok($"{details.Mod.Name}（{details.Files.Count} 个文件）", data);
        }

        public static async Task<CliCommandResponse> DownloadAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var fileIndex = int.TryParse(args.Option("--file"), out var parsedFile) ? parsedFile : 0;
            var positional = args.Positionals();
            if (positional.Count != 1)
                return CliCommandResponse.Usage("用法: browse download <平台模组ID或链接> [--file 文件序号]（下载并导入）");
            var browse = context.Get<ModBrowseService>();
            var modId = browse.ParseModId(positional[0]);
            var details = await browse.GetDetailsAsync(modId, ct);
            if (details.Files.Count == 0) return CliCommandResponse.Fail($"{details.Mod.Name} 没有可下载文件。");
            var file = details.Files[Math.Clamp(fileIndex, 0, details.Files.Count - 1)];
            var session = context.Session;
            var progress = new Progress<HD2Mimnt.Core.Entities.DownloadProgress>(value =>
                session.Progress.Report(new CliProgressEvent("下载并导入", value.Percentage, file.Name, value.StatusMessage)));
            var mods = context.Get<ModService>();
            // 下载产物经标准导入链路入库（与 GUI 的导入回调一致）。
            await browse.DownloadAndImportAsync(modId, file, null,
                async (archivePath, token) => await mods.ImportModAsync(archivePath, token), progress, ct);
            return CliCommandResponse.Ok($"下载并导入完成：{details.Mod.Name} / {file.Name}",
                new { modId, file = file.Name });
        }
    }

    /// <summary>download 域：直链下载与下载管理。</summary>
    internal static class Download
    {
        public static async Task<CliCommandResponse> DirectAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var outDir = args.Option("--out");
            var positional = args.Positionals();
            if (positional.Count != 1) return CliCommandResponse.Usage("用法: download direct <URL> [--out 目标目录]");
            var link = positional[0];
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return CliCommandResponse.Fail($"不是有效的 http(s) 链接：{link}");
            var downloads = context.Get<DirectDownloadService>();
            var fileName = DirectDownloadService.SanitizeFileName(Path.GetFileName(uri.AbsolutePath));
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "download.bin";
            var destination = outDir ?? downloads.GetDestination();
            if (string.IsNullOrWhiteSpace(destination))
                return CliCommandResponse.Fail("没有下载目标目录：用 --out 指定，或先在 GUI 直链下载页保存偏好。");
            var session = context.Session;
            var progress = new Progress<HD2Mimnt.Core.Entities.DownloadProgress>(value =>
                session.Progress.Report(new CliProgressEvent("直链下载", value.Percentage, null, value.StatusMessage)));
            var path = await downloads.DownloadAsync(link, fileName, destination, downloads.GetProxy(), null, progress, null, ct);
            return CliCommandResponse.Ok($"下载完成：{path}", new { path });
        }
    }

    /// <summary>account 域：Nexus / Ayaka 的 API Key 与 OAuth 登录入口。</summary>
    internal static class Account
    {
        public static async Task<CliCommandResponse> NexusStatusAsync(CliCommandContext context, CancellationToken ct)
        {
            var auth = context.Get<NexusModsAuthService>();
            try { await auth.RefreshRateLimitAsync(ct); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                return CliCommandResponse.Fail("Nexus 凭据无效或网络失败：" + ex.Message);
            }
            return CliCommandResponse.Ok("Nexus API 凭据有效。");
        }

        public static async Task<CliCommandResponse> NexusApiKeyAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var key = args.Option("--key") ?? ReadStdin("粘贴 Nexus API Key：");
            if (string.IsNullOrWhiteSpace(key)) return CliCommandResponse.Fail("未提供 API Key。");
            await context.Get<NexusModsAuthService>().ApplyApiKeyAsync(key, ct);
            return CliCommandResponse.Ok("Nexus API Key 已保存。");
        }

        public static async Task<CliCommandResponse> NexusLogoutAsync(CliCommandContext context, CancellationToken ct)
        {
            await context.Get<NexusModsAuthService>().LogoutAsync(ct);
            return CliCommandResponse.Ok("已退出 Nexus 登录。");
        }

                public static async Task<CliCommandResponse> AyakaStatusAsync(CliCommandContext context, CancellationToken ct)
            => CliCommandResponse.Ok("Ayaka 凭据状态见 settings/账号页；CLI 支持 api-key 路径。");

        public static async Task<CliCommandResponse> AyakaApiKeyAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var key = args.Option("--key") ?? ReadStdin("粘贴 Ayaka API Key：");
            if (string.IsNullOrWhiteSpace(key)) return CliCommandResponse.Fail("未提供 API Key。");
            await context.Get<AyakaModsAuthService>().ApplyApiKeyAsync(key, ct);
            return CliCommandResponse.Ok("Ayaka API Key 已保存。");
        }

        public static async Task<CliCommandResponse> AyakaLogoutAsync(CliCommandContext context, CancellationToken ct)
        {
            await context.Get<AyakaModsAuthService>().ClearApiKeyAsync(ct);
            return CliCommandResponse.Ok("已清除 Ayaka API Key。");
        }

                private static string? ReadStdin(string prompt)
        {
            Console.Error.Write(prompt);
            var line = Console.ReadLine();
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
    }

    /// <summary>tool 域：外部工具注册/启动、音效修复与 Lua 提取。</summary>
    internal static class Tools
    {
        public static async Task<CliCommandResponse> ListAsync(CliCommandContext context, CancellationToken ct)
        {
            var tools = await context.Get<IExternalToolService>().GetAllToolsAsync(ct);
            var data = tools.Select(tool => new
            {
                id = tool.Id,
                name = tool.Name,
                path = tool.ExecutablePath,
                enabled = tool.IsEnabled
            }).ToList();
            return CliCommandResponse.Ok(tools.Count == 0 ? "没有注册的外部工具。" : $"共 {tools.Count} 个外部工具：\n  " +
                string.Join("\n  ", tools.Select(tool => $"{(tool.IsEnabled ? "●" : "○")} {tool.Name} [{tool.Id}]")), data);
        }

        public static async Task<CliCommandResponse> RegisterAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 2 || !File.Exists(positional[1]))
                return CliCommandResponse.Fail("用法: tool register <名称> <exe 路径>（exe 需存在）");
            var tool = new ExternalTool
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = positional[0],
                ExecutablePath = positional[1]
            };
            var result = await context.Get<IExternalToolService>().RegisterToolAsync(tool, ct);
            return result.Success
                ? CliCommandResponse.Ok($"已注册工具 {positional[0]}（{tool.Id}）。", new { toolId = tool.Id })
                : CliCommandResponse.Fail("注册失败：" + result.Message);
        }

        public static async Task<CliCommandResponse> DeleteAsync(CliCommandContext context, CancellationToken ct)
        {
            var toolId = RequireId(context, "tool delete <工具ID>");
            var result = await context.Get<IExternalToolService>().DeleteToolAsync(toolId, ct);
            return result.Success
                ? CliCommandResponse.Ok($"已删除工具 {toolId}。")
                : CliCommandResponse.Fail("删除失败：" + result.Message);
        }

        public static async Task<CliCommandResponse> LaunchAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count < 1) return CliCommandResponse.Usage("用法: tool launch <工具ID> [关联文件路径]");
            var result = await context.Get<IExternalToolService>()
                .LaunchToolAsync(positional[0], positional.Count > 1 ? positional[1] : "", ct);
            return result.Success
                ? CliCommandResponse.Ok($"工具已启动。")
                : CliCommandResponse.Fail("启动失败：" + result.Message);
        }

        public static async Task<CliCommandResponse> SoundFixScanAsync(CliCommandContext context, CancellationToken ct)
        {
            var reports = await context.Get<SoundFixService>().ScanLibraryAsync(token: ct);
            var data = reports.Select(report => new
            {
                modId = report.ModId,
                modName = report.Name,
                canRepair = report.CanRepair,
                errors = report.Errors
            }).ToList();
            return CliCommandResponse.Ok(reports.Count == 0
                ? "全部模组的音效结构正常。"
                : $"发现 {reports.Count} 个模组的音效结构问题。", data);
        }

        public static async Task<CliCommandResponse> SoundFixRepairAsync(CliCommandContext context, CancellationToken ct)
        {
            var reports = await context.Get<SoundFixService>().ScanLibraryAsync(token: ct);
            if (reports.Count == 0) return CliCommandResponse.Ok("没有需要修复的音效结构问题。");
            var results = new List<string>();
            foreach (var report in reports)
            {
                var result = await context.Get<SoundFixService>().RepairAsync(report, ct);
                results.Add($"{(result.Error == null ? "✓" : "✗")} {report.Name}: {result.Error ?? (result.Changed ? "已修复" : "无需改动")}");
            }
            return CliCommandResponse.Ok($"音效修复完成：\n  " + string.Join("\n  ", results), results);
        }

        public static async Task<CliCommandResponse> LuaExtractAsync(CliCommandContext context, CancellationToken ct)
        {
            var summary = await context.Get<LuaExtractionService>().ExtractForLibraryAsync(ct);
            return CliCommandResponse.Ok($"Lua 提取完成：处理 {summary.ProcessedMods} 个模组（字节码 {summary.BytecodeCount}）。", new
            {
                processedMods = summary.ProcessedMods,
                sources = summary.SourceCount,
                bytecodes = summary.BytecodeCount
            });
        }

        private static string RequireId(CliCommandContext context, string usage)
        {
            var positional = context.Request.Arguments.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (positional.Count == 0) throw new ArgumentException("用法: " + usage);
            return positional[0];
        }
    }

    /// <summary>migration 域：从外部管理器导入 / 导出到外部管理器（预览 + 执行）。</summary>
    internal static class Migration
    {
        public static async Task<CliCommandResponse> ImportPreviewAsync(CliCommandContext context, CancellationToken ct)
        {
            var source = ResolveSource(context, "migration import preview --manager hd2mm|echelon|arsenal <数据目录>");
            var plan = await context.Get<ExternalManagerMigrationService>().PreviewAsync(source, ct);
            return CliCommandResponse.Ok(DescribePlan("导入", plan), plan);
        }

        public static async Task<CliCommandResponse> ImportExecuteAsync(CliCommandContext context, CancellationToken ct)
        {
            var source = ResolveSource(context, "migration import execute --manager hd2mm|echelon|arsenal <数据目录>");
            var service = context.Get<ExternalManagerMigrationService>();
            var plan = await service.PreviewAsync(source, ct);
            var result = await service.MigrateAsync(plan, null, ct);
            return CliCommandResponse.Ok(
                $"导入完成：新导入 {result.ImportedMods} 个模组、复用 {result.ReusedMods}；配置新导入 {result.ImportedProfiles}、复用 {result.ReusedProfiles}。",
                result);
        }

        public static async Task<CliCommandResponse> ExportPreviewAsync(CliCommandContext context, CancellationToken ct)
        {
            var source = ResolveSource(context, "migration export preview --manager hd2mm|echelon|arsenal <目标管理器目录>");
            var plan = await context.Get<ExternalManagerExportService>().PreviewAsync(source, ct);
            return CliCommandResponse.Ok(DescribePlan("导出", plan), plan);
        }

        public static async Task<CliCommandResponse> ExportExecuteAsync(CliCommandContext context, CancellationToken ct)
        {
            var source = ResolveSource(context, "migration export execute --manager hd2mm|echelon|arsenal <目标管理器目录>");
            var service = context.Get<ExternalManagerExportService>();
            var plan = await service.PreviewAsync(source, ct);
            var result = await service.MigrateAsync(plan, null, ct);
            return CliCommandResponse.Ok(
                $"导出完成：新导出 {result.ImportedMods} 个模组、复用 {result.ReusedMods}；配置 {result.ImportedProfiles}。",
                result);
        }

        /// <summary>解析 --manager 与数据目录为 ExternalMigrationSource（Manager + DataPath）。</summary>
        private static ExternalMigrationSource ResolveSource(CliCommandContext context, string usage)
        {
            var args = new CliArgs(context.Request.Arguments);
            var managerValue = args.Option("--manager");
            var positional = args.Positionals();
            if (positional.Count != 1 || managerValue == null)
                throw new ArgumentException("用法: " + usage);
            var kind = managerValue.ToLowerInvariant() switch
            {
                "hd2mm" or "helldivers2modmanager" => ExternalManagerKind.Helldivers2ModManager,
                "echelon" => ExternalManagerKind.Echelon,
                "arsenal" or "hd2arsenal" => ExternalManagerKind.HD2Arsenal,
                _ => throw new ArgumentException("manager 可选 hd2mm / echelon / arsenal。")
            };
            if (!Directory.Exists(positional[0]))
                throw new ArgumentException($"目录不存在：{positional[0]}");
            return new ExternalMigrationSource(kind, positional[0]);
        }

        private static string DescribePlan(string direction, ExternalMigrationPlan plan)
            => $"{direction}预览：{plan.Mods.Count} 个模组、{plan.Profiles.Count} 个配置、{plan.Issues.Count} 条注意事项（详见 --json 的 data）";
    }

    /// <summary>mod create 域：从目录生成模组清单并打包导出。</summary>
    internal static class Creation
    {
        public static async Task<CliCommandResponse> FromDirAsync(CliCommandContext context, CancellationToken ct)
        {
            // SaveAsync 的约束：manifest 必须写到 <源目录>/manifest.json，不接受外部输出路径。
            var result = await BuildFromDirAsync(context, "mod create from-dir <模组目录> [--name 名称] [--author 作者]", ct);
            if (result.Response != null) return result.Response;
            var creation = context.Get<ModCreationService>();
            creation.Validate(result.Document!);
            var outputPath = Path.Combine(result.SourceDir!, "manifest.json");
            await creation.SaveAsync(result.Document!, outputPath, ct);
            return CliCommandResponse.Ok($"已生成并保存 manifest：{outputPath}", new { path = outputPath, name = result.Document!.Manifest.Name });
        }

        public static async Task<CliCommandResponse> PackAsync(CliCommandContext context, CancellationToken ct)
        {
            var result = await BuildFromDirAsync(context, "mod create pack <模组目录> <输出压缩包路径> [--name 名称] [--author 作者]", ct);
            if (result.Response != null) return result.Response;
            var creation = context.Get<ModCreationService>();
            creation.Validate(result.Document!);
            await creation.ExportArchiveAsync(result.Document!, result.OutputPath!, ct);
            return CliCommandResponse.Ok($"已打包：{result.OutputPath}", new { path = result.OutputPath, name = result.Document!.Manifest.Name });
        }

        /// <summary>from-dir/pack 共用：读目录 → 组装文档（--name/--author 填充元数据，缺省用目录名）。</summary>
        private static async Task<(ModCreationDocument? Document, string? SourceDir, string? OutputPath, CliCommandResponse? Response)>
            BuildFromDirAsync(CliCommandContext context, string usage, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var name = args.Option("--name");
            var author = args.Option("--author");
            var positional = args.Positionals();
            if (positional.Count != 1 || !Directory.Exists(positional[0]))
                return (null, null, null, CliCommandResponse.Usage("用法: " + usage + "（目录需存在）"));
            var creation = context.Get<ModCreationService>();
            var directory = await creation.ReadDirectoryAsync(positional[0], ct);
            var document = creation.Create();
            document.Manifest.Name = name ?? directory.Name ?? "未命名模组";
            document.Manifest.Author = author ?? "";
            document.SourceDirectory = positional[0];
            document.Manifest.Options = creation.CreateDirectoryOptions(directory, directory.Name ?? "mod");
            return (document, positional[0], null, null);
        }
    }
}
