using System.IO.Compression;
using System.Text;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Tests.Darktide;

internal sealed class DarktideTestEnvironment : IDisposable
{
    public DarktideTestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "DarktideMimntTests", Guid.NewGuid().ToString("N"));
        GameRoot = Path.Combine(Root, "game");
        AppData = Path.Combine(Root, "appdata");
        Cache = Path.Combine(Root, "cache");
        ModsStorage = Path.Combine(Root, "hd2mods");
        Directory.CreateDirectory(GameRoot);
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(ModsStorage);
        Storage = DarktideStoragePaths.Create(new StorageLocations(AppData, ModsStorage, Cache));
        Storage.EnsureDirectories();
    }

    public string Root { get; }
    public string GameRoot { get; }
    public string AppData { get; }
    public string Cache { get; }
    public string ModsStorage { get; }
    public string DatabasePath => Path.Combine(AppData, "darktidemimnt.db");
    public DarktideStoragePaths Storage { get; }

    public void CreateGame(bool withDml = true)
    {
        Directory.CreateDirectory(Path.Combine(GameRoot, "binaries"));
        Directory.CreateDirectory(Path.Combine(GameRoot, "bundle"));
        File.WriteAllText(Path.Combine(GameRoot, "binaries", "Darktide.exe"), "");
        if (!withDml) return;
        Directory.CreateDirectory(Path.Combine(GameRoot, "tools"));
        Directory.CreateDirectory(Path.Combine(GameRoot, "mods", "base"));
        Directory.CreateDirectory(Path.Combine(GameRoot, "mods", "dmf"));
        File.WriteAllText(Path.Combine(GameRoot, "binaries", "mod_loader"), "return {}");
        File.WriteAllText(Path.Combine(GameRoot, "mods", "base", "mod_manager.lua"), "return {}");
        File.WriteAllText(Path.Combine(GameRoot, "mods", "dmf", "dmf.mod"), "return {}");
        File.WriteAllText(Path.Combine(GameRoot, "tools", "dtkit-patch.exe"), "");
        File.WriteAllText(Path.Combine(GameRoot, DarktidePaths.ToggleScriptRelativePath), "@echo off\r\npause\r\n");
        WriteLoadOrder("-- ################################################################\r\n-- Enter user mod names below, separated by line.\r\n-- ################################################################\r\n\r\n");
    }

    public string AddMod(string name, string version = "1.0.0", string author = "Tester",
        IReadOnlyList<string>? required = null, bool register = true)
    {
        var folder = Path.Combine(GameRoot, "mods", name);
        Directory.CreateDirectory(Path.Combine(folder, "scripts", "mods", name));
        File.WriteAllText(Path.Combine(folder, name + ".mod"), "return { run = function() end }");
        File.WriteAllText(Path.Combine(folder, "scripts", "mods", name, name + ".lua"), "return {}");
        var dependencies = required is { Count: > 0 }
            ? ",\"dependencies\":{\"required\":[" + string.Join(',', required.Select(value => $"\"{value}\"")) + "]}"
            : "";
        File.WriteAllText(Path.Combine(folder, "info.json"),
            $"{{\"name\":\"{name} Display\",\"version\":\"{version}\",\"author\":\"{author}\",\"description\":\"{name} description\"{dependencies}}}");
        if (register) AppendLoadOrder(name);
        return folder;
    }

    public void AppendLoadOrder(string text)
    {
        var path = DarktidePaths.LoadOrderFile(GameRoot);
        var current = File.Exists(path) ? File.ReadAllText(path) : "";
        File.WriteAllText(path, current + text + "\r\n");
    }

    public void WriteLoadOrder(string content, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.Combine(GameRoot, "mods"));
        File.WriteAllText(DarktidePaths.LoadOrderFile(GameRoot), content, encoding ?? new UTF8Encoding(false));
    }

    public string CreateModPackage(string fileName, string internalName, string version = "1.0.0",
        string? wrapper = null, IReadOnlyList<string>? required = null, IReadOnlyList<(string Path, string Content)>? extra = null)
    {
        var path = Path.Combine(Root, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var prefix = string.IsNullOrEmpty(wrapper) ? "" : wrapper.Trim('/') + "/";
        AddEntry(archive, $"{prefix}{internalName}/{internalName}.mod", "return { run = function() end }");
        AddEntry(archive, $"{prefix}{internalName}/scripts/mods/{internalName}/{internalName}.lua", "return {}");
        var dependencies = required is { Count: > 0 }
            ? ",\"dependencies\":{\"required\":[" + string.Join(',', required.Select(value => $"\"{value}\"")) + "]}"
            : "";
        AddEntry(archive, $"{prefix}{internalName}/info.json",
            $"{{\"name\":\"{internalName} Display\",\"version\":\"{version}\",\"author\":\"Tester\",\"description\":\"{internalName}\"{dependencies}}}");
        foreach (var (entryPath, content) in extra ?? Array.Empty<(string, string)>())
            AddEntry(archive, $"{prefix}{internalName}/{entryPath}", content);
        return path;
    }

    public string CreateDmlPackage(string fileName, bool full, IReadOnlyList<(string Path, string Content)>? extra = null)
    {
        var path = Path.Combine(Root, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddEntry(archive, "binaries/mod_loader", "return { loader = 2 }");
        AddEntry(archive, "mods/base/mod_manager.lua", "return { manager = 2 }");
        AddEntry(archive, "toggle_darktide_mods.bat", "@echo off\r\npause\r\n");
        AddEntry(archive, "tools/dtkit-patch.exe", "patcher");
        AddEntry(archive, "bundle/9ba626afa44a3aa3.patch_999", "patch");
        AddEntry(archive, "README.md", "DML test package");
        if (full) AddEntry(archive, "mods/mod_load_order.txt", "-- packaged order\r\n");
        foreach (var (entryPath, content) in extra ?? Array.Empty<(string, string)>())
            AddEntry(archive, entryPath, content);
        return path;
    }

    /// <summary>构造“多模组 + 可选的原生文件/脚本”的压缩包，模拟 PlayerModelHub 这类特殊发布。</summary>
    public string CreateMultiModPackage(string fileName, (string Name, string[] Required)[] mods,
        bool withNativeExtras = false)
    {
        var path = Path.Combine(Root, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, required) in mods)
        {
            AddEntry(archive, $"{name}/{name}.mod", "return { run = function() end }");
            AddEntry(archive, $"{name}/scripts/mods/{name}/{name}.lua", "return {}");
            var dependencies = required.Length > 0
                ? ",\"dependencies\":{\"required\":[" + string.Join(',', required.Select(value => $"\"{value}\"")) + "],\"self_after\":["
                  + string.Join(',', required.Select(value => $"\"{value}\"")) + "]}"
                : "";
            AddEntry(archive, $"{name}/info.json",
                $"{{\"name\":\"{name}\",\"version\":\"1.0.0\",\"author\":\"Tester\",\"description\":\"{name}\"{dependencies}}}");
        }
        if (withNativeExtras)
        {
            AddEntry(archive, "binaries/plugins/custom_assets_plugin.dll", "binary");
            AddEntry(archive, "tools/custom-assets-patcher.exe", "binary");
            AddEntry(archive, "install_models.bat", "@echo off\r\n");
            AddEntry(archive, "README.md", "readme");
        }
        return path;
    }

    public static void AddEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
        try
        {
            var parent = Path.GetDirectoryName(Root);
            if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
