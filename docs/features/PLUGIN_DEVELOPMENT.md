# 插件开发指南

本文描述当前**已实现并可用**的插件能力。归档文档中的 Phase 17 记录描述的是设计目标，执行链路以本文与源码为准。

## 1. 能做什么

| 能力 | 状态 | 说明 |
|---|---|---|
| 生命周期 | 可用 | `IPlugin.InitializeAsync` / `ShutdownAsync`，宿主**首次执行命令时按需拉起**，见第 2 节说明 |
| 命令贡献（管理页） | 可用 | `IPluginCommand`，`placement` 缺省时在插件管理页生成按钮 |
| 命令贡献（模组右键菜单） | 可用 | `placement: "modContextMenu"`，右键模组卡片出现该命令，调用时附带模组元数据 |
| 私有存储 | 可用 | `IPluginStorage`，限制在插件数据目录内，防路径穿越 |
| 私有配置 | 可用 | `IPluginConfiguration`，存 `plugin.config.json` |
| 日志回传 | 可用 | `IPluginLogger`，经 IPC 写入主程序日志 |
| 受限 AI | 可用 | `IPluginAiClient`，需 `ai.use` 权限；插件**拿不到 API Key** |
| 独立页面 | 可用 | `ui.pages`，安装后作为导航项出现，区块由管理器渲染 |
| 浮窗 | 可用 | `ui.dialogs`，可由功能菜单、模组库工具栏或页面按钮打开 |
| 功能菜单 / 工具栏入口 | 可用 | `ui.menus`，支持 `mainMenu` 与 `libraryToolbar` |
| 主题包 | 可用 | `ui.themes`，白名单设计令牌（颜色/圆角/字号），可一键还原 |
| Markdown 结果渲染 | 可用 | 命令返回的 Markdown 在结果窗口按格式渲染，可切换原文 |
| Lua 脚本插件 | 可用 | `kind: "lua"` + `script`，宿主内置解释器执行，无需编译 |
| 脚本 + DLL 混合 | 可用 | 脚本主控，`hd2.dll.call` 调用程序集里的命令 |
| 模组数据读取 | 未接通 | `IPluginModAccess` 在宿主侧仍抛 `NotImplementedException` |
| 导入/部署事件 | 未接通 | `IModPlugin` / `IDeploymentPlugin` / `IAnalysisPlugin` 无调用点 |

> 为什么界面扩展必须走声明式：插件运行在独立进程，一旦让插件程序集加载进 UI 进程，它就能读取主程序内存中的 API Key，第 6 节的安全边界将完全失效。

## 2. 最小插件

一个插件 = 一个实现 `IPlugin` 的入口类 + 任意数量的 `IPluginCommand` 实现 + `manifest.json`。

```csharp
public sealed class MyPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "com.example.myplugin",
        Name = "我的插件",
        Version = "1.0.0",
        ApiVersion = PluginSdkInfo.Version,
        Assembly = "MyPlugin.dll",
        EntryType = typeof(MyPlugin).FullName!
    };

    public Task InitializeAsync(IPluginContext context, CancellationToken ct = default) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class HelloCommand : IPluginCommand
{
    public PluginCommandDescriptor Descriptor { get; } = new() { Name = "hello", DisplayName = "打招呼" };

    public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken ct = default)
        => Task.FromResult(PluginResult.Ok($"args={context.Arguments}"));
}
```

命令通过 `Descriptor.Name` 分发；宿主启动时扫描程序集内所有实现，manifest 只是**声明**，两者必须一致（声明有、实现无会被记录为警告）。

> **宿主启动时机（懒加载）**：应用启动、安装、启用插件都**不会**拉起插件进程，只有第一次执行该插件的命令（且所需权限已授予）时才启动宿主并调用 `InitializeAsync`。因此界面贡献（页面/菜单/浮窗）在安装后立即出现——它们直接来自 manifest 的静态解析，不依赖插件运行；而 `InitializeAsync` 里的日志要到首次使用才会出现，这是设计行为。不要在 `InitializeAsync` 里假设"应用刚启动"，也不要用它做必须开机执行的工作。

## 3. 用 Lua 写插件（无需编译）

插件有两种写法，跑在同一套进程隔离、权限与界面模型下：

| 写法 | `kind` | 入口 | 适合 |
|---|---|---|---|
| 编译型 | `dotnet`（缺省） | `assembly` + `entryType` | 需要强类型、复杂逻辑、第三方库 |
| 脚本型 | `lua` | `script`（`main.lua`） | 快速迭代、轻量逻辑、改完即生效 |
| 混合型 | `lua` + `assembly` | 脚本主控，DLL 提供底层能力 | 大部分逻辑用脚本写，重活交给 DLL |

三者都是**独立进程**，都拿不到密钥，都通过声明式界面渲染。

### 3.1 目录结构

```
MyLuaPlugin/
├── manifest.json
├── main.lua          # 入口脚本
├── util.lua          # 可选，用 hd2.require("util") 加载
└── MyTools.dll       # 仅混合插件需要
```

```json
{
  "id": "com.example.lua-plugin",
  "name": "Lua 插件",
  "version": "1.0.0",
  "apiVersion": "1.3.0",
  "kind": "lua",
  "script": "main.lua",
  "assembly": "MyTools.dll",     // 可选，混合插件才填
  "commands": [
    { "name": "my-command", "displayName": "我的命令", "placement": "modContextMenu" }
  ]
}
```

脚本型插件的 `entryType` 不再是必填项；`assembly` 在纯脚本插件里省略即可。

### 3.2 生命周期

```lua
function on_load()            -- 可选，加载时调用；抛错会判定插件加载失败
  hd2.log.info("已加载")
end

function on_shutdown()        -- 可选，进程退出前调用
end
```

### 3.3 命令

manifest 里声明的每个命令都调 `on_command`，参数是宿主传进来的表：

```lua
function on_command(name, args)
  if name ~= "my-command" then
    return hd2.result.fail("未知命令：" .. tostring(name))
  end

  local mod = args.mod or {}       -- 从模组右键菜单触发时有 mod 表
  return hd2.result.ok(nil, hd2.ui.update("replace", {
    hd2.ui.markdown("## " .. tostring(mod.name)),
    hd2.ui.facts({ { label = "版本", value = tostring(mod.version) } })
  }))
end
```

返回值有三种写法：

| 返回 | 含义 |
|---|---|
| `hd2.result.ok(message, ui)` | 成功，可带文本与界面更新 |
| `hd2.result.fail(error)` | 失败，错误显示在管理器里 |
| 直接返回字符串 / 区块数组 | 字符串当消息，区块数组等价于 `replace` |
| 返回 `nil` | 视为“脚本没处理”，回落到 DLL 同名命令 |

### 3.4 `hd2` API 表

脚本能触达宿主的方式只有这张表，**没有 `io`、`os`、`debug`、`loadfile`、`dofile`、`require`、`package`，也没有网络与反射**。

| 命名空间 | 方法 | 说明 |
|---|---|---|
| `hd2.plugin` | `id` / `name` / `version` / `directory` | 插件身份信息（只读） |
| `hd2.log` | `info` / `warn` / `error` / `debug` | 写入主程序日志 |
| `hd2.storage` | `dir` / `read` / `write` / `exists` / `delete` | 插件私有目录内的文件 |
| `hd2.config` | `get` / `set` / `delete` / `keys` | 插件私有配置 |
| `hd2.ai` | `available()` / `chat(prompt, opts)` | 受限 AI；请求由主程序代发，脚本拿不到密钥 |
| `hd2.json` | `encode` / `decode` | 表与 JSON 互转 |
| `hd2.ui` | `markdown` / `text` / `heading` / `divider` / `notice` / `facts` / `list` / `input` / `actions` / `update` / `status` | 构造声明式界面 |
| `hd2.result` | `ok` / `fail` / `ui` | 构造返回值 |
| `hd2.dll` | `available()` / `names()` / `has(name)` / `call(name, args)` | 混合插件调用 DLL 命令 |
| `hd2.require` | `require("util")` | 加载插件目录内的 `.lua`，拒绝 `..` 与绝对路径 |

`hd2.ai.chat` 成功返回文本，失败返回 `nil, 原因`：

```lua
local text, err = hd2.ai.chat("把这段说明压缩成三条要点：" .. mod.description)
if not text then return hd2.result.fail(err) end
```

### 3.5 混合编写：脚本调 DLL

`kind: "lua"` 且填了 `assembly` 时，宿主会在**同一个插件进程**里把该程序集按“命令库”加载（不实例化 `IPlugin` 入口），脚本用 `hd2.dll.call` 调用其中实现了 `IPluginCommand` 的类：

```lua
local result, err = hd2.dll.call("hybrid-digest", { text = mod.description })
if result == nil then return hd2.result.fail(err) end

local blocks = { hd2.ui.markdown("## 报告") }
for _, block in ipairs(result.ui.blocks) do   -- DLL 返回的声明式区块可直接并入
  table.insert(blocks, block)
end
return hd2.result.ok(result.message, hd2.ui.update("replace", blocks))
```

DLL 侧就是一个普通的 `IPluginCommand`，与编译型插件写法完全一致：

```csharp
public sealed class DigestCommand : IPluginCommand
{
    public PluginCommandDescriptor Descriptor => new() { Name = "hybrid-digest", DisplayName = "指纹" };

    public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken ct = default)
        => Task.FromResult(PluginResult.Ok("完成", PluginUiUpdate.Replace(
            new PluginUiBlock { Type = PluginUiBlockTypes.Facts, Facts = { new() { Label = "长度", Value = "…" } } })));
}
```

分工原则：**脚本负责流程与界面，DLL 负责脚本不擅长的重活**（哈希、二进制解析、原生互操作、CPU 密集计算）。脚本无法拿 DLL 做别的事——它只能按命令名调用，没有反射入口。

### 3.6 执行限制

- 单次命令默认 **30 秒**超时。Lua 状态机无法被安全中断，因此超时后宿主会把这次执行标记为失控，在回执发出后回收整个插件进程（与崩溃同一套处理），管理器侧看到的是明确的超时错误。
- 脚本串行执行：同一个插件进程内不会有两个命令同时进入 Lua 状态机。
- 脚本无法访问网络；需要联网的能力必须由宿主提供（当前只有 `hd2.ai`）。
- 模块加载限定在插件目录内，路径穿越会被拒绝并报错。

## 4. manifest.json

字段大小写不敏感，枚举可用字符串（`"Sandbox"`）或数字，允许注释与尾逗号。

| 字段 | 必填 | 说明 |
|---|---|---|
| `id` | 是 | 稳定标识，反向域名或 GUID 均可（内部按字符串传递，不再强制 GUID） |
| `name` / `version` / `author` / `description` | 是 / 是 / 否 / 否 | 展示信息；`version` 需为 2–4 段数字 |
| `apiVersion` | 建议 | SDK 契约版本；主版本高于宿主会被拒绝安装 |
| `runtime` | 否 | 目标运行时，如 `net10.0` |
| `permissionMode` | 否 | `Sandbox`（默认）或 `FullAccess` |
| `requestedPermissions` | 否 | 权限标识列表，见下表 |
| `commands` | 否 | 命令声明，供界面在宿主启动前展示 |
| `ui` | 否 | 声明式界面：`pages` / `dialogs` / `menus` / `themes`，见第 5 节 |
| `kind` | 否 | `dotnet`（缺省）或 `lua`；缺省时按编译型处理，旧清单无需改动 |
| `script` | 脚本型必填 | 入口脚本（如 `main.lua`），必须是 `.lua` 文件 |
| `assembly` / `entryType` | 编译型必填 | 程序集文件名与入口类型全名；脚本型不需要 `entryType`，`assembly` 仅混合插件填 |

权限标识：`mods.read`、`mods.write`、`storage.read`、`storage.write`、`config.write`、`network`、`ai.use`、`ui.theme`。

> `ai.use` 是敏感权限：**任何模式下都不会随安装自动勾选**，必须在权限对话框中主动勾选。安装对话框支持逐项勾选，只授予勾选项；“仅本次允许”的授权只留在内存，重启后失效。`ui.theme` 用于应用插件主题，属于低风险外观权限，但同样受“只授予勾选项”约束。

### 命令的 placement

```json
{
  "name": "summarize",
  "displayName": "AI 摘要",
  "requiredPermission": "ai.use",
  "placement": "modContextMenu"
}
```

| 取值 | 出现位置 | 调用参数 |
|---|---|---|
| `pluginPage`（默认） | 设置 → 插件，插件卡片上的命令按钮 | 无 |
| `modContextMenu` | 模组卡片右键菜单 | `{ "mod": { "id", "name", "version", "author", "description" }, "text": "..." }` |
| `mainMenu` | 导航栏底部“插件功能”菜单 | 无 |
| `libraryToolbar` | 模组库左侧工具栏 | 无 |

别名 `mod`、`context-menu`、`menu`、`toolbar`、`library` 等会归一化为上述四种之一。未授权（缺少 `requiredPermission`）的命令在菜单中以禁用状态显示，并标注“需要授权”，不会静默消失。

模组上下文只包含展示信息：**不含文件路径，也不含任何密钥**。插件想读文件仍需 `mods.read` 与后续的 `IPluginModAccess` IPC 通道。

## 5. 声明式界面：页面 / 浮窗 / 菜单 / 主题

插件的界面能力全部写在 manifest 的 `ui` 节点里，**插件程序集不会加载进 UI 进程**：管理器读取声明，用自己的控件渲染，交互结果再以命令调用回传给插件。这样插件可以加页面、加菜单、开浮窗、换主题，但始终接触不到任何 UI 对象或密钥材料。

```json
"ui": {
  "pages": [{
    "id": "ai-tools",
    "title": "AI 工具",
    "icon": "Plugin",
    "description": "把任意文本压缩成要点。",
    "blocks": [
      { "type": "markdown", "text": "支持 **Markdown** 与 `行内代码`。" },
      { "type": "input", "title": "文本", "input": { "id": "text", "multiline": true, "rows": 6 } },
      { "type": "actions", "actions": [
        { "id": "run", "label": "生成摘要", "style": "primary", "command": "summarize", "requiredPermission": "ai.use" }
      ]},
      { "id": "result", "type": "markdown", "text": "_结果会显示在这里。_" }
    ]
  }],
  "dialogs": [{ "id": "quick", "title": "快速摘要", "blocks": [ /* 同上 */ ] }],
  "menus": [
    { "id": "m1", "placement": "mainMenu", "label": "快速摘要…", "dialog": "quick", "requiredPermission": "ai.use" },
    { "id": "m2", "placement": "libraryToolbar", "label": "快速摘要", "dialog": "quick" }
  ],
  "themes": [{ "id": "midnight", "name": "午夜蓝", "tokens": { "accent": "#4C8DFF", "cardCornerRadius": "10" } }]
}
```

### 区块类型

| `type` | 用途 | 主要字段 |
|---|---|---|
| `markdown` | 富文本，按 Markdown 渲染 | `text` |
| `text` | 纯文本段 | `text` |
| `heading` | 标题 | `text`、`level`（1–4） |
| `divider` | 分隔线 | — |
| `notice` | 提示条 | `title`、`text`、`tone`：`info`/`success`/`warning`/`danger` |
| `facts` | 键值对列表 | `facts: [{ label, value }]` |
| `list` | 条目列表 | `items: [{ text, detail, icon }]` |
| `input` | 输入框 | `input: { id, label, placeholder, value, multiline, rows }` |
| `actions` | 按钮组 | `actions: [{ id, label, icon, style, command, dialog, requiredPermission, submitInputs }]` |

### 交互与增量更新

按钮点击后，管理器调用 `action.command`，并把输入框内容一起放进参数：

```json
{
  "ui": { "pluginId": "...", "surfaceId": "ai-tools", "actionId": "run", "values": { "text": "用户输入" } }
}
```

`action.arguments` 里的静态参数会先铺底，`ui` 节点覆盖同名键。插件返回 `PluginResult.Ok(null, uiUpdate)` 即可刷新界面：

```csharp
return PluginResult.Ok(null, new PluginUiUpdate
{
    Mode = PluginUiUpdateModes.Replace,   // 或 Append
    TargetId = "result",                  // 为空表示整个界面
    Blocks = { new PluginUiBlock { Id = "result", Type = PluginUiBlockTypes.Markdown, Text = md } }
});
```

若同时返回 `Message`，有 `Ui` 时作为状态栏提示；没有 `Ui` 时弹出结果窗口，**按 Markdown 渲染**，并可一键切回原文。

浮窗与页面行为一致：`action.dialog` 指向同插件另一个浮窗 id 即可在浮窗内继续跳转。

### 主题包（设计令牌）

主题只接受白名单令牌，值必须是字面量；未知键或非法值会被忽略并在应用结果中提示。插件无法注入样式代码或资源键。

| 类别 | 令牌 |
|---|---|
| 颜色 | `accent`、`accentStrong`、`surface0`–`surface3`、`border`、`borderStrong`、`textPrimary`、`textSecondary`、`textMuted`、`success`、`warning`、`error`、`hoverSurface`、`selectedSurface` |
| 圆角 | `cardCornerRadius`、`overlayCornerRadius`、`notificationCornerRadius`、`subtleCornerRadius`（0–32） |
| 字号 | `pageTitleFontSize`、`sectionTitleFontSize`、`bodyFontSize`、`captionFontSize` |

应用入口在「设置 → 插件」的插件卡片上，页面右上角可一键「恢复默认主题」。应用需要 `ui.theme` 权限；主题切换不改动任何业务逻辑，也不持久化到插件清单。

## 6. 安全边界（密钥）

这是本 SDK 的硬性规则，不是建议：

1. **插件进程内不存在密钥**。宿主只转发提示词，主程序用自己持有的配置发请求；`PluginAiOptions`、`PluginAiResult`、IPC 报文里都没有密钥字段。
2. **提示词会被扫描**。插件可控文本（AI 提示词、系统提示词、日志、命令结果、错误信息）都会与当前真实密钥精确匹配，同时兜底识别 `sk-*`、`sk-ant-*`、`AKIA*`、`ghp_*`、`Bearer` 等格式。
3. **命中真实密钥立即终止**：宿主进程被强杀，插件被停用，`PluginExecutionHistory` 写入一条 `security-violation` / `Blocked` 记录。AI 请求命中时不会发出，模型返回内容命中时不会交给插件。
4. **疑似格式只脱敏不终止**：避免误伤正常文本，替换为 `***` 并记警告。
5. **频率限制**：每个插件每分钟最多 20 次 AI 调用，单次提示词上限 16000 字符，系统提示词上限 4000 字符；输出上限不超过用户配置。

正确的用法是只传业务文本：

```csharp
var result = await context.AI.CompleteAsync(
    prompt: $"把这段模组说明压缩成三条要点：\n{description}",
    options: new PluginAiOptions { SystemPrompt = "你是模组说明编辑。" });
```

不要尝试读取环境变量、配置数据库或 `%APPDATA%` 去找密钥，也不要让模型复述凭据——这类行为会命中上述规则。

**残存风险（如实说明）**：插件与主程序当前以同一 Windows 用户身份运行。若插件绕过 SDK 直接读文件系统，理论上可拿到 DPAPI 密文；`ai.use` 的提示词扫描与终止机制只覆盖经过 SDK 的通道。要彻底阻断需要 OS 级沙箱（受限令牌/AppContainer），尚未实现。

## 7. 版本策略：SDK 不单独拉分支

SDK 是**对外契约**，不是独立产品，因此不拆到单独分支：

- 独立分支会与主程序双向漂移，插件的兼容性只能靠人工记忆；
- 契约版本已由 `PluginSdkInfo.Version` 与 manifest 的 `apiVersion` 表达，宿主按**主版本兼容**校验，破坏性变更提升主版本并明确拒绝旧插件；
- 需要并行维护旧契约时，用发布标签（tag）+ NuGet/本地包引用即可，不需要长期分支；
- 示例插件随仓库主干演进，能在每次构建中验证契约没有回归。

## 8. 示例插件

| 目录 | 演示内容 |
|---|---|
| `samples/plugins/HelloWorld` | 生命周期、命令贡献、参数解析、私有存储计数 |
| `samples/plugins/AiSummarizer` | `ai.use` 权限、AI 代理调用、模组右键菜单命令、独立页面、浮窗、功能菜单与模组库工具栏入口 |
| `samples/plugins/ThemeShowcase` | `ui.theme` 权限、设计令牌主题包（两套配色）、声明式说明页 |
| `samples/plugins/LuaModCheck` | 纯 Lua 插件：无编译产物、脚本读写私有存储、脚本生成报告与浮窗 |
| `samples/plugins/LuaHybrid` | 混合插件：`main.lua` 编排界面，`HybridTools.dll` 计算 SHA-256 并回传区块 |

构建后 `bin/Debug/net10.0/` 里只有插件 dll 与 manifest（SDK 由宿主提供），整个目录即可作为安装源：

```powershell
dotnet build samples/plugins/HelloWorld/HelloWorld.csproj
# 设置 → 插件 → 安装，选择 bin/Debug/net10.0/manifest.json
```

## 9. 调试

- 插件日志会带 `插件 {PluginId}` 前缀写入主程序日志；
- 宿主进程异常或被杀时，当前命令返回明确原因，执行历史同步落库；
- 命令未出现在界面：检查 manifest 的 `commands` 名称是否与 `Descriptor.Name` 完全一致（忽略大小写）；
- 安装后无法启动：检查 `apiVersion` 主版本、程序集是否被替换（安装时记录了 SHA-256）。
