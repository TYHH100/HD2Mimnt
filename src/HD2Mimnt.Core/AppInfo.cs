using System.Reflection;

namespace HD2Mimnt.Core;

/// <summary>应用版本号的运行时入口，值来自 Directory.Build.props 注入的程序集元数据。</summary>
public static class AppInfo
{
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var value = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3)
            : informational;
        if (string.IsNullOrEmpty(value)) return "0.0.0";
        var metadata = value.IndexOf('+');
        return metadata > 0 ? value[..metadata] : value;
    }
}
