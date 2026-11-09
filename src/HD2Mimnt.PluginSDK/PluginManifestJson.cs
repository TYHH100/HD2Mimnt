using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD2Mimnt.PluginSDK;

/// <summary>
/// manifest.json 的统一读取选项。
///
/// 插件作者按常见习惯写 camelCase、字符串枚举与注释也能正确解析；
/// 宿主内所有读取 manifest 的位置都必须使用这一份选项，避免行为不一致。
/// </summary>
public static class PluginManifestJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static PluginManifest? Deserialize(string json)
        => JsonSerializer.Deserialize<PluginManifest>(json, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        // 允许 "Sandbox" 这类字符串写法，同时兼容旧的数字枚举
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: true));
        return options;
    }
}
