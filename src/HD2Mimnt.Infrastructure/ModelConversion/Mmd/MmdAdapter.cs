using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HD2Mimnt.Infrastructure.ModelConversion.Mmd;

public sealed record MmdResourceOverride(string Type, string Id, string Later);

public sealed record MmdInspectionTrack(
    string Id, string Title, int Fps, double Duration, string IdlePlayback, int Frames,
    bool Music, bool Camera, IReadOnlyList<string> OriginalSlots, string Playback);

public sealed record MmdInspection(
    string Kind, string Name, string Source, IReadOnlyList<MmdSourceOption> SourceOptions,
    string? SelectedOption, string IdlePlayback, string Status, IReadOnlyList<MmdInspectionTrack> Tracks,
    IReadOnlyList<string> Warnings, IReadOnlyList<MmdResourceOverride> Overrides,
    int OutputNativeReplacements, IReadOnlyList<string> UnreferencedPatches, bool HasPreview);

public sealed record MmdConvertResult(
    string OutputDirectory, string Id, string Title, string Delivery, int Fps, string IdlePlayback,
    int Frames, double Duration, IReadOnlyList<string> OriginalSlots, int Resources);

public sealed class MmdSpec
{
    public IReadOnlyList<MmdBlob>? Blobs { get; init; }
    public IReadOnlyList<string> Clips { get; init; } = [];
    public string Module { get; init; } = string.Empty;
    public IReadOnlySet<string> Requested { get; init; } = new HashSet<string>();
    public int Fps { get; init; } = 30;
    public double Duration { get; init; }
    public IReadOnlyList<double> Stage { get; init; } = [0, 0, 0];
    public byte[]? Camera { get; init; }
    public MmdMdl3Audio? Mdl3Audio { get; init; }
    public (ulong Type, ulong Id)? AudioIndex { get; init; }
    public (ulong Type, ulong Id)? AudioBlob { get; init; }
}

/// <summary>一次检查得到的可复用结果；只存在于 Worker 进程内，不跨进程序列化。</summary>
public sealed class MmdPlan
{
    public required MmdSourceDocument? Source { get; init; }
    public IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> Entries { get; init; }
        = new Dictionary<(ulong, ulong), IasResource>();
    public IasValue.Object? Row { get; init; }
    public MmdSpec Spec { get; init; } = new();
    public required MmdInspection Inspection { get; init; }
}

/// <summary>
/// 上游 <c>mmd_adapter</c>：检查 MMD 原始资源包并生成独立播放适配包。
/// 只读取所选来源，不访问管理器库；输出的动作/镜头/音轨数据格式与上游一致。
/// </summary>
public static class MmdAdapter
{
    public const string ProductPatch = "9ba626afa44a3aa3.patch_0";
    private const string AdaptedCurrent = "角色库独立MMD适配包，由角色库提供动作/音乐/镜头；安装时停用同曲原表情替换包。";
    private const string ModulePattern = "(mods/[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+){0,8})";
    private static readonly byte[] MarkBytes = Encoding.ASCII.GetBytes(MmdBundle.Mark);
    private static readonly byte[] AddonHeader = Encoding.ASCII.GetBytes("-- HD2-Addon: ");

    private sealed record Candidate(
        string Module,
        Dictionary<string, (ulong Type, ulong Id)> Links,
        HashSet<string> Requested);

    public static (MmdPlan Plan, MmdInspection Inspection) Inspect(string sourcePath, string? sourceOption,
        MmdSourceMetadata? metadata, string? idlePlayback, Action<string>? progress, CancellationToken token)
    {
        if (idlePlayback is not null) MmdBundle.ValidateIdle(idlePlayback);
        progress?.Invoke("读取所选 MMD 模组");
        token.ThrowIfCancellationRequested();
        var requestedPath = Path.GetFullPath(sourcePath);
        var modules = MmdSourceReader.FindModules(requestedPath, token);
        string? moduleKey = null;
        if (modules.Count > 0)
        {
            MmdSourceModule? module = null;
            if (!string.IsNullOrEmpty(sourceOption))
            {
                module = modules.FirstOrDefault(item => item.Key == sourceOption || item.Name == sourceOption);
                if (module is not null)
                {
                    sourceOption = null;
                }
                else
                {
                    var parts = sourceOption.Split("::", 2);
                    module = modules.FirstOrDefault(item => item.Key == parts[0]);
                    if (module is null) throw new InvalidDataException("曲包不存在：" + sourceOption);
                    sourceOption = parts.Length > 1 ? parts[1] : null;
                }
            }
            else if (modules.Count == 1)
            {
                module = modules[0];
            }
            if (module is null)
            {
                var listing = modules.Select(item => new MmdSourceOption(item.Key, item.Name, [])).ToList();
                var pending = new MmdInspection("mmd", Path.GetFileName(requestedPath), requestedPath, listing,
                    null, idlePlayback ?? "loop", "needs_selection", [],
                    ["来源包含多个曲包，请选择要检查的一首 MMD 动作。"], [], 0, [], false);
                return (new MmdPlan { Source = null, Inspection = pending }, pending);
            }
            moduleKey = module.Key;
            sourcePath = module.Path;
        }

        var source = MmdSourceReader.Read(sourcePath, metadata, token);
        var choices = MmdSourceReader.ReadOptions(source.Meta, source.Patches);
        MmdSourceOption? match = null;
        if (!string.IsNullOrEmpty(sourceOption))
        {
            var matches = choices.Where(choice => choice.Key == sourceOption || choice.Name == sourceOption).ToList();
            if (matches.Count != 1) throw new InvalidDataException("动作选项不唯一或不存在：" + sourceOption);
            match = matches[0];
        }
        else if (choices.Count == 1)
        {
            match = choices[0];
        }

        var warnings = new List<string>();
        if (match is null)
        {
            warnings.Add("来源包含多个加载选项，请选择要转换的一项后重新检查。");
            var listing = moduleKey is null
                ? choices
                : choices.Select(choice =>
                    new MmdSourceOption(moduleKey + "::" + choice.Key, choice.Name, choice.Patches)).ToList();
            var pending = new MmdInspection("mmd", MmdSourceReader.Title(source.Meta), source.SourcePath,
                listing, null, idlePlayback ?? "loop", "needs_selection", [], warnings, [], 0, [], false);
            return (new MmdPlan { Source = source, Inspection = pending }, pending);
        }

        var (entries, overrides) = ReadResources(source, match.Patches, token);
        progress?.Invoke("核对动作、音乐与镜头数据");
        IasValue.Object row;
        MmdSpec spec;
        if (entries.Any(entry => entry.Key.Type == IasResourceTypes.Lua && HasMmdMark(entry.Value.Toc)))
        {
            var (registration, blobs) = MmdBundle.Unpack(entries, manual: false);
            row = MmdJson.With(registration.Row, "title", IasValue.Of(MmdSourceReader.Title(source.Meta)));
            spec = new MmdSpec { Blobs = blobs };
        }
        else
        {
            (row, spec) = ReadLegacy(entries, source.Meta);
        }

        var selectedPlayback = idlePlayback ?? MmdJson.Text(row, "idle_playback") ?? "loop";
        selectedPlayback = MmdBundle.ValidateIdle(selectedPlayback);
        row = MmdJson.With(row, "idle_playback", IasValue.Of(selectedPlayback));
        var id = MmdJson.Text(row, "id")!;
        var title = MmdJson.Text(row, "title") ?? string.Empty;
        var fps = (int)(MmdJson.Integer(row, "fps") ?? 0);
        var duration = MmdJson.Number(row, "duration") ?? 0;
        var frames = (int)Math.Round(duration * fps, MidpointRounding.ToEven) + 1;
        var hasPreview = false;
        if (MmdJson.Text(source.Meta, "IconPath") is { } icon)
        {
            MmdBundle.PreviewInfo(id, source.Members[icon]);
            hasPreview = true;
        }
        var tracks = new List<MmdInspectionTrack>
        {
            new(id, title, fps, duration, selectedPlayback, frames,
                spec.Blobs?.Any(blob => blob.Kind == "wav") == true || spec.Mdl3Audio is not null
                || spec.Requested.Contains("audio_runtime"),
                spec.Blobs?.Any(blob => blob.Kind == "camera") == true || spec.Camera is not null,
                spec.Clips, "角色库独立播放")
        };
        if (overrides.Count > 0)
            warnings.Add("所选加载项存在覆盖资源，按来源补丁顺序保留最后生效载荷。");
        var referenced = choices.SelectMany(choice => choice.Patches).ToHashSet(StringComparer.Ordinal);
        var inspection = new MmdInspection("mmd", MmdSourceReader.Title(source.Meta), source.SourcePath,
            choices, match.Key, selectedPlayback, "ready", tracks, warnings, overrides, 0,
            source.Patches.Where(patch => !referenced.Contains(patch)).ToList(), hasPreview);
        return (new MmdPlan
        {
            Source = source, Entries = entries, Row = row, Spec = spec, Inspection = inspection
        }, inspection);
    }

    public static MmdConvertResult Convert(MmdPlan plan, string outputDirectory,
        Action<string>? progress, CancellationToken token)
    {
        if (plan.Inspection.Status != "ready" || plan.Source is null || plan.Entries.Count == 0 || plan.Row is null)
            throw new InvalidDataException("请先选择完整动作选项并完成检查。");
        token.ThrowIfCancellationRequested();
        var source = plan.Source;
        var output = Path.GetFullPath(outputDirectory);
        if (output == source.SourcePath
            || (Directory.Exists(source.SourcePath)
                && output.StartsWith(source.SourcePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("输出目录必须与原始来源目录分开。");

        var blobs = Decode(plan, progress, token);
        var icon = MmdJson.Text(source.Meta, "IconPath");
        var preview = icon is null ? null : source.Members.GetValueOrDefault(icon);
        var packed = MmdBundle.Pack(plan.Row, blobs, preview);
        var row = packed.Row;
        var id = MmdJson.Text(row, "id")!;
        var title = MmdJson.Text(row, "title") ?? string.Empty;
        var idle = MmdJson.Text(row, "idle_playback") ?? "loop";
        var fps = (int)(MmdJson.Integer(row, "fps") ?? 0);
        var duration = MmdJson.Number(row, "duration") ?? 0;

        progress?.Invoke("打包并回读独立 MMD 成品");
        var buffers = IasArchive.Write(packed.Entries);
        IasArchive.VerifyReadback(buffers.Toc, buffers.Gpu, buffers.Stream, packed.Entries);
        var reread = MmdBundle.Unpack(IasArchive.ReadEntries(buffers.Toc, buffers.Gpu, buffers.Stream));
        if (MmdBundle.RowJson(reread.Registration.Row) != MmdBundle.RowJson(row)
            || !SameBlobs(reread.Blobs, blobs))
            throw new InvalidDataException("独立 MMD 资源回读不一致。");
        var frames = MmdBundle.Check(reread.Registration, reread.Blobs);

        var meta = MmdJson.With(source.Meta, "Name", IasValue.Of(title));
        meta = MmdJson.With(meta, "Guid",
            IasValue.Of(MmdIdentity.Uuid5("HD2CharacterLibrary/MMD/" + id + "/adapted-mmd")));
        meta = MmdJson.With(meta, "Description",
            IasValue.Of(AdaptedDescription(MmdJson.Text(meta, "Description"))));
        meta = MmdJson.Without(meta, "Options");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [ProductPatch] = buffers.Toc,
            [ProductPatch + ".gpu_resources"] = buffers.Gpu,
            [ProductPatch + ".stream"] = buffers.Stream,
            ["manifest.json"] = Encoding.UTF8.GetBytes(IasLua.WriteJson(meta, ensureAscii: false, sortKeys: false, indent: 2))
        };
        if (icon is not null) files[icon] = source.Members[icon];
        Directory.CreateDirectory(output);
        foreach (var (name, raw) in files)
        {
            token.ThrowIfCancellationRequested();
            File.WriteAllBytes(Path.Combine(output, name.Replace('/', Path.DirectorySeparatorChar)), raw);
        }

        progress?.Invoke("MMD 适配完成");
        return new MmdConvertResult(output, id, title, MmdBundle.Delivery,
            fps, idle, frames, duration, plan.Spec.Clips, packed.Entries.Count);
    }

    private static List<MmdBlob> Decode(MmdPlan plan, Action<string>? progress, CancellationToken token)
    {
        if (plan.Spec.Blobs is { } existing) return existing.ToList();
        var spec = plan.Spec;
        var blobs = new List<MmdBlob>();
        if (spec.Camera is not null) blobs.Add(new MmdBlob("camera", spec.Camera));
        if (spec.Mdl3Audio is { } mdl3Audio)
        {
            progress?.Invoke("解码并重采样 MDL3 原始音乐");
            blobs.Add(new MmdBlob("wav", MmdAudio.To48kWav(mdl3Audio)));
        }
        else if (spec.Requested.Contains("audio_runtime"))
        {
            progress?.Invoke("提取原始音乐");
            var index = MmdBundle.UnwrapLua(plan.Entries[spec.AudioIndex!.Value].Toc, 8192);
            if (!index.StartsWith(Encoding.ASCII.GetBytes("local name='" + spec.Module + "/audio_blob'")))
                throw new InvalidDataException("音乐索引指向另一份动作模组。");
            var encoded = MmdBundle.UnwrapLua(plan.Entries[spec.AudioBlob!.Value].Toc, 115343360);
            blobs.Add(new MmdBlob("wav", ParseAudio(Encoding.ASCII.GetString(index), Encoding.ASCII.GetString(encoded))));
        }
        token.ThrowIfCancellationRequested();
        progress?.Invoke("转换独立骨骼动作");
        var raw = plan.Entries.First(entry => entry.Key.Type == IasResourceTypes.Animation).Value.Toc;
        blobs.Add(new MmdBlob("pose", MmdMotion.Decode(raw, spec.Fps)));
        return blobs;
    }

    private static (IasValue.Object Row, MmdSpec Spec) ReadLegacy(
        IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> entries, IasValue.Object meta)
    {
        var clips = entries.Where(entry => entry.Key.Type == IasResourceTypes.Animation)
            .OrderBy(entry => entry.Key.Id).ToList();
        if (clips.Count == 0) throw new InvalidDataException("这不是含动作本体的完整 MMD 模组。");
        var first = clips[0].Value;
        if (clips.Skip(1).Any(clip => !SameEntry(clip.Value, first)))
            throw new InvalidDataException("多个替换槽位包含不同动作，请选择单独的动作选项。");
        var raw = first.Toc;
        if (raw.Length < 60 || raw.Length > 33_554_432)
            throw new InvalidDataException("来源动作头截断或载荷过大。");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        var bones = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
        var duration = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(8));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
        var events = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16));
        var extra = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(20));
        if (version != 0 || bones != MmdMotion.BoneCount || size != raw.Length || events != 0 || extra != 0
            || !(duration > 0) || duration > 900 || first.Gpu.Length > 0 || first.Stream.Length > 0)
            throw new InvalidDataException("当前只支持已核对的无事件 90 骨 MMD 动作载荷。");

        var candidates = new List<Candidate>();
        foreach (var (key, entry) in entries)
        {
            if (key.Type != IasResourceTypes.Lua || entry.Toc.Length < 8 + AddonHeader.Length
                || !entry.Toc.AsSpan(8, AddonHeader.Length).SequenceEqual(AddonHeader))
                continue;
            var code = Encoding.ASCII.GetString(MmdBundle.UnwrapLua(entry.Toc, 131_072));
            var firstLine = SplitLines(code).FirstOrDefault()?.TrimEnd('\r') ?? string.Empty;
            var tag = Regex.Match(firstLine, "\\A-- HD2-Addon: " + ModulePattern + "\\z");
            if (!tag.Success || IasHash.Murmur64(tag.Groups[1].Value) != key.Id) continue;
            var module = tag.Groups[1].Value;
            var declared = Regex.Match(code,
                "HD2DanceBus\\.register\\(\\s*HD2DanceKey\\s*,\\s*\\{([\\d\",\\s]+)\\}\\s*,\\s*\"([a-z0-9_]+)\"\\)");
            if (declared.Success)
            {
                var declaredIds = Regex.Matches(declared.Groups[1].Value, "\"([1-9]\\d{0,19})\"")
                    .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                if (!declaredIds.SetEquals(clips.Select(clip => clip.Key.Id.ToString(CultureInfo.InvariantCulture))))
                    throw new InvalidDataException("作者登记的动作槽位与当前选项载荷不一致。");
            }
            var requested = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kind in new[] { "audio_runtime", "camera_runtime" })
            {
                if (Regex.IsMatch(code, "checked_require\\(\\s*['\"]" + Regex.Escape(module + "/" + kind) + "['\"]\\s*\\)"))
                    requested.Add(kind);
            }
            if (!declared.Success && !(requested.Contains("audio_runtime") && requested.Contains("camera_runtime")))
                continue;
            var links = new Dictionary<string, (ulong, ulong)>(StringComparer.Ordinal);
            foreach (var part in new[] { "camera_runtime", "audio_runtime", "audio_index", "audio_blob" })
                links[part] = (IasResourceTypes.Lua, IasHash.Murmur64(module + "/" + part));
            var required = new HashSet<string>(requested, StringComparer.Ordinal);
            if (requested.Contains("audio_runtime")) { required.Add("audio_index"); required.Add("audio_blob"); }
            if (required.Any(part => !entries.ContainsKey(links[part])))
                throw new InvalidDataException("作者声明的音乐或镜头资源不完整。");
            candidates.Add(new Candidate(module, links, requested));
        }
        if (candidates.Count == 0 && MmdMdl3Reader.TryRead(entries) is { } mdl3)
            return ReadMdl3(mdl3, entries, meta, duration);
        if (candidates.Count != 1)
            throw new InvalidDataException("无法唯一确认此动作的音乐/镜头入口，不执行或猜测作者脚本。");

        var spec = candidates[0];
        var fps = 30;
        var stage = new List<double> { 0, 0, 0 };
        byte[]? camera = null;
        if (spec.Requested.Contains("camera_runtime"))
        {
            var code = MmdBundle.UnwrapLua(entries[spec.Links["camera_runtime"]].Toc, 8_388_608);
            (fps, stage, camera) = ParseCamera(Encoding.ASCII.GetString(code));
        }
        var identity = "mmd_" + IasHash.Sha256Hex("HD2ManualMMD/" + MmdJson.Text(meta, "Guid") + "/" + spec.Module)[..24];
        var row = (IasValue.Object)IasValue.Obj(
            ("protocol", IasValue.Of(1L)),
            ("id", IasValue.Of(identity)),
            ("title", IasValue.Of(MmdSourceReader.Title(meta))),
            ("fps", IasValue.Of((long)fps)),
            ("duration", IasValue.Of((double)duration)),
            ("stage", new IasValue.Array(stage.Select(IasValue.Of).ToList())),
            ("origin", IasValue.Obj(
                ("guid", IasValue.Of(MmdJson.Text(meta, "Guid")!)),
                ("module", IasValue.Of(spec.Module)))));
        var clipsText = clips.Select(clip => clip.Key.Id.ToString("x16")).ToList();
        return (row, new MmdSpec
        {
            Clips = clipsText,
            Module = spec.Module,
            Requested = spec.Requested,
            Fps = fps,
            Duration = (double)duration,
            Stage = stage,
            Camera = camera,
            AudioIndex = spec.Links.GetValueOrDefault("audio_index"),
            AudioBlob = spec.Links.GetValueOrDefault("audio_blob")
        });
    }

    /// <summary>
    /// MDL3 曲包：作者登记表声明动作资源、镜头与音乐脚本；
    /// 动作仍是 90 骨标准载荷，音乐为 32 kHz MS ADPCM，转成角色库要求的 48 kHz PCM。
    /// </summary>
    private static (IasValue.Object Row, MmdSpec Spec) ReadMdl3(
        MmdMdl3Package package, IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> entries,
        IasValue.Object meta, float duration)
    {
        if (Math.Abs(package.Duration - duration) > 0.05)
            throw new InvalidDataException("MDL3 登记时长与动作载荷不一致。");
        var ids = entries.Where(entry => entry.Key.Type == IasResourceTypes.Animation)
            .Select(entry => entry.Key.Id.ToString("x16")).ToHashSet(StringComparer.Ordinal);
        if (!ids.SetEquals(package.Resources))
            throw new InvalidDataException("MDL3 登记的动作资源与来源压缩包不一致。");
        var identity = "mmd_" + IasHash.Sha256Hex(
            "HD2ManualMMD/" + MmdJson.Text(meta, "Guid") + "/" + package.Path)[..24];
        var row = (IasValue.Object)IasValue.Obj(
            ("protocol", IasValue.Of(1L)),
            ("id", IasValue.Of(identity)),
            ("title", IasValue.Of(package.Label)),
            ("fps", IasValue.Of((long)package.Fps)),
            ("duration", IasValue.Of((double)duration)),
            ("stage", new IasValue.Array(package.Stage.Select(IasValue.Of).ToList())),
            ("origin", IasValue.Obj(
                ("guid", IasValue.Of(MmdJson.Text(meta, "Guid")!)),
                ("module", IasValue.Of(package.Path)))));
        return (row, new MmdSpec
        {
            Clips = package.Resources.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            Module = package.Path,
            Fps = package.Fps,
            Duration = (double)duration,
            Stage = package.Stage,
            Camera = package.Camera,
            Mdl3Audio = package.Audio,
        });
    }

    private static (int Fps, List<double> Stage, byte[] Camera) ParseCamera(string code)
    {
        var fpsCount = Regex.Match(code, "local FPS, COUNT = (\\d+), (\\d+)");
        var cutsMatch = Regex.Match(code, "local CUTS = \\{([^}]*)\\}");
        var bakedMatch = Regex.Match(code, "local BAKED = \\{([^}]+)\\}");
        if (!fpsCount.Success || !cutsMatch.Success || !bakedMatch.Success)
            throw new InvalidDataException("来源镜头烘焙表不完整或帧率无效。");
        var fps = int.Parse(fpsCount.Groups[1].Value, CultureInfo.InvariantCulture);
        var count = int.Parse(fpsCount.Groups[2].Value, CultureInfo.InvariantCulture);
        var cuts = Regex.Matches(cutsMatch.Groups[1].Value, "\\[(\\d+)\\]\\s*=\\s*true")
            .Select(match => match.Groups[1].Value).ToList();
        var lines = new List<string>();
        foreach (var line in SplitLines(bakedMatch.Groups[1].Value))
        {
            var stripped = line.Trim();
            if (stripped.Length == 0) continue;
            lines.Add(stripped.TrimEnd(','));
        }
        if (lines.Count != count || fps < 1 || fps > 120)
            throw new InvalidDataException("来源镜头烘焙表不完整或帧率无效。");
        var stage = new List<double> { 0, 0, 0 };
        var stageMatch = Regex.Match(code, "stage_offset\\s*=\\s*config\\.stage_offset\\s+or\\s*\\{([^}]+)\\}");
        if (stageMatch.Success)
        {
            stage = stageMatch.Groups[1].Value.Split(',')
                .Select(value => double.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToList();
        }
        var camera = Encoding.ASCII.GetBytes(string.Join(' ', cuts) + "\n" + string.Join('\n', lines) + "\n");
        return (fps, stage, camera);
    }

    internal static (int Fps, byte[] Camera) ParseCameraBlob(string code)
    {
        var (fps, _, camera) = ParseCamera(code);
        return (fps, camera);
    }

    private static byte[] ParseAudio(string index, string encoded)
    {
        var chunks = Regex.Matches(encoded, "\"([A-Za-z0-9+/=]+)\"")
            .Select(match => match.Groups[1].Value).ToList();
        using var buffer = new MemoryStream();
        foreach (var chunk in chunks)
        {
            try { buffer.Write(System.Convert.FromBase64String(chunk)); }
            catch (FormatException error) { throw new InvalidDataException("来源音轨数据与作者索引不一致。", error); }
        }
        var raw = buffer.ToArray();
        var chunkCount = Regex.Match(index, "chunk_count=(\\d+)");
        var size = Regex.Match(index, "size=(\\d+)");
        var sha = Regex.Match(index, "sha256='([a-f0-9]{64})'");
        if (!chunkCount.Success || !size.Success || !sha.Success
            || int.Parse(chunkCount.Groups[1].Value, CultureInfo.InvariantCulture) != chunks.Count
            || int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) != raw.Length
            || !string.Equals(sha.Groups[1].Value, IasHash.Sha256Hex(raw), StringComparison.Ordinal))
            throw new InvalidDataException("来源音轨数据与作者索引不一致。");
        return raw;
    }

    private static (Dictionary<(ulong Type, ulong Id), IasResource> Entries, List<MmdResourceOverride> Overrides)
        ReadResources(MmdSourceDocument source, IReadOnlyList<string> patches, CancellationToken token)
    {
        var result = new Dictionary<(ulong, ulong), IasResource>();
        var overrides = new List<MmdResourceOverride>();
        foreach (var name in patches)
        {
            token.ThrowIfCancellationRequested();
            var missing = new[] { string.Empty, ".gpu_resources", ".stream" }
                .Where(suffix => !source.Members.ContainsKey(name + suffix))
                .Select(suffix => name + suffix).ToList();
            if (missing.Count > 0)
                throw new InvalidDataException("来源动作三件套不完整：" + string.Join(", ", missing));
            var entries = IasArchive.ReadEntries(source.Members[name], source.Members[name + ".gpu_resources"],
                source.Members[name + ".stream"]);
            foreach (var (key, entry) in entries)
            {
                if (result.TryGetValue(key, out var earlier) && !SameEntry(earlier, entry))
                    overrides.Add(new MmdResourceOverride(key.Item1.ToString("x16"), key.Item2.ToString("x16"), name));
                result[key] = entry;
            }
        }
        return (result, overrides);
    }

    private static bool HasMmdMark(byte[] toc)
        => toc.Length >= 8 + MarkBytes.Length && toc.AsSpan(8, MarkBytes.Length).SequenceEqual(MarkBytes);

    private static bool SameEntry(IasResource left, IasResource right)
        => left.Toc.AsSpan().SequenceEqual(right.Toc)
           && left.Gpu.AsSpan().SequenceEqual(right.Gpu)
           && left.Stream.AsSpan().SequenceEqual(right.Stream);

    private static bool SameBlobs(IReadOnlyList<MmdBlob> left, IReadOnlyList<MmdBlob> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Kind == pair.Second.Kind && pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data));

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string AdaptedDescription(string? original)
    {
        if (string.IsNullOrEmpty(original)) return AdaptedCurrent;
        return original.StartsWith(AdaptedCurrent, StringComparison.Ordinal)
            ? original
            : AdaptedCurrent + "\n\n原作者说明（适配前）：\n" + original;
    }
}
