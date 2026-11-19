using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace HD2Mimnt.Hosting;

/// <summary>
/// 发布版把托管程序集放在应用目录的 lib/ 子目录（原生库仍在应用目录）。
/// 宿主默认只探测应用目录，这里在模块初始化阶段注册解析器——必须早于 Main 执行，
/// 因为 JIT 编译 Main 时就会解析其中引用的依赖程序集。
/// 主程序与 PluginHost/Worker/Cli 四个入口程序集共享同一份实现（各自编译进自己的程序集，
/// 入口 dll 必须与各自 exe 同目录，不能依赖 lib/ 里的程序集来完成解析）。
/// </summary>
internal static class LibAssemblyResolver
{
    private static readonly Lazy<Dictionary<string, string>> Index =
        new(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string LibDirectory => Path.Combine(AppContext.BaseDirectory, "lib");

    [ModuleInitializer]
    internal static void Install()
    {
        if (!Directory.Exists(LibDirectory)) return;
        AssemblyLoadContext.Default.Resolving += Resolve;
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        => Index.Value.TryGetValue($"{name.Name}.dll", out var path)
            ? context.LoadFromAssemblyPath(path)
            : null;

    private static Dictionary<string, string> BuildIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(LibDirectory, "*.dll"))
            index[Path.GetFileName(path)] = path;
        return index;
    }
}
