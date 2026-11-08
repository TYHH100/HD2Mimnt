# 插件开发指南

本文描述当前**已实现并可用**的插件能力。归档文档中的 Phase 17 记录描述的是设计目标，执行链路以本文与源码为准。

## 1. 能做什么

| 能力 | 状态 | 说明 |
|---|---|---|
| 生命周期 | 可用 | `IPlugin.InitializeAsync` / `ShutdownAsync` |
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
| 模组数据读取 | 未接通 | `IPluginModAccess` 在宿主侧仍抛 `NotImplementedException` |
| 导入/部署事件 | 未接通 | `IModPlugin` / `IDeploymentPlugin` / `IAnalysisPlugin` 无调用点 |

> 为什么界面扩展必须走声明式：插件运行在独立进程，一旦让插件程序集加载进 UI 进程，它就能读取主程序内存中的 API Key，第 4 节的安全边界将完全失效。

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

## 3. manifest.json

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
| `ui` | 否 | 声明式界面：`pages` / `dialogs` / `menus` / `themes`，见第 4 节 |
| `assembly` / `entryType` | 是 | 程序集文件名与入口类型全名 |

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

## 4. 声明式界面：页面 / 浮窗 / 菜单 / 主题

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

## 5. 安全边界（密钥）

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

## 6. 版本策略：SDK 不单独拉分支

SDK 是**对外契约**，不是独立产品，因此不拆到单独分支：

- 独立分支会与主程序双向漂移，插件的兼容性只能靠人工记忆；
- 契约版本已由 `PluginSdkInfo.Version` 与 manifest 的 `apiVersion` 表达，宿主按**主版本兼容**校验，破坏性变更提升主版本并明确拒绝旧插件；
- 需要并行维护旧契约时，用发布标签（tag）+ NuGet/本地包引用即可，不需要长期分支；
- 示例插件随仓库主干演进，能在每次构建中验证契约没有回归。

## 7. 示例插件

| 目录 | 演示内容 |
|---|---|
| `samples/plugins/HelloWorld` | 生命周期、命令贡献、参数解析、私有存储计数 |
| `samples/plugins/AiSummarizer` | `ai.use` 权限、AI 代理调用、模组右键菜单命令、独立页面、浮窗、功能菜单与模组库工具栏入口 |
| `samples/plugins/ThemeShowcase` | `ui.theme` 权限、设计令牌主题包（两套配色）、声明式说明页 |

构建后 `bin/Debug/net10.0/` 里只有插件 dll 与 manifest（SDK 由宿主提供），整个目录即可作为安装源：

```powershell
dotnet build samples/plugins/HelloWorld/HelloWorld.csproj
# 设置 → 插件 → 安装，选择 bin/Debug/net10.0/manifest.json
```

## 8. 调试

- 插件日志会带 `插件 {PluginId}` 前缀写入主程序日志；
- 宿主进程异常或被杀时，当前命令返回明确原因，执行历史同步落库；
- 命令未出现在界面：检查 manifest 的 `commands` 名称是否与 `Descriptor.Name` 完全一致（忽略大小写）；
- 安装后无法启动：检查 `apiVersion` 主版本、程序集是否被替换（安装时记录了 SHA-256）。
