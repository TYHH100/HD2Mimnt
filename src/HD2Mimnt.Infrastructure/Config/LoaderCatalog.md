# LoaderCatalog.json 字段说明

本文件与 `LoaderCatalog.json` 一起随应用分发，是手动新增或更新加载器时（编辑分发的默认值，或在 `<AppData>/loaders.json` 覆盖）的字段参考。解析与使用逻辑在应用代码的 `LoaderRegistry` / `BslModDetector` 中。

`<AppData>/loaders.json` 解析失败会回退随应用分发的默认值，并在日志记录 `LoaderRegistry.InitializationError`。

## 顶层字段

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `"//"` | string | 否 | 注释键，代码不读取，仅供人读。 |
| `ruleVersion` | int | 是 | 规则版本号，同时是识别结果缓存的 `DetectionVersion`。**任何识别特征（GUID、资源、标记、模式等）变更后必须 +1**，否则已按旧规则识别并缓存的模组不会重扫。快照不可变，扫描中途不会读到混合配置。 |
| `dependencyPriority` | int | 否，默认 `1` | 依赖模组（`BslModRole.Dependency`）在部署序列中的分段权重。数值越小越先部署；普通模组为 0，故依赖排在普通模组之后、加载器之前。 |
| `addonExcludes` | string[] | 否，默认 `[]` | BSL `-- HD2-Addon: mods/<author>/<name>` addon 声明的排除名单（小写资源路径，如 `mods/codex/loader`）。命中名单的 addon 声明不把该包认定为依赖——用于排除加载器本体自带的 addon 声明。 |
| `loaders` | object[] | 是 | 加载器定义列表，见下。 |

## loaders[] 每个加载器的字段

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 是 | 配置内唯一标识（不区分大小写），供 `guestOf` 引用和日志定位。只在本文件内使用，不进 UI。 |
| `displayName` | string | 是 | UI 显示名。主互斥组成员按配置顺序拼接为 “BSL / MDL / Boot Mod Loader” 等聚合文案，本地化走 `{0}` 占位模板键，此处写英文基础名。 |
| `role` | string | 是 | 部署槽位，固定映射到 `BslModRole` 枚举：`loader`（主加载器，BSL 用）、`mdl`、`bml`、`csl`、`smoothboot`。**不能自造值**，解析到未知值直接抛异常；新加载器默认用 `loader`，特殊部署位置需在代码中加新枚举槽位。配置移除的历史角色按主加载器段兜底排序。 |
| `priority` | int | 否，默认 `2` | 部署排序分段权重，数值越小越先部署。同 `role` 多个加载器取最小值。现值：依赖 1 < CSL 2 < BSL/MDL/MDL3/BML 3 < SmoothBoot 4。 |
| `mutexGroup` | string | 否 | 主互斥组名。**非 null 即视为主加载器**：同组（实际只用 `primary`）成员只能启用一个，启用一个时自动停用同组其它成员；同时进入配置页末段固定分组。省略该字段的加载器仍是识别目标，但按普通模组排序。 |
| `guestOf` | string | 否 | 从属加载器声明：需由该 `id` 的**主互斥组**加载器托管才能共存（如 CSL 由 BML 启动）。代码校验指向必须存在且为互斥组成员，否则整个配置解析失败。从属加载器固定部署在托管者之前，与 BSL/MDL 仍互斥；启停按组合闭包处理。 |
| `guids` | string[] | 是 | 加载器包的清单 GUID（`Guid.Parse` 解析，格式错抛异常）。最强识别锚点：模组 manifest 的 Guid 或 provenance `*manifest.json` 的 `Guid` 字段命中即认定，不再扫描资源。同时自动加入依赖 GUID 名单——其它包以 GUID 字符串声明依赖也能识别。 |
| `entryResources` | string[] | 否，默认 `[]` | 该加载器**独有**的 Lua 资源名（如 `mods/mdl/runtime`、`mods/codex/smoothboot`、`mods/chef_shared_loader/core_1`）。写入配置的是资源路径字符串，代码内部按 seed-zero MurmurHash64A（ASCII）算哈希比对，**不需要手动算哈希**。命中即认定本体，是 GUID 缺失/变化时的兜底锚点。 |
| `startupChecks` | object[] | 否，默认 `[]` | 启动资源正文标记检查。声明了启动资源的包一律视为“自带启动脚本”，但**是否是加载器本体须正文标记确认**：资源正文须同时包含该条 `markers` 的全部标记才命中（LuaJIT 编译后的字符串常量保留这些标记；不执行脚本，只做字节包含检查）。多个加载器可共享同一启动资源（MDL3 与 BML 共享 `boot`，靠标记区分），按配置顺序第一个全部命中者生效。子字段：<br>• `resource`（string，必填）：启动资源名，如 `boot`、`core/wwise/lua/wwise_flow_callbacks`。<br>• `markers`（string[]，必填）：须全部包含的正文标记，UTF-8 字节比对，空串被丢弃。 |
| `registeredModules` | string[] | 否，默认 `[]` | 该加载器会启动的注册模块资源名（如 BSL 的 `mods/cowboybingus/<name>` 系列）。资源名命中这些路径的包视为“被注册”，在本体存在时归为依赖。注意：旧版自带启动脚本的独立包不能仅凭同名资源认定为依赖（`ownsStartup` 判定），此字段只对不带启动脚本的模块生效。 |
| `requiresPatterns` | string[] | 否，默认 `[]` | .NET 正则（忽略大小写、文化不变、200ms 超时），匹配模组 manifest `Description` 或 provenance JSON 的 `Description` 字段文本，命中即视为声明了依赖（如 `\\bRequires?\\s+MDL3\\b`）。注意 JSON 中的 `\b` 须写成 `\\b`。全部加载器的模式合并为一个总名单，不区分目标加载器。 |
| `dependencyNames` | string[] | 否，默认 `[]` | provenance JSON 的 `requires` / `dependencies` 字段中以**显示名**声明依赖的写法（不区分大小写，如 `"Bingus Shared Loader"`、`"BSL"`）。字符串精确相等比对。 |
| `dependencyKeys` | string[] | 否，默认 `[]` | provenance JSON 的 `requires` / `dependencies` 对象中以**键名**声明 API 版本的写法（不区分大小写，如 `"shared_loader_api"`）。要求键对应的值为正整数，`"shared_loader_api": 2` 之类才算命中。 |

### 识别优先级（实际判定顺序）

1. manifest/provenance 的 GUID 命中 → 直接认定对应 role。
2. provenance 的 `Guid` 字段命中 → 同上；`Description` 命中 `requiresPatterns` 或 `requires`/`dependencies` 命中名字/键 → 标记为依赖候选。
3. 扫描包内全部 `*.patch_*` 的 Lua 资源名：
   - 命中 `entryResources` → 立即认定本体；
   - 命中 `startupChecks` 资源且正文含全部标记 → 认定本体（全部资源扫完后才确认，保证独有入口可覆盖共有标记）;
   - 命中 `registeredModules` 或 BSL addon 声明 → 标记依赖候选。
4. 有依赖候选（或“被注册但不自带启动脚本”）→ `Dependency`；否则不是加载器相关包。

### 部署排序（配置页 → 部署序列）

分段按 `priority` 数值升序：普通模组（0）→ 依赖（`dependencyPriority`）→ 从属加载器 CSL（2，须在托管者 BML 之前）→ 主互斥组 BSL/MDL/MDL3/BML（3）→ SmoothBoot 类末位（4，始终最后部署）。同段保持配置页顺序；反向部署时固定区域反转置顶。互斥/托管行为只看 `mutexGroup` 与 `guestOf`，不看 `priority`。

## 手动新增加载器的步骤

1. 从加载器官方发行包拿到：清单 GUID（`*-manifest.json` 的 `Guid`）、Lua 入口/启动资源的准确资源名、正文标记、依赖声明形式（Description 文本 / `requires` 名字 / API 键）。
2. 在 `loaders` 数组追加条目：`id`、`displayName`、`role`（现有枚举内选）、`guids` 必填；按识别可靠性补 `entryResources`（独有入口最强兜底）和 `startupChecks`（共享启动资源时必须有可区分标记）。
3. 确定共存关系：主加载器加 `"mutexGroup": "primary"`；从属加载器用 `guestOf` 指向托管者；需要永远最后部署的用 `smoothboot` 槽位。
4. 按部署位置给 `priority`（主加载器 3，需更早/更晚的 ±1）。
5. 其它模组声明依赖它的：补 `requiresPatterns`（Description 正则）、`dependencyNames`、`dependencyKeys`，以及该加载器 `registeredModules` 列表。
6. **`ruleVersion` +1**，触发已缓存识别结果重扫。
7. 构建、运行 `LoaderRegistry` / `BslModDetector` 相关测试；用真包导入验证识别结果与配置页分组、部署顺序。

外部覆盖调试：把改好的 JSON 放到 `<AppData>/loaders.json` 即可，无需重装应用；解析失败会回退分发默认并记录 `InitializationError`。
