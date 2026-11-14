// HellForge CLI 测试替身：按命令输出安装根目录里预置的 JSON，记录参数，支持模拟失败。
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

var command = args.Length > 0 ? args[0] : string.Empty;

// --help：GameDetectionService 靠输出里是否有 "play " 行判断 CLI 是否支持会话命令。
// 该探测不带 --install，必须在 root 校验之前应答。
if (command == "--help" || args.Contains("--help"))
{
    Console.WriteLine("usage: HellForge.Cli <command> [options]");
    Console.WriteLine("status [--install <folder>]");
    Console.WriteLine("profiles [--install <folder>] [--create <name> --template <template>]");
    Console.WriteLine("play [--install <folder>] [--wait yes]");
    Console.WriteLine("stop [--install <folder>] [--wait yes]");
    Console.WriteLine("settings [--install <folder>] [--profile <name>]");
    return 0;
}

var root = string.Empty;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--install") root = args[i + 1];
if (root.Length == 0)
{
    Console.Error.WriteLine("HellForge: missing --install.");
    return 1;
}

File.AppendAllText(Path.Combine(root, "cli-args.txt"), string.Join(" ", args) + Environment.NewLine);

var fail = Path.Combine(root, "cli-fail.txt");
if (File.Exists(fail))
{
    Console.Error.WriteLine(File.ReadAllText(fail).Trim());
    return 1;
}

// play：逐行输出预置的 JSONL 事件流（started/status/.../ended），模拟 --wait 跟随。
// 存在 cli-play-hold.txt 时，输出完事件后等待 cli-stop-signal.txt（由 stop 写入）再结束，
// 用于验证“运行中 → 停止”的交互；否则直接按 cli-play-exit.txt 的退出码退出。
if (command == "play")
{
    var stream = Path.Combine(root, "cli-play.jsonl");
    if (!File.Exists(stream))
    {
        Console.Error.WriteLine("HellForge: no canned play stream.");
        return 1;
    }
    foreach (var line in File.ReadAllLines(stream))
    {
        if (line.Length == 0) continue;
        Console.WriteLine(line);
        Console.Out.Flush();
        Thread.Sleep(20);
    }
    if (File.Exists(Path.Combine(root, "cli-play-hold.txt")))
    {
        var signal = Path.Combine(root, "cli-stop-signal.txt");
        for (var i = 0; i < 600 && !File.Exists(signal); i++) Thread.Sleep(50);
        Console.WriteLine("""{"event":"ended","status":"observed","result":"stub","logs":"stub"}""");
        Console.Out.Flush();
        return 0;
    }
    var exitFile = Path.Combine(root, "cli-play-exit.txt");
    return File.Exists(exitFile) && int.TryParse(File.ReadAllText(exitFile).Trim(), out var code) ? code : 0;
}

// stop：写入停止信号让 hold 中的 play 收尾，输出 stopping。
if (command == "stop")
{
    File.WriteAllText(Path.Combine(root, "cli-stop-signal.txt"), "stop");
    Console.WriteLine("""{"event":"stopping","run":"stub"}""");
    return 0;
}

// 模组命令：真实维护 mods\ 文件夹与 hellforge-mods.json，让 mods 列表反映增删/开关/排序/选项，
// 供同步编排与 UI 测试断言实际落位结果（而非静态应答）。
if (command is "mods" or "mod-add" or "mod-remove" or "mod-switch" or "mod-order" or "mod-pack")
    return StubMods.Handle(command, args, root);

// settings/logs：真实维护 launcher-settings.json 与 logs\sessions，按 CLI 契约输出，
// 供设置保存、日志列表与导出/崩溃报告测试断言实际行为。
if (command is "settings" or "logs")
    return StubSettingsAndLogs.Handle(command, args, root);

var response = Path.Combine(root, $"cli-{command}.json");
if (!File.Exists(response))
{
    Console.Error.WriteLine($"HellForge: no canned response for {command}.");
    return 1;
}
Console.Write(File.ReadAllText(response));
return 0;

// 模拟 HellForge 的 mods\ 管理：文件夹模组（hellforge-mod.json）与 cosmetic pack（manifest.json），
// hellforge-mods.json 记录 disabled/order/packs。仅实现同步所需的最小语义。
internal static partial class StubMods
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static int Handle(string command, string[] args, string root)
    {
        var modsDir = Path.Combine(root, "mods");
        Directory.CreateDirectory(modsDir);
        var settingsFile = Path.Combine(modsDir, "hellforge-mods.json");
        var settings = LoadSettings(settingsFile);
        try
        {
            object result = command switch
            {
                "mod-add" => Add(modsDir, Option(args, "--file")),
                "mod-remove" => Remove(modsDir, Option(args, "--id")),
                "mod-switch" => Switch(settings, Option(args, "--id"), Option(args, "--on") == "yes"),
                "mod-order" => Order(settings, Option(args, "--ids"), modsDir),
                "mod-pack" => Pack(settings, modsDir, Option(args, "--id"), Option(args, "--option"),
                    Optional(args, "--on"), Optional(args, "--variant")),
                _ => List(modsDir, settings)
            };
            SaveSettings(settingsFile, settings);
            Console.Write(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            Console.Error.WriteLine("HellForge: " + ex.Message);
            return 1;
        }
    }

    private static object Add(string modsDir, string file)
    {
        var source = Path.GetDirectoryName(Path.GetFullPath(file))
            ?? throw new InvalidDataException("mod-add needs a manifest file.");
        var (id, name, version, _, _) = ReadMod(source);
        string? replaced = null;
        foreach (var folder in ModFolders(modsDir))
            if (ReadMod(folder).Id == id)
            {
                replaced = folder;
                Directory.Delete(folder, true);
            }
        var target = Path.Combine(modsDir, Path.GetFileName(source));
        for (var n = 2; Directory.Exists(target); n++)
            target = Path.Combine(modsDir, $"{Path.GetFileName(source)}-{n}");
        CopyFolder(source, target);
        return new { id, name, version, installed = target, replaced };
    }

    private static object Remove(string modsDir, string id)
    {
        var folder = ModFolders(modsDir).FirstOrDefault(f => ReadMod(f).Id == id)
            ?? throw new ArgumentException($"No mod with the id {id}.");
        Directory.Delete(folder, true);
        return new { id, removed = folder };
    }

    private static object Switch(Settings settings, string id, bool on)
    {
        if (on) settings.Disabled.Remove(id); else settings.Disabled.Add(id);
        return new { id, enabled = on };
    }

    private static object Order(Settings settings, string ids, string modsDir)
    {
        var listed = ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var all = ModFolders(modsDir).Select(f => ReadMod(f).Id).ToList();
        foreach (var id in listed)
            if (!all.Contains(id)) throw new ArgumentException($"No mod with the id {id}.");
        settings.Order = listed.Concat(all.Where(id => !listed.Contains(id))).ToList();
        return new { order = settings.Order };
    }

    private static object Pack(Settings settings, string modsDir, string id, string option, string? on, string? variant)
    {
        var folder = ModFolders(modsDir).FirstOrDefault(f => ReadMod(f).Id == id)
            ?? throw new ArgumentException($"No mod with the id {id}.");
        var mod = ReadMod(folder);
        if (!mod.IsPack) throw new ArgumentException($"{id} is not a cosmetic pack with options.");
        if (!settings.Packs.TryGetValue(id, out var choice))
            choice = settings.Packs[id] = new PackChoice();
        if (on == "no") choice.Off.Add(option); else choice.Off.Remove(option);
        if (variant != null) choice.Sub[option] = variant;
        return new { id, options = PackOptions(mod, choice) };
    }

    private static object List(string modsDir, Settings settings)
    {
        var mods = ModFolders(modsDir).Select(folder =>
        {
            var mod = ReadMod(folder);
            settings.Packs.TryGetValue(mod.Id, out var choice);
            choice ??= new PackChoice();
            return new
            {
                id = mod.Id, name = mod.Name, version = mod.Version, description = "",
                enabled = !settings.Disabled.Contains(mod.Id),
                group = mod.IsPack ? "cosmetic" : "gameplay",
                tags = mod.IsPack ? new[] { "pack" } : new[] { "data" },
                folder, archive = (string?)null, removable = true,
                options = mod.IsPack ? PackOptions(mod, choice) : null
            };
        }).ToList();
        var index = settings.Order.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);
        var ordered = mods.OrderBy(m => index.ContainsKey(m.id) ? 0 : 1)
            .ThenBy(m => index.GetValueOrDefault(m.id)).ThenBy(m => m.id, StringComparer.Ordinal).ToList();
        return new { mods = ordered, problems = Array.Empty<string>(), modlist = (string?)null, settings = Path.Combine(modsDir, "hellforge-mods.json") };
    }

    private static object[] PackOptions(Mod mod, PackChoice choice) => mod.Options.Select(o => new
    {
        name = o.Name,
        on = !choice.Off.Contains(o.Name),
        variants = o.SubOptions.Count > 0 ? o.SubOptions.ToArray() : null,
        picked = choice.Sub.GetValueOrDefault(o.Name)
    }).ToArray<object>();

    private static IEnumerable<string> ModFolders(string modsDir) =>
        Directory.EnumerateDirectories(modsDir).Where(d => !Path.GetFileName(d).StartsWith('.'))
            .Where(d => File.Exists(Path.Combine(d, "hellforge-mod.json")) || File.Exists(Path.Combine(d, "manifest.json")))
            .OrderBy(d => d, StringComparer.Ordinal);

    private static Mod ReadMod(string folder)
    {
        var flat = Path.Combine(folder, "hellforge-mod.json");
        if (File.Exists(flat))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(flat));
            var root = doc.RootElement;
            return new Mod(root.GetProperty("id").GetString()!, root.GetProperty("name").GetString()!,
                root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "", false, new List<StubOption>());
        }
        var pack = Path.Combine(folder, "manifest.json");
        if (File.Exists(pack))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pack));
            var root = doc.RootElement;
            var name = root.TryGetProperty("Name", out var n) ? n.GetString() ?? Path.GetFileName(folder) : Path.GetFileName(folder);
            var id = root.TryGetProperty("Guid", out var g) && Guid.TryParse(g.GetString(), out var guid)
                ? "pack." + guid.ToString("D")
                : "pack." + Slug(name);
            var options = new List<StubOption>();
            if (root.TryGetProperty("Options", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var o in list.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object) continue;
                    var subs = new List<string>();
                    if (o.TryGetProperty("SubOptions", out var subList) && subList.ValueKind == JsonValueKind.Array)
                        foreach (var s in subList.EnumerateArray())
                            if (s.TryGetProperty("Name", out var sn)) subs.Add(sn.GetString()!);
                    options.Add(new StubOption(o.GetProperty("Name").GetString()!, subs));
                }
            return new Mod(id, name, "", true, options);
        }
        throw new InvalidDataException($"no manifest in {folder}.");
    }

    private static Settings LoadSettings(string file)
    {
        if (!File.Exists(file)) return new Settings();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json) ?? new Settings(); }
        catch (JsonException) { return new Settings(); }
    }

    // settings --strangers/--forge-credits 落在 mods\hellforge-mods.json；供 StubSettingsAndLogs 读写。
    public static (string? Strangers, int? ForgeCreditChance) ReadChoices(string root)
    {
        var settings = LoadSettings(Path.Combine(root, "mods", "hellforge-mods.json"));
        return (settings.Strangers, settings.ForgeCreditChance);
    }

    public static void SetStrangers(string root, string? choice) => UpdateChoices(root, s => s.Strangers = choice);

    public static void SetForgeCredits(string root, int? percent) => UpdateChoices(root, s => s.ForgeCreditChance = percent);

    private static void UpdateChoices(string root, Action<Settings> change)
    {
        var file = Path.Combine(root, "mods", "hellforge-mods.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var settings = LoadSettings(file);
        change(settings);
        SaveSettings(file, settings);
    }

    // 与官方格式一致（docs/external-launchers.md）：小写 camelCase 的 disabled/order/packs。
    private static readonly JsonSerializerOptions JsonOut = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static void SaveSettings(string file, Settings settings) =>
        File.WriteAllText(file, JsonSerializer.Serialize(settings, JsonOut));

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static string Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        throw new ArgumentException($"missing {name}.");
    }

    private static string? Optional(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static string Slug(string name) =>
        Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');

    private sealed record Mod(string Id, string Name, string Version, bool IsPack, List<StubOption> Options);
    private sealed record StubOption(string Name, List<string> SubOptions);

    private sealed class Settings
    {
        public HashSet<string> Disabled { get; set; } = new();
        public List<string> Order { get; set; } = new();
        public Dictionary<string, PackChoice> Packs { get; set; } = new();
        public string? Strangers { get; set; }
        public int? ForgeCreditChance { get; set; }
    }

    private sealed class PackChoice
    {
        public HashSet<string> Off { get; set; } = new();
        public Dictionary<string, string> Sub { get; set; } = new();
    }
}

// 模拟 HellForge 的 settings 与 logs：launcher-settings.json 持久化启动器选项，
// logs\sessions\<id>\ 每个目录是一次会话（run.json 可给 status/failure/gameExitCode 等），
// --export/--crash-report 真的生成 zip，行为与 CLI 契约一致（目标已存在则拒绝）。
internal static class StubSettingsAndLogs
{
    private static readonly JsonSerializerOptions JsonIn = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonOut = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int Handle(string command, string[] args, string root)
    {
        try
        {
            var result = command == "settings" ? Settings(args, root) : Logs(args, root);
            Console.Write(JsonSerializer.Serialize(result, JsonOut));
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            Console.Error.WriteLine("HellForge: " + ex.Message);
            return 1;
        }
    }

    private static object Settings(string[] args, string root)
    {
        var settings = LoadLauncher(root);
        string? strangers = null;
        int? credits = null;
        var strangersGiven = false;
        var creditsGiven = false;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var name = args[i];
            var value = args[++i];
            if (name == "--install") continue;
            switch (name)
            {
                case "--profile":
                    if (!ProfileExists(root, value)) throw new ArgumentException($"No profile named {value}.");
                    settings.Profile = value;
                    break;
                case "--war":
                    if (value is not ("live" or "frozen" or "open"))
                        throw new ArgumentException("--war takes live, frozen or open.");
                    settings.War = value;
                    break;
                case "--local-playfab": settings.LocalPlayFab = YesNo(name, value); break;
                case "--multiplayer": settings.Multiplayer = YesNo(name, value); break;
                case "--engine-log": settings.EngineLog = YesNo(name, value); break;
                case "--crash-dump": settings.CrashDump = YesNo(name, value); break;
                case "--sandbox": settings.Sandbox = YesNo(name, value); break;
                case "--steam-udp": settings.SteamUdp = YesNo(name, value); break;
                case "--live-changes": settings.LiveChanges = YesNo(name, value); break;
                case "--game-options": settings.GameOptions = value; break;
                case "--strangers":
                    strangers = value switch
                    {
                        "yes" => "on",
                        "no" => "off",
                        "default" => null,
                        _ => throw new ArgumentException("--strangers takes yes, no or default.")
                    };
                    strangersGiven = true;
                    break;
                case "--forge-credits":
                    credits = value is "default" or "off" ? null
                        : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent <= 100
                            ? percent
                            : throw new ArgumentException("--forge-credits takes a percentage from 0 to 100, or default.");
                    creditsGiven = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option {name}.");
            }
        }
        SaveLauncher(root, settings);
        if (strangersGiven) StubMods.SetStrangers(root, strangers);
        if (creditsGiven) StubMods.SetForgeCredits(root, credits);
        var (chosenStrangers, forgeCredits) = StubMods.ReadChoices(root);
        bool? chosen = chosenStrangers switch { "on" => true, "off" => false, _ => null };
        return new
        {
            profile = settings.Profile,
            war = settings.War,
            localPlayFab = settings.LocalPlayFab,
            multiplayer = settings.Multiplayer,
            engineLog = settings.EngineLog,
            crashDump = settings.CrashDump,
            gameOptions = settings.GameOptions ?? "",
            sandbox = settings.Sandbox,
            steamUdp = settings.SteamUdp,
            steamBridge = true,
            playWithStrangers = chosen ?? settings.Sandbox,
            playWithStrangersChosen = chosen,
            liveChanges = settings.LiveChanges,
            forgeCreditChance = forgeCredits ?? DefaultForgeCreditChance,
            file = Path.Combine(root, "launcher-settings.json")
        };
    }

    private const int DefaultForgeCreditChance = 30;

    private static object Logs(string[] args, string root)
    {
        var sessions = Path.Combine(root, "logs", "sessions");
        Directory.CreateDirectory(sessions);
        var runs = Directory.EnumerateDirectories(sessions)
            .Select(ReadRun)
            .OrderByDescending(r => r.Id, StringComparer.Ordinal)
            .ToList();
        var wanted = Optional(args, "--run");
        var export = Optional(args, "--export");
        var crash = Optional(args, "--crash-report");
        var dumpOption = Optional(args, "--dump");
        if (dumpOption != null && crash == null) throw new ArgumentException("--dump goes with --crash-report.");
        if (export != null && crash != null) throw new ArgumentException("--export and --crash-report are exclusive.");
        var picked = wanted == null
            ? null
            : runs.FirstOrDefault(r => r.Id == wanted
                || string.Equals(r.Folder, Path.GetFullPath(wanted), StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"No session {wanted} in {sessions}.");
        if (export != null)
        {
            var run = picked ?? runs.FirstOrDefault()
                ?? throw new InvalidDataException("No session logs yet: play a session first.");
            return Export(run, export);
        }
        if (crash != null)
        {
            var run = picked ?? runs.FirstOrDefault()
                ?? throw new InvalidDataException("No session logs yet: play a session first.");
            return CrashReport(run, crash, dumpOption == "yes");
        }
        var runnerLog = Path.Combine(root, "logs", "session-runner.log");
        return new
        {
            runs = runs.Select(Describe).ToList(),
            files = picked == null ? null : Files(picked.Folder),
            sessionRunnerLog = File.Exists(runnerLog) ? runnerLog : null,
            gameLogs = Path.Combine(root, "logs")
        };
    }

    private static object Describe(RunInfo run) => new
    {
        id = run.Id,
        folder = run.Folder,
        startedUtc = run.StartedUtc,
        status = run.Status,
        failure = run.Failure,
        gameExitCode = run.GameExitCode,
        completedUtc = run.CompletedUtc,
        crashed = run.Crashed
    };

    private static RunInfo ReadRun(string folder)
    {
        string? status = null, failure = null, exit = null;
        DateTime? started = null, completed = null;
        bool? crashed = null;
        var meta = Path.Combine(folder, "run.json");
        if (File.Exists(meta))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var v)) status = v.GetString();
            if (root.TryGetProperty("failure", out v)) failure = v.GetString();
            if (root.TryGetProperty("gameExitCode", out v))
                exit = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            if (root.TryGetProperty("startedUtc", out v) && v.TryGetDateTime(out var st)) started = st;
            if (root.TryGetProperty("completedUtc", out v) && v.TryGetDateTime(out var ct)) completed = ct;
            if (root.TryGetProperty("crashed", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                crashed = v.GetBoolean();
        }
        crashed ??= status == "crashed" || File.Exists(Path.Combine(folder, "crash.dmp"));
        return new RunInfo(Path.GetFileName(folder), folder, started, status ?? "observed", failure, exit,
            completed, crashed.Value);
    }

    private static List<object> Files(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .OrderBy(f => f, StringComparer.Ordinal)
        .Select(f => (object)new
        {
            path = Path.GetRelativePath(folder, f).Replace('\\', '/'),
            bytes = new FileInfo(f).Length
        })
        .ToList();

    private static object Export(RunInfo run, string target)
    {
        target = Path.GetFullPath(target);
        if (File.Exists(target)) throw new ArgumentException($"{target} exists already.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var files = Directory.GetFiles(run.Folder, "*", SearchOption.AllDirectories);
        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
            foreach (var file in files)
                zip.CreateEntryFromFile(file, Path.GetRelativePath(run.Folder, file).Replace('\\', '/'));
        return new { run = Describe(run), zip = target, files = files.Length, skipped = 0 };
    }

    private static object CrashReport(RunInfo run, string target, bool dump)
    {
        target = Path.GetFullPath(target);
        if (File.Exists(target)) throw new ArgumentException($"{target} exists already.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var dumpFile = Path.Combine(run.Folder, "crash.dmp");
        var count = 0;
        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("summary.txt").Open()))
            {
                writer.WriteLine("HellForge stub crash report");
                writer.WriteLine("run: " + run.Id);
                writer.WriteLine("status: " + run.Status);
                writer.WriteLine("game exit code: " + (run.GameExitCode ?? "none"));
            }
            count++;
            foreach (var file in Directory.GetFiles(run.Folder, "*", SearchOption.AllDirectories))
            {
                var entry = zip.CreateEntry("logs/" + Path.GetRelativePath(run.Folder, file).Replace('\\', '/'));
                using var input = File.OpenRead(file);
                using var output = entry.Open();
                input.CopyTo(output);
                count++;
            }
            if (dump && File.Exists(dumpFile))
            {
                zip.CreateEntryFromFile(dumpFile, "dump/crash-1.dmp");
                count++;
            }
        }
        return new
        {
            run = Describe(run),
            zip = target,
            files = count,
            skipped = 0,
            dump = dump && File.Exists(dumpFile),
            dumpAvailable = File.Exists(dumpFile),
            issuePage = "https://github.com/hellforge/hellforge/issues/new"
        };
    }

    private static LauncherSettings LoadLauncher(string root)
    {
        var file = Path.Combine(root, "launcher-settings.json");
        if (File.Exists(file))
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(file), JsonIn) ?? new LauncherSettings();
        var settings = new LauncherSettings { Profile = SelectedProfile(root) };
        SaveLauncher(root, settings);
        return settings;
    }

    private static void SaveLauncher(string root, LauncherSettings settings)
        => File.WriteAllText(Path.Combine(root, "launcher-settings.json"), JsonSerializer.Serialize(settings));

    private static string? SelectedProfile(string root)
    {
        var file = Path.Combine(root, "cli-profiles.json");
        if (!File.Exists(file)) return "default";
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("profiles", out var profiles)) return "default";
        foreach (var profile in profiles.EnumerateArray())
            if (profile.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True
                && profile.TryGetProperty("name", out var name))
                return name.GetString();
        return "default";
    }

    private static bool ProfileExists(string root, string name)
    {
        var file = Path.Combine(root, "cli-profiles.json");
        if (!File.Exists(file)) return true;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("profiles", out var profiles)) return true;
        return profiles.EnumerateArray().Any(p => p.TryGetProperty("name", out var n) && n.GetString() == name);
    }

    private static bool YesNo(string name, string value) => value switch
    {
        "yes" => true,
        "no" => false,
        _ => throw new ArgumentException($"{name} takes yes or no.")
    };

    private static string? Optional(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private sealed class LauncherSettings
    {
        public string? Profile { get; set; }
        public string War { get; set; } = "live";
        public bool LocalPlayFab { get; set; } = true;
        public bool Multiplayer { get; set; } = true;
        public bool EngineLog { get; set; } = true;
        public bool CrashDump { get; set; }
        public string? GameOptions { get; set; }
        public bool Sandbox { get; set; } = true;
        public bool SteamUdp { get; set; } = true;
        public bool LiveChanges { get; set; }
    }

    private sealed record RunInfo(string Id, string Folder, DateTime? StartedUtc, string? Status, string? Failure,
        string? GameExitCode, DateTime? CompletedUtc, bool Crashed);
}