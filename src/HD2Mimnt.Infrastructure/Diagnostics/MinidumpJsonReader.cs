using System.Text.Json;
using HD2Mimnt.Core.Diagnostics;

namespace HD2Mimnt.Infrastructure.Diagnostics;

/// <summary>rust-minidump 的所有 JSON 字段均可能缺失或为 null。</summary>
public static class MinidumpJsonReader
{
    public static CrashDumpAnalysis Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid minidump-stackwalk JSON object.");
        var status = Text(root, "status");
        if (!string.IsNullOrEmpty(status) && status != "OK")
            throw new InvalidDataException($"minidump-stackwalk reported status: {status}");
        var info = Property(root, "crash_info");
        var modules = Array(root, "modules").Select(module => new CrashModule(
            Text(module, "filename"), Text(module, "version"), Text(module, "base_addr"), Text(module, "end_addr"))).ToArray();
        var mainIndex = Number(root, "main_module", -1);
        var main = mainIndex >= 0 && mainIndex < modules.Length ? modules[mainIndex].Name : string.Empty;
        var thread = Property(root, "crashing_thread");
        if (thread.ValueKind != JsonValueKind.Object)
        {
            var threads = Array(root, "threads").ToArray();
            var index = Number(info, "crashing_thread", -1);
            if (index >= 0 && index < threads.Length) thread = threads[index];
        }
        var frames = Array(thread, "frames").Take(128).Select((frame, index) => new CrashFrame(
            Number(frame, "frame", index), Text(frame, "module"), Text(frame, "module_offset"),
            Text(frame, "function"), Text(frame, "trust"))).ToArray();
        return new(Text(info, "type"), Text(info, "address"), main, frames, modules);
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;
    private static string Text(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } child ? child.GetString() ?? string.Empty : string.Empty;
    private static int Number(JsonElement value, string name, int fallback) =>
        Property(value, name) is { ValueKind: JsonValueKind.Number } child && child.TryGetInt32(out var number) ? number : fallback;
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Array } child ? child.EnumerateArray() : [];
}
