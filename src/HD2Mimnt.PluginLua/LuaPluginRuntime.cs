using HD2Mimnt.PluginSDK;
using MoonSharp.Interpreter;

namespace HD2Mimnt.PluginLua;

/// <summary>脚本型插件暴露给宿主的单个命令。</summary>
public sealed class LuaPluginCommand : IPluginCommand
{
    private readonly LuaPluginRuntime _runtime;

    internal LuaPluginCommand(LuaPluginRuntime runtime, PluginCommandDescriptor descriptor)
    {
        _runtime = runtime;
        Descriptor = descriptor;
    }

    public PluginCommandDescriptor Descriptor { get; }

    public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default)
        => _runtime.ExecuteCommandAsync(Descriptor.Name, context, cancellationToken);
}

/// <summary>
/// Lua 插件运行时：在插件宿主进程内解释执行脚本。
///
/// 沙箱策略：
/// - 只加载 <see cref="CoreModules.Preset_HardSandbox"/>，脚本没有 io/os/debug/loadfile/dofile；
/// - 文件访问只走 <c>hd2.storage</c>（插件私有目录），模块加载限定在插件目录内；
/// - 唯一对外通道是 <c>hd2.ai</c>，请求由主程序代发，脚本进程里没有密钥；
/// - 单脚本状态串行执行，避免多线程同时进入 Lua 状态机。
///
/// 超时策略：Lua 无法被安全中断，因此超时后把这次执行标记为“失控”，
/// 由宿主在把错误回执发出后回收整个插件进程——和其他崩溃路径同一套处理方式。
/// </summary>
public sealed class LuaPluginRuntime : IDisposable
{
    private readonly Script _script;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILuaDllBridge _dll;
    private readonly string _pluginId;
    private readonly Action<string> _verbose;
    private Dictionary<string, IPluginCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private LuaServices? _services;
    private bool _initialized;

    public LuaPluginRuntime(string pluginId, ILuaDllBridge? dll = null, Action<string>? verbose = null)
    {
        _pluginId = pluginId;
        _dll = dll ?? NullLuaDllBridge.Instance;
        _verbose = verbose ?? (_ => { });

        // 硬沙箱：不含 io、os、debug、loadfile/dofile
        _script = new Script(CoreModules.Preset_HardSandbox);
        // 沙箱仍会带出 package 表（模块路径等），脚本用不到，直接摘掉
        _script.Globals.Set("package", DynValue.Nil);
        _script.Options.DebugPrint = message => _verbose(message);
        _script.Options.UseLuaErrorLocations = true;
    }

    public PluginManifest Manifest { get; private set; } = new();

    /// <summary>脚本贡献的命令，键为命令名（忽略大小写）。</summary>
    public IReadOnlyDictionary<string, IPluginCommand> Commands => _commands;

    /// <summary>加载过程中的非致命问题（例如声明了命令但脚本没有 on_command）。</summary>
    public List<string> LoadErrors { get; } = new();

    /// <summary>单次命令执行的超时时间。</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>是否出现过无法中断的脚本执行；为 true 时宿主应回收进程。</summary>
    public bool HasRunawayExecution { get; private set; }

    /// <summary>
    /// 加载脚本并完成初始化。
    /// </summary>
    public async Task InitializeAsync(
        string scriptPath,
        string manifestPath,
        IPluginContext context,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"插件脚本不存在：{scriptPath}");
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        Manifest = PluginManifestJson.Deserialize(manifestJson)
            ?? throw new InvalidOperationException("插件清单解析失败");

        _services = new LuaServices(context.Logger, context.Storage, context.Configuration, context.AI);

        var scriptDirectory = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? ".";
        _scriptDirectoryPath = scriptDirectory;
        var bridge = new LuaApiBridge(
            _script,
            Manifest,
            scriptDirectory,
            () => _services ?? throw new InvalidOperationException("插件尚未初始化"),
            _dll,
            _verbose);
        bridge.Register();

        // 混合插件：先把 DLL 挂上，脚本里才能用 hd2.dll
        if (_dll is not NullLuaDllBridge)
        {
            var ok = await _dll.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
            if (!ok && _dll.LoadError is { } error)
            {
                LoadErrors.Add($"程序集挂载失败：{error}");
            }
        }

        var source = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        _script.DoString(source, null, scriptPath);

        BuildCommands();

        CallHook("on_load");
        _initialized = true;
    }

    /// <summary>按 manifest 声明注册命令；脚本没有对应处理函数时只记录，不阻断加载。</summary>
    private void BuildCommands()
    {
        _commands = new Dictionary<string, IPluginCommand>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in Manifest.Commands)
        {
            if (string.IsNullOrWhiteSpace(descriptor.Name))
            {
                LoadErrors.Add("manifest.commands 中存在空命令名");
                continue;
            }

            _commands[descriptor.Name] = new LuaPluginCommand(this, descriptor);
        }

        if (_commands.Count == 0)
        {
            return;
        }

        if (!HasHandler("on_command") && !_dll.IsAvailable)
        {
            LoadErrors.Add("manifest 声明了命令，但脚本没有定义 on_command，也没有可用的程序集");
        }
    }

    /// <summary>
    /// 执行一个命令：优先交给脚本的 <c>on_command(name, args)</c>；
    /// 脚本没有处理或返回 nil 时，回落到混合插件的 DLL 同名命令。
    /// </summary>
    public async Task<PluginResult> ExecuteCommandAsync(
        string name,
        PluginCommandContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            return PluginResult.Fail("插件尚未初始化完成");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // 命令期间把存储/配置/AI 换成命令上下文，脚本侧 API 不变
        var previous = _services;
        _services = new LuaServices(context.Logger, context.Storage, context.Configuration, context.AI);

        try
        {
            var execution = Task.Run(() => Invoke(name, context.Arguments), CancellationToken.None);

            Task completed;
            try
            {
                completed = await Task.WhenAny(execution, Task.Delay(CommandTimeout, cancellationToken))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                HasRunawayExecution = true;
                return PluginResult.Fail("命令已取消");
            }

            if (completed != execution)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    HasRunawayExecution = true;
                    return PluginResult.Fail("命令已取消");
                }

                HasRunawayExecution = true;
                return PluginResult.Fail(
                    $"脚本执行超过 {CommandTimeout.TotalSeconds:0} 秒，已中止；插件进程将被回收");
            }

            return await execution.ConfigureAwait(false);
        }
        catch (ScriptRuntimeException ex)
        {
            return PluginResult.Fail(DescribeScriptError(ex));
        }
        catch (InterpreterException ex)
        {
            return PluginResult.Fail($"脚本语法错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            return PluginResult.Fail(ex.Message);
        }
        finally
        {
            _services = previous;
            _gate.Release();
        }
    }

    private PluginResult Invoke(string name, string? argumentsJson)
    {
        if (HasHandler("on_command"))
        {
            var args = LuaJson.Parse(_script, argumentsJson);
            if (args.Type != DataType.Table)
            {
                args = DynValue.NewTable(new Table(_script));
            }

            var returned = _script.Call(_script.Globals.Get("on_command"), DynValue.NewString(name), args);
            var converted = ConvertResult(returned);
            if (converted is not null)
            {
                return converted;
            }
        }

        // 混合模式：脚本没处理时交给 DLL
        if (_dll.TryGetCommand(name, out var command))
        {
            var services = _services!;
            var fallbackContext = new PluginCommandContext(
                services.Logger,
                services.Storage,
                services.Configuration,
                argumentsJson,
                services.AI);
            return command.ExecuteAsync(fallbackContext, CancellationToken.None).GetAwaiter().GetResult();
        }

        return PluginResult.Fail(HasHandler("on_command")
            ? $"脚本没有处理命令 '{name}'，程序集中也没有同名命令可回落"
            : $"脚本没有定义 on_command，程序集中也没有同名命令可回落");
    }

    /// <summary>
    /// 把脚本返回值转换成宿主结果。
    /// 支持三种写法：{success=, message=, error=, ui=}、只给 ui、只给字符串。
    /// 返回 null 表示“脚本没表态”，交给上层回落。
    /// </summary>
    private PluginResult? ConvertResult(DynValue value)
    {
        switch (value.Type)
        {
            case DataType.Nil:
            case DataType.Void:
                return null;
            case DataType.String:
                return PluginResult.Ok(value.String);
            case DataType.Boolean:
                return value.Boolean ? PluginResult.Ok() : PluginResult.Fail("脚本返回失败");
            case DataType.Table:
                break;
            default:
                return PluginResult.Ok(value.CastToString());
        }

        var table = value.Table!;
        var successValue = table.Get("success");
        var ui = table.Get("ui");
        var message = LuaTables.Str(table, "message");
        var error = LuaTables.Str(table, "error");

        if (successValue.Type == DataType.Boolean)
        {
            return new PluginResult
            {
                Success = successValue.Boolean,
                Message = message,
                Error = successValue.Boolean ? null : error ?? message ?? "命令执行失败",
                Ui = LuaUi.ToUpdate(ui)
            };
        }

        // 只返回了界面更新
        if (ui.Type == DataType.Table)
        {
            return PluginResult.Ok(message, LuaUi.ToUpdate(ui));
        }

        // 直接返回区块数组
        var blocks = LuaUi.ToUpdate(value);
        return blocks is null ? null : PluginResult.Ok(message, blocks);
    }

    private bool HasHandler(string name)
        => _script.Globals.Get(name).Type == DataType.Function;

    private void CallHook(string name)
    {
        if (!HasHandler(name))
        {
            return;
        }

        try
        {
            _script.Call(_script.Globals.Get(name));
        }
        catch (InterpreterException ex)
        {
            if (name == "on_load")
            {
                throw;
            }

            LoadErrors.Add($"{name} 执行失败：{DescribeScriptError(ex)}");
        }
    }

    /// <summary>异常信息里去掉脚本绝对路径，只保留文件名与行号。</summary>
    private string DescribeScriptError(InterpreterException ex)
    {
        var message = ex.DecoratedMessage ?? ex.Message;
        if (!string.IsNullOrEmpty(_scriptDirectoryPath))
        {
            message = message.Replace(_scriptDirectoryPath + Path.DirectorySeparatorChar, string.Empty);
            message = message.Replace(_scriptDirectoryPath, string.Empty);
        }
        return $"脚本错误：{message}";
    }

    private string _scriptDirectoryPath = string.Empty;

    /// <summary>脚本所在目录。</summary>
    public string ScriptDirectory => _scriptDirectoryPath;

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        CallHook("on_shutdown");
        CallHook("on_unload");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _gate.Dispose();
        _dll.Dispose();
    }
}
