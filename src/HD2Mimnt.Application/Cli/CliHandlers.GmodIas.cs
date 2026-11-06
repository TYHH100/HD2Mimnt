using System.Globalization;
using HD2Mimnt.Application.Services;
using HD2Mimnt.ModelConversion.Gmod.Physics;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source;

namespace HD2Mimnt.Application.Cli;

internal static partial class CliHandlers
{
    internal static class GmodIas
    {
        /// <summary>列出 addon 内可转换的模型，供选择 <c>gmod-ias convert</c> 的模型参数。</summary>
        public static async Task<CliCommandResponse> ListAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var showGroups = args.Flag("--groups");
            var positional = args.Positionals();
            var unknown = args.UnknownFlags();
            if (unknown.Count > 0) return CliCommandResponse.Usage("未知选项：" + string.Join(' ', unknown));
            if (positional.Count != 1)
                return CliCommandResponse.Usage("用法: gmod-ias list <addon.gma|目录> [--groups]");
            var addon = positional[0];
            if (!Directory.Exists(addon) && !File.Exists(addon))
                return CliCommandResponse.Fail($"找不到 addon：{addon}");

            try
            {
                var analysis = await new GmodIasConversionService().AnalyzeAsync(addon, ct).ConfigureAwait(false);
                var convertible = analysis.Models.Where(model => model.IsConvertible).ToArray();
                var skipped = analysis.Models.Where(model => !model.IsConvertible).ToArray();
                var lines = new List<string>
                {
                    $"addon：{addon}",
                    $"可转换模型 {convertible.Length} 个；不可转换 {skipped.Length} 个；诊断 {analysis.Issues.Count} 条"
                };
                foreach (var model in convertible)
                {
                    lines.Add($"  {model.Path}  [骨骼 {model.BoneCount} · 网格 {model.MeshCount} · " +
                              $"开关 {model.BodyGroupCount}/{model.BodyGroupOptionCount} · skin {model.SkinCount} · " +
                              $"动骨 {model.JiggleBoneCount} · 材质 {model.MaterialCount}" +
                              (model.HasPhysics ? " · 有 .phy" : "") + "]");
                    if (!showGroups) continue;
                    foreach (var group in model.BodyGroups.Where(group => group.Options.Count > 1))
                    {
                        var options = string.Join(" | ", group.Options.Select((option, index) => $"{index}:{option}"));
                        lines.Add($"      bodygroup:{group.Index}  {group.Name}  ({group.Options.Count})  {options}");
                    }
                }
                foreach (var model in skipped)
                {
                    var missing = (model.HasVertexData ? "" : " 缺 VVD") + (model.HasIndexData ? "" : " 缺 VTX");
                    lines.Add($"  (跳过) {model.Path}{missing}");
                }
                foreach (var issue in analysis.Issues) lines.Add($"  ! {issue.Code}: {issue.Asset} — {issue.Message}");

                var data = new
                {
                    addon,
                    convertibleCount = convertible.Length,
                    skippedCount = skipped.Length,
                    models = convertible.Select(model => new
                    {
                        model.Path, model.Name, model.BoneCount, model.MeshCount, model.MaterialCount,
                        model.BodyGroupCount, model.BodyGroupOptionCount, model.SkinCount,
                        model.JiggleBoneCount, model.HasPhysics, model.BodyGroups
                    }).ToArray(),
                    skipped = skipped.Select(model => new
                    {
                        model.Path, model.HasVertexData, model.HasIndexData
                    }).ToArray(),
                    analysis.Issues
                };
                return CliCommandResponse.Ok(string.Join("\n", lines), data);
            }
            catch (Exception error) when (error is SourceFormatException or InvalidDataException or
                IOException or UnauthorizedAccessException or ArgumentException)
            {
                return CliCommandResponse.Fail($"分析失败：{error.Message}");
            }
        }

        /// <summary>列出可作为替换目标的身体护甲与头盔，供 <c>convert --armor</c> 使用。</summary>
        public static Task<CliCommandResponse> ArmorsAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var domain = args.Option("--domain");
            var filter = args.Option("--filter");
            var unknown = args.UnknownFlags();
            if (unknown.Count > 0) return Task.FromResult(CliCommandResponse.Usage("未知选项：" + string.Join(' ', unknown)));
            if (args.Positionals().Count > 0)
                return Task.FromResult(CliCommandResponse.Usage("用法: gmod-ias armors [--domain body|helmet] [--filter 关键字]"));

            var parsedDomain = ParseDomain(domain);
            if (parsedDomain is null) return Task.FromResult(CliCommandResponse.Usage("--domain 只能是 body 或 helmet。"));
            var catalog = GmodArmorTargetService.TryLoadDefault();
            if (catalog is null)
                return Task.FromResult(CliCommandResponse.Fail(
                    $"找不到目标护甲目录：{GmodArmorTargetService.DefaultPath}"));

            var targets = catalog.Targets
                .Where(target => target.Domain == parsedDomain.Value)
                .Where(target => string.IsNullOrWhiteSpace(filter) ||
                    target.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    target.ArchiveHex.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(target => target.Code, StringComparer.OrdinalIgnoreCase)
                .ThenBy(target => target.ArchiveId)
                .ToArray();
            var lines = new List<string>
            {
                $"目标护甲目录：{catalog.Path}",
                $"{(parsedDomain.Value == GmodIasDomain.Body ? "身体护甲" : "头盔")} {targets.Length} 套" +
                    (string.IsNullOrWhiteSpace(filter) ? "" : $"（筛选：{filter}）")
            };
            foreach (var target in targets)
            {
                lines.Add($"  {target.DisplayName}  {target.ArchiveHex}  Unit {target.UnitIds.Count} 套" +
                    $"  equipment 0x{target.EquipmentKeys[0]:X8}  载体 0x{target.CarrierUnitId:x16}");
            }
            var data = new
            {
                catalog = catalog.Path,
                domain = parsedDomain.Value.ToString().ToLowerInvariant(),
                count = targets.Length,
                targets = targets.Select(target => new
                {
                    code = target.Code,
                    name = target.Name,
                    displayName = target.DisplayName,
                    archive = target.ArchiveHex,
                    equipment = target.EquipmentKeys.Select(key => "0x" + key.ToString("X8", CultureInfo.InvariantCulture)).ToArray(),
                    carrier = "0x" + target.CarrierUnitId.ToString("x16", CultureInfo.InvariantCulture),
                    unitCount = target.UnitIds.Count
                }).ToArray()
            };
            return Task.FromResult(CliCommandResponse.Ok(string.Join("\n", lines), data));
        }

        public static async Task<CliCommandResponse> ConvertAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var project = args.Option("--project");
            var armor = args.Option("--armor");
            var helmetArmor = args.Option("--helmet-armor");
            var archive = args.Option("--archive");
            var domain = args.Option("--domain");
            var equipment = args.Option("--equipment");
            var native = args.Option("--native");
            var switchMode = args.Option("--switch-mode");
            var split = args.Option("--split");
            var helmetArchive = args.Option("--helmet-archive");
            var switches = args.Option("--switches");
            var noSkin = args.Flag("--no-skin");
            var noPhysics = args.Flag("--no-physics");
            var noSource = args.Flag("--no-source");
            var positional = args.Positionals();
            var unknown = args.UnknownFlags();
            if (unknown.Count > 0) return CliCommandResponse.Usage("未知选项：" + string.Join(' ', unknown));
            if (positional.Count != 3)
                return CliCommandResponse.Usage("用法: gmod-ias convert <addon.gma|目录> <model.mdl> <输出目录> [选项]");
            var (addon, model, output) = (positional[0], positional[1], positional[2]);
            if (!Directory.Exists(addon) && !File.Exists(addon))
                return CliCommandResponse.Fail($"找不到 addon：{addon}");

            var projectId = string.IsNullOrWhiteSpace(project) ? GmodIasConversionService.DeriveProjectId(model) : project.Trim();
            if (!GmodIasConversionService.TryParseHexId(archive, out var archiveId))
                archiveId = GmodIasConversionService.DeriveArchiveId(projectId, model);
            if (archiveId == 0) archiveId = 0x9E3779B97F4A7C15UL;
            if (!GmodIasConversionService.TryParseHexId(equipment, out var equipmentValue)) equipmentValue = 1;
            if (equipmentValue is 0 or > uint.MaxValue)
                return CliCommandResponse.Usage("--equipment 必须是非零的 32 位十六进制值。");
            if (!GmodIasConversionService.TryParseNativeIds(native, out var nativeIds)) nativeIds = [archiveId];
            var parsedDomain = ParseDomain(domain);
            if (parsedDomain is null)
                return CliCommandResponse.Usage("--domain 只能是 body 或 helmet。");
            var parsedMode = ParseMode(switchMode);
            if (parsedMode is null)
                return CliCommandResponse.Usage("--switch-mode 只能是 auto、independent 或 composite。");
            var parsedSplit = ParseSplit(split);
            if (parsedSplit is null)
                return CliCommandResponse.Usage("--split 只能是 both、body、helmet 或 none。");
            // 目标护甲：Archive ID / equipment / Unit 闭包全部由目录提供，用户只选游戏里的护甲。
            GmodIasArmorTarget? bodyTarget = null;
            GmodIasArmorTarget? helmetTarget = null;
            if (!string.IsNullOrWhiteSpace(armor) || !string.IsNullOrWhiteSpace(helmetArmor))
            {
                var catalog = GmodArmorTargetService.TryLoadDefault();
                if (catalog is null)
                    return CliCommandResponse.Fail($"找不到目标护甲目录：{GmodArmorTargetService.DefaultPath}");
                if (!string.IsNullOrWhiteSpace(armor))
                {
                    bodyTarget = catalog.Find(armor);
                    if (bodyTarget is null)
                        return CliCommandResponse.Usage($"没有找到身体护甲：{armor}（用 gmod-ias armors 查看可选项）。");
                    if (bodyTarget.Domain != GmodIasDomain.Body)
                        return CliCommandResponse.Usage($"{armor} 是头盔，请用 --helmet-armor 指定。");
                }
                if (!string.IsNullOrWhiteSpace(helmetArmor))
                {
                    helmetTarget = catalog.Find(helmetArmor);
                    if (helmetTarget is null)
                        return CliCommandResponse.Usage($"没有找到头盔：{helmetArmor}（用 gmod-ias armors --domain helmet 查看可选项）。");
                    if (helmetTarget.Domain != GmodIasDomain.Helmet)
                        return CliCommandResponse.Usage($"{helmetArmor} 是身体护甲，请用 --armor 指定。");
                }
                var seed = bodyTarget ?? helmetTarget!;
                archiveId = seed.ArchiveId;
                equipmentValue = seed.EquipmentKeys[0];
                nativeIds = seed.UnitIds;
            }
            ulong? helmetArchiveId = null;
            if (!string.IsNullOrWhiteSpace(helmetArchive))
            {
                if (!GmodIasConversionService.TryParseHexId(helmetArchive, out var value) || value == 0)
                    return CliCommandResponse.Usage("--helmet-archive 必须是非零的 64 位十六进制值。");
                helmetArchiveId = value;
            }
            string[]? exposedSwitches = null;
            if (!string.IsNullOrWhiteSpace(switches))
            {
                if (!GmodIasConversionService.TryParseSwitchKeys(switches, out var parsedSwitches))
                    return CliCommandResponse.Usage("--switches 只能是 skin 或 bodygroup:编号，多个用逗号分隔（用 gmod-ias list --groups 查看编号）。");
                exposedSwitches = parsedSwitches.ToArray();
            }

            try
            {
                var outcome = await new GmodIasConversionService().ConvertAsync(
                    new GmodIasConversionRequest(addon, model, output, projectId, archiveId,
                        parsedDomain.Value, (uint)equipmentValue, nativeIds, parsedMode.Value,
                        !noSkin, !noPhysics, !noSource, parsedSplit.Value, helmetArchiveId, exposedSwitches,
                        BodyArmor: bodyTarget, HelmetArmor: helmetTarget), ct).ConfigureAwait(false);
                var report = outcome.Report;
                var data = new
                {
                    output = outcome.OutputDirectory,
                    fileCount = outcome.FileCount,
                    projectId,
                    archive = "0x" + archiveId.ToString("x16", CultureInfo.InvariantCulture),
                    nativeUnitIds = nativeIds.Select(id => "0x" + id.ToString("x16", CultureInfo.InvariantCulture)).ToArray(),
                    split = parsedSplit.Value.ToString().ToLowerInvariant(),
                    parts = report.Parts,
                    report.SwitchCount, report.OptionCount, report.MeshCount, report.VertexCount,
                    report.TriangleCount, report.MaterialCount, report.TextureCount,
                    report.DecodedTextureCount, report.PhysicsChainCount, report.PhysicsProfileCount,
                    report.RigProfileCount, report.RawSourceFileCount, report.Issues
                };
                var lines = new List<string>
                {
                    $"转换完成：{outcome.FileCount} 个文件 → {outcome.OutputDirectory}",
                    $"模型：骨骼 {report.BoneCount}、材质/贴图 {report.MaterialCount} / {report.TextureCount}（解码 {report.DecodedTextureCount}）"
                };
                foreach (var part in report.Parts)
                {
                    var prefix = part.Part.Length == 0 ? "(根目录)" : part.Part + "/";
                    lines.Add($"  {prefix} 域 {part.Domain} · archive {part.Archive} · 网格 {part.MeshCount} · " +
                              $"三角面 {part.TriangleCount} · 开关 {part.SwitchCount}/{part.OptionCount} · " +
                              $"动骨资源 {part.PhysicsProfileCount}");
                    lines.Add($"    目标护甲：{(part.TargetArmor.Length == 0 ? "未选择（路由指向手填 ID，游戏中不会挂到任何护甲）" : part.TargetArmor)}" +
                              (part.CleanupUnitCount > 0 ? $"；其余 {part.CleanupUnitCount} 个甲片槽位写成零面积占位" : ""));
                    lines.Add($"    {prefix}package/ 可运行数据包：Unit {part.UnitCount} · 材质 {part.MaterialResourceCount} · " +
                              $"贴图 {part.TextureResourceCount} · 补丁 {FormatSize(part.PackageBytes)}" +
                              "（模组库导入该文件夹即可部署）");
                }
                if (report.RuntimePack.Length > 0)
                    lines.Add($"独立骨架配套包：{report.RuntimePack}（{FormatSize(report.RuntimePackBytes)}；" +
                              "含 rigbin / 物理 / 差分与隔离清单，导入部位目录时会与 data 模组一起部署）");
                lines.Add($"原始 addon 文件：{report.RawSourceFileCount}，提示 {report.Issues.Count} 条（详见 conversion-report.json）");
                return CliCommandResponse.Ok(string.Join("\n", lines), data);
            }
            catch (Exception error) when (error is SourceFormatException or IasFormatException or
                Hd2PhysFormatException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return CliCommandResponse.Fail($"转换失败：{error.Message}");
            }
        }

        /// <summary>补丁体积按人类可读单位显示；0 字节时显式说明没有载荷。</summary>
        private static string FormatSize(long bytes) => bytes switch
        {
            <= 0 => "0 B",
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.0} KiB",
            _ => $"{bytes / (1024.0 * 1024.0):0.0} MiB"
        };

        private static GmodIasDomain? ParseDomain(string? value) => value?.ToLowerInvariant() switch        {
            null or "body" => GmodIasDomain.Body,
            "helmet" => GmodIasDomain.Helmet,
            _ => null
        };

        private static GmodIasSwitchMode? ParseMode(string? value) => value?.ToLowerInvariant() switch
        {
            null or "auto" => GmodIasSwitchMode.Auto,
            "independent" => GmodIasSwitchMode.Independent,
            "composite" => GmodIasSwitchMode.Composite,
            _ => null
        };

        private static GmodIasPartSplit? ParseSplit(string? value) => value?.ToLowerInvariant() switch
        {
            null or "both" => GmodIasPartSplit.Both,
            "body" => GmodIasPartSplit.Body,
            "helmet" => GmodIasPartSplit.Helmet,
            "none" => GmodIasPartSplit.None,
            _ => null
        };

    }
}
