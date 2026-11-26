using System.Text.Json;

namespace CpuSchedulingProbe;

internal sealed class ProbeState
{
    public int Pid { get; set; }
    public long StartTimeUtcTicks { get; set; }
    public string Mode { get; set; } = string.Empty;
    public uint[] OriginalDefaultSets { get; set; } = [];
    public uint[] AppliedSets { get; set; } = [];
    public ulong? OriginalAffinity { get; set; }
    public ulong? AppliedAffinity { get; set; }
}

internal static class ProbeStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string DirectoryPath => Path.Combine(Path.GetTempPath(), "hd2-cpu-probe");

    public static string PathFor(int pid) => Path.Combine(DirectoryPath, $"state-{pid}.json");

    public static void Save(ProbeState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(PathFor(state.Pid), JsonSerializer.Serialize(state, Options));
    }

    public static ProbeState? Load(int pid)
    {
        var path = PathFor(pid);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ProbeState>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Console.Error.WriteLine($"Could not read {path}: {ex.Message}");
            return null;
        }
    }

    public static void Delete(int pid)
    {
        var path = PathFor(pid);
        if (File.Exists(path))
            File.Delete(path);
    }
}