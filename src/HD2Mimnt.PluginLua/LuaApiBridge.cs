using System.Globalization;
using HD2Mimnt.PluginSDK;
using MoonSharp.Interpreter;

namespace HD2Mimnt.PluginLua;

/// <summary>
/// 暴露给脚本的 <c>hd2</c> 全局表。
///
/// 这里是脚本能触达宿主能力的**唯一**入口，因此能力清单是封闭的：
/// 日志、插件私有存储、配置、受限 AI、JSON、声明式界面构造、DLL 命令调用、模块加载。
/// 没有文件系统、没有进程、没有网络、没有任意反射——脚本想做的事必须由宿主代劳，
/// 而宿主代劳的每一件事都还在进程隔离与权限模型之内。
/// </summary>
internal sealed class LuaApiBridge
{
    private readonly Script _script;
    private readonly string _pluginId;
    private readonly string _pluginName;
    private readonly string _pluginVersion;
    private readonly string _pluginApiVersion;
    private readonly string _scriptDirectory;
    private readonly Func<LuaServices> _services;
    private readonly ILuaDllBridge _dll;
    private readonly Action<string> _verbose;
    private readonly Dictionary<string, DynValue> _modules = new(StringComparer.OrdinalIgnoreCase);

    public LuaApiBridge(
        Script script,
        PluginManifest manifest,
        string scriptDirectory,
        Func<LuaServices> services,
        ILuaDllBridge dll,
        Action<string> verbose)
    {
        _script = script;
        _pluginId = manifest.Id;
        _pluginName = manifest.Name;
        _pluginVersion = manifest.Version;
        _pluginApiVersion = manifest.ApiVersion;
        _scriptDirectory = scriptDirectory;
        _services = services;
        _dll = dll;
        _verbose = verbose;
    }

    /// <summary>构造并注册 <c>hd2</c> 表。</summary>
    public void Register()
    {
        var hd2 = new Table(_script);

        hd2.Set("plugin", BuildPluginInfo());
        hd2.Set("log", BuildLog());
        hd2.Set("storage", BuildStorage());
        hd2.Set("config", BuildConfig());
        hd2.Set("ai", BuildAi());
        hd2.Set("json", BuildJson());
        hd2.Set("ui", BuildUi());
        hd2.Set("result", BuildResult());
        hd2.Set("dll", BuildDll());
        hd2.Set("require", Callback((_, args) => Require(StrArg(args, 0))));

        _script.Globals.Set("hd2", DynValue.NewTable(hd2));

        // 习惯用法：脚本里 print 直接落到宿主日志，而不是控制台（控制台输出会污染 IPC）
        _script.Globals.Set("print", Callback((_, args) =>
        {
            var parts = new List<string>(args.Count);
            for (var i = 0; i < args.Count; i++)
            {
                parts.Add(StrArg(args, i));
            }
            _verbose(string.Join("\t", parts));
            return DynValue.Nil;
        }));
    }

    private Table BuildPluginInfo()
    {
        var table = new Table(_script);
        table.Set("id", DynValue.NewString(_pluginId));
        table.Set("name", DynValue.NewString(_pluginName));
        table.Set("version", DynValue.NewString(_pluginVersion));
        table.Set("apiVersion", DynValue.NewString(_pluginApiVersion));
        table.Set("directory", DynValue.NewString(_scriptDirectory));
        return table;
    }

    private Table BuildLog()
    {
        var table = new Table(_script);
        table.Set("info", Callback((_, args) =>
        {
            _services().Logger.Info(StrArg(args, 0));
            return DynValue.Nil;
        }));
        table.Set("warn", Callback((_, args) =>
        {
            _services().Logger.Warning(StrArg(args, 0));
            return DynValue.Nil;
        }));
        table.Set("error", Callback((_, args) =>
        {
            _services().Logger.Error(StrArg(args, 0));
            return DynValue.Nil;
        }));
        table.Set("debug", Callback((_, args) =>
        {
            _services().Logger.Debug(StrArg(args, 0));
            return DynValue.Nil;
        }));
        return table;
    }

    private Table BuildStorage()
    {
        var table = new Table(_script);
        table.Set("dir", Callback((_, _) => DynValue.NewString(_services().Storage.GetDataDirectory())));

        table.Set("read", Callback((_, args) =>
        {
            var name = StrArg(args, 0);
            if (string.IsNullOrWhiteSpace(name) || !_services().Storage.FileExists(name))
            {
                return DynValue.Nil;
            }
            return DynValue.NewString(Sync(_services().Storage.ReadFileAsync(name)));
        }));

        table.Set("write", Callback((_, args) =>
        {
            var name = StrArg(args, 0);
            var content = StrArg(args, 1);
            Sync(_services().Storage.WriteFileAsync(name, content));
            return DynValue.NewBoolean(true);
        }));

        table.Set("exists", Callback((_, args) => DynValue.NewBoolean(
            _services().Storage.FileExists(StrArg(args, 0)))));

        table.Set("delete", Callback((_, args) =>
        {
            Sync(_services().Storage.DeleteFileAsync(StrArg(args, 0)));
            return DynValue.NewBoolean(true);
        }));

        return table;
    }

    private Table BuildConfig()
    {
        var table = new Table(_script);

        table.Set("get", Callback((_, args) =>
        {
            var value = _services().Configuration.Get(StrArg(args, 0));
            return value is null ? DynValue.Nil : DynValue.NewString(value);
        }));

        table.Set("set", Callback((_, args) =>
        {
            Sync(_services().Configuration.SetAsync(StrArg(args, 0), StrArg(args, 1)));
            return DynValue.NewBoolean(true);
        }));

        table.Set("delete", Callback((_, args) =>
        {
            Sync(_services().Configuration.DeleteAsync(StrArg(args, 0)));
            return DynValue.NewBoolean(true);
        }));

        table.Set("keys", Callback((_, _) =>
        {
            var result = new Table(_script);
            var index = 1;
            foreach (var key in _services().Configuration.GetKeys())
            {
                result.Set(index++, DynValue.NewString(key));
            }
            return DynValue.NewTable(result);
        }));

        return table;
    }

    private Table BuildAi()
    {
        var table = new Table(_script);

        table.Set("available", Callback((_, _) =>
            DynValue.NewBoolean(Sync(_services().AI.IsAvailableAsync()))));

        // 成功返回文本；失败返回 nil, 原因（Lua 多返回值约定）
        table.Set("chat", Callback((_, args) =>
        {
            var prompt = StrArg(args, 0);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return DynValue.NewTuple(DynValue.Nil, DynValue.NewString("提示词不能为空"));
            }

            var options = ReadAiOptions(args[1]);
            var result = Sync(_services().AI.CompleteAsync(prompt, options));

            return result.Success
                ? DynValue.NewString(result.Content)
                : DynValue.NewTuple(DynValue.Nil, DynValue.NewString(result.Error ?? "AI 请求失败"));
        }));

        return table;
    }

    private static PluginAiOptions? ReadAiOptions(DynValue value)
    {
        if (value.Type == DataType.String)
        {
            return new PluginAiOptions { SystemPrompt = value.String };
        }

        if (value.Type != DataType.Table)
        {
            return null;
        }

        var table = value.Table!;
        var maxTokens = LuaTables.Int(table, "maxTokens", 0);
        return new PluginAiOptions
        {
            SystemPrompt = LuaTables.Str(table, "system"),
            MaxOutputTokens = maxTokens > 0 ? maxTokens : null
        };
    }

    private Table BuildJson()
    {
        var table = new Table(_script);
        table.Set("encode", Callback((_, args) =>
        {
            var json = LuaJson.Serialize(args[0]);
            return json is null ? DynValue.Nil : DynValue.NewString(json);
        }));
        table.Set("decode", Callback((_, args) => LuaJson.Parse(_script, StrArg(args, 0))));
        return table;
    }

    private Table BuildUi()
    {
        var table = new Table(_script);

        table.Set("block", Callback((_, args) => args[0].Type == DataType.Table ? args[0] : DynValue.Nil));

        table.Set("markdown", Callback((_, args) =>
        {
            var block = NewBlock(PluginUiBlockTypes.Markdown);
            block.Set("text", DynValue.NewString(StrArg(args, 0)));
            ApplyOptions(block, args[1]);
            return DynValue.NewTable(block);
        }));

        table.Set("text", Callback((_, args) =>
        {
            var block = NewBlock(PluginUiBlockTypes.Text);
            block.Set("text", DynValue.NewString(StrArg(args, 0)));
            ApplyOptions(block, args[1]);
            return DynValue.NewTable(block);
        }));

        table.Set("heading", Callback((_, args) =>
        {
            var block = NewBlock(PluginUiBlockTypes.Heading);
            block.Set("title", DynValue.NewString(StrArg(args, 0)));
            block.Set("level", DynValue.NewNumber(Math.Clamp(ToInt(args[1], 2), 1, 4)));
            ApplyOptions(block, args[2]);
            return DynValue.NewTable(block);
        }));

        table.Set("divider", Callback((_, args) =>
        {
            var block = NewBlock(PluginUiBlockTypes.Divider);
            ApplyOptions(block, args[0]);
            return DynValue.NewTable(block);
        }));

        table.Set("notice", Callback((_, args) =>
        {
            var block = NewBlock(PluginUiBlockTypes.Notice);
            block.Set("text", DynValue.NewString(StrArg(args, 0)));
            var tone = StrArg(args, 1);
            block.Set("tone", DynValue.NewString(string.IsNullOrWhiteSpace(tone) ? PluginUiTones.Info : tone));
            var title = StrArg(args, 2);
            if (!string.IsNullOrWhiteSpace(title))
            {
                block.Set("title", DynValue.NewString(title));
            }
            ApplyOptions(block, args[3]);
            return DynValue.NewTable(block);
        }));

        table.Set("facts", Callback((_, args) =>
        {
            var block = NewBlock(PluginUiBlockTypes.Facts);
            block.Set("facts", NormalizeFacts(args[0]));
            ApplyOptions(block, args[1]);
            return DynValue.NewTable(block);
        }));

        table.Set("list", Callback((_, args) =>
        {
            var block = NewBlock(PluginUiBlockTypes.List);
            block.Set("items", NormalizeItems(args[0]));
            ApplyOptions(block, args[1]);
            return DynValue.NewTable(block);
        }));

        table.Set("input", Callback((_, args) =>
        {
            var block = NewBlock(PluginUiBlockTypes.Input);
            var input = new Table(_script);
            input.Set("id", DynValue.NewString(StrArg(args, 0, "value")));
            var label = StrArg(args, 1);
            if (!string.IsNullOrWhiteSpace(label))
            {
                input.Set("label", DynValue.NewString(label));
            }
            if (args[2].Type == DataType.Table)
            {
                var options = args[2].Table!;
                CopyIfPresent(options, input, "placeholder");
                CopyIfPresent(options, input, "value");
                CopyIfPresent(options, input, "multiline");
                CopyIfPresent(options, input, "rows");
            }
            block.Set("input", DynValue.NewTable(input));
            return DynValue.NewTable(block);
        }));

        table.Set("actions", Callback((_, args) =>
        {
            var block = NewBlock(PluginUiBlockTypes.Actions);
            block.Set("actions", NormalizeItems(args[0]));
            ApplyOptions(block, args[1]);
            return DynValue.NewTable(block);
        }));

        // 组装一次界面更新：hd2.ui.update("replace", { a, b }) / hd2.ui.update({ a, b })
        table.Set("update", Callback((_, args) =>
        {
            var update = new Table(_script);
            if (args[0].Type == DataType.String)
            {
                update.Set("mode", DynValue.NewString(args[0].String));
                update.Set("blocks", args[1].Type == DataType.Table ? args[1] : DynValue.NewTable(new Table(_script)));
            }
            else
            {
                update.Set("mode", DynValue.NewString(PluginUiUpdateModes.Replace));
                update.Set("blocks", args[0].Type == DataType.Table ? args[0] : DynValue.NewTable(new Table(_script)));
            }
            return DynValue.NewTable(update);
        }));

        // 只改状态栏提示
        table.Set("status", Callback((_, args) =>
        {
            var update = new Table(_script);
            update.Set("mode", DynValue.NewString(PluginUiUpdateModes.Replace));
            update.Set("message", DynValue.NewString(StrArg(args, 0)));
            update.Set("blocks", DynValue.NewTable(new Table(_script)));
            return DynValue.NewTable(update);
        }));

        return table;
    }

    private Table BuildResult()
    {
        var table = new Table(_script);

        table.Set("ok", Callback((_, args) =>
        {
            var result = new Table(_script);
            result.Set("success", DynValue.NewBoolean(true));
            var message = StrArg(args, 0);
            if (!string.IsNullOrWhiteSpace(message))
            {
                result.Set("message", DynValue.NewString(message));
            }
            if (args[1].Type == DataType.Table)
            {
                result.Set("ui", args[1]);
            }
            return DynValue.NewTable(result);
        }));

        table.Set("fail", Callback((_, args) =>
        {
            var result = new Table(_script);
            result.Set("success", DynValue.NewBoolean(false));
            result.Set("error", DynValue.NewString(StrArg(args, 0, "命令执行失败")));
            return DynValue.NewTable(result);
        }));

        // 只返回界面更新，等价于 ok(nil, ui)
        table.Set("ui", Callback((_, args) =>
        {
            var result = new Table(_script);
            result.Set("success", DynValue.NewBoolean(true));
            result.Set("ui", args[0]);
            return DynValue.NewTable(result);
        }));

        return table;
    }

    private Table BuildDll()
    {
        var table = new Table(_script);

        table.Set("available", Callback((_, _) => DynValue.NewBoolean(_dll.IsAvailable)));

        table.Set("names", Callback((_, _) =>
        {
            var names = new Table(_script);
            var index = 1;
            foreach (var name in _dll.CommandNames)
            {
                names.Set(index++, DynValue.NewString(name));
            }
            return DynValue.NewTable(names);
        }));

        table.Set("has", Callback((ctx, args) =>
            DynValue.NewBoolean(_dll.TryGetCommand(StrArg(args, 0), out _))));

        // 调用 DLL 里的命令，返回 { success=, message=, error=, ui= }
        table.Set("call", Callback((_, args) =>
        {
            var name = StrArg(args, 0);
            if (!_dll.TryGetCommand(name, out var command))
            {
                return DynValue.NewTuple(
                    DynValue.Nil,
                    DynValue.NewString(_dll.IsAvailable
                        ? $"程序集中没有命令 '{name}'"
                        : "本插件未挂载程序集"));
            }

            var services = _services();
            var arguments = args[1].Type == DataType.Table ? LuaJson.Serialize(args[1]) : null;
            var context = new PluginCommandContext(
                services.Logger,
                services.Storage,
                services.Configuration,
                arguments,
                services.AI);

            var result = Sync(command.ExecuteAsync(context, CancellationToken.None));
            return DynValue.NewTable(ToLuaResult(result));
        }));

        return table;
    }

    /// <summary>把 DLL 命令结果转成脚本可读的表，界面更新以表形式透传。</summary>
    private Table ToLuaResult(PluginResult result)
    {
        var table = new Table(_script);
        table.Set("success", DynValue.NewBoolean(result.Success));
        if (result.Message is not null)
        {
            table.Set("message", DynValue.NewString(result.Message));
        }
        if (result.Error is not null)
        {
            table.Set("error", DynValue.NewString(result.Error));
        }
        if (result.Ui is not null)
        {
            // 脚本侧统一 camelCase：清单反序列化对大小写宽容，但脚本取值是精确的
            var json = System.Text.Json.JsonSerializer.Serialize(result.Ui, LuaSerializationOptions);
            table.Set("ui", LuaJson.Parse(_script, json));
        }
        return table;
    }

    private static readonly System.Text.Json.JsonSerializerOptions LuaSerializationOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 模块加载：只允许加载脚本目录内的 .lua 文件。
    /// 不暴露 loadfile/dofile/package，也不接受绝对路径与目录穿越。
    /// </summary>
    private DynValue Require(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return DynValue.Nil;
        }

        // 先做白名单字符校验：拒绝 ..、绝对路径、盘符与任何可疑字符
        if (name.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(name)
            || name.IndexOfAny(new[] { ':', '\\', '\0' }) >= 0
            || !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/'))
        {
            throw new ScriptRuntimeException($"模块名非法：'{name}'；只允许加载插件目录内的 .lua 文件");
        }

        if (_modules.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var moduleName = name.Replace('.', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        foreach (var candidate in new[] { moduleName, Path.Combine(moduleName, "init") })
        {
            var fullPath = SafeResolve(candidate + ".lua");
            if (fullPath is null || !File.Exists(fullPath))
            {
                continue;
            }

            var source = File.ReadAllText(fullPath);
            var chunk = _script.LoadString(source, null, fullPath);
            var result = _script.Call(chunk);
            if (result.Type == DataType.Nil || result.Type == DataType.Void)
            {
                // 模块没有显式返回值时，把它的全局表当作导出
                result = DynValue.NewTable(_script.Globals);
            }

            _modules[name] = result;
            return result;
        }

        throw new ScriptRuntimeException($"找不到模块 '{name}'（只允许加载插件目录内的 .lua 文件）");
    }

    /// <summary>把相对路径限制在插件目录内，拒绝 .. 与绝对路径。</summary>
    private string? SafeResolve(string relativePath)
    {
        try
        {
            var baseDirectory = Path.GetFullPath(_scriptDirectory);
            var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
            var relative = Path.GetRelativePath(baseDirectory, fullPath);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return null;
            }
            return fullPath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Table NewBlock(string type)
    {
        var block = new Table(_script);
        block.Set("type", DynValue.NewString(type));
        return block;
    }

    private static void ApplyOptions(Table block, DynValue options)
    {
        if (options.Type != DataType.Table)
        {
            return;
        }

        foreach (var key in new[] { "id", "title", "icon", "tone" })
        {
            CopyIfPresent(options.Table!, block, key);
        }
    }

    private static void CopyIfPresent(Table source, Table target, string key)
    {
        var value = source.Get(key);
        if (value is not null && value.Type is not (DataType.Nil or DataType.Void))
        {
            target.Set(key, value);
        }
    }

    /// <summary>facts 允许 { label, value } 数组，也允许 { 键 = 值 } 映射。</summary>
    private Table NormalizeFacts(DynValue value)
    {
        var result = new Table(_script);
        if (value.Type != DataType.Table)
        {
            return result;
        }

        var source = value.Table!;
        var index = 1;

        foreach (var table in LuaTables.ArrayTables(source))
        {
            result.Set(index++, DynValue.NewTable(table));
        }

        if (index > 1)
        {
            return result;
        }

        foreach (var pair in source.Pairs)
        {
            if (pair.Key.Type is not (DataType.String or DataType.Number))
            {
                continue;
            }

            var fact = new Table(_script);
            fact.Set("label", DynValue.NewString(pair.Key.CastToString()));
            fact.Set("value", DynValue.NewString(pair.Value.CastToString()));
            result.Set(index++, DynValue.NewTable(fact));
        }
        return result;
    }

    /// <summary>list/actions 允许直接给字符串数组，自动转成对应表结构。</summary>
    private Table NormalizeItems(DynValue value)
    {
        var result = new Table(_script);
        if (value.Type != DataType.Table)
        {
            return result;
        }

        var source = value.Table!;
        for (var i = 1; i <= source.Length; i++)
        {
            var item = source.Get(i);
            if (item.Type == DataType.Table)
            {
                result.Set(i, item);
            }
            else if (item.Type is DataType.String or DataType.Number)
            {
                var table = new Table(_script);
                table.Set("text", DynValue.NewString(item.CastToString()));
                table.Set("label", DynValue.NewString(item.CastToString()));
                result.Set(i, DynValue.NewTable(table));
            }
        }
        return result;
    }

    private static DynValue Callback(Func<ScriptExecutionContext, CallbackArguments, DynValue> handler)
        => DynValue.NewCallback((context, args) =>
        {
            try
            {
                return handler(context, args);
            }
            catch (ScriptRuntimeException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 只把消息文本交给脚本，不把 .NET 堆栈泄进 Lua
                throw new ScriptRuntimeException(ex.Message);
            }
        });

    private static string StrArg(CallbackArguments args, int index, string fallback = "")
    {
        if (index < 0 || index >= args.Count)
        {
            return fallback;
        }

        var value = args[index];
        return value.Type switch
        {
            DataType.String => value.String,
            DataType.Number => value.Number.ToString("R", CultureInfo.InvariantCulture),
            DataType.Boolean => value.Boolean ? "true" : "false",
            DataType.Nil or DataType.Void => fallback,
            _ => value.CastToString()
        };
    }

    private static int ToInt(DynValue value, int fallback)
        => value.Type == DataType.Number ? (int)value.Number : fallback;

    /// <summary>脚本在独立线程上同步调用宿主异步 API；宿主没有同步上下文，不会死锁。</summary>
    private static T Sync<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Sync(Task task) => task.GetAwaiter().GetResult();
}
