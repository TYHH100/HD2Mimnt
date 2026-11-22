# HD2Mimnt CLI 命令参考

HD2Mimnt.Cli 是管理器的命令行入口，供外部工具与 AI 智能体操作管理器，覆盖 GUI 的全部可命令化功能，**不要求先打开管理器**。

## 架构与响应速度

```
HD2Mimnt.Cli.exe   （薄客户端：解析/探测/转发，不持有业务状态）
   ├─ UI 在运行   → 直连 UI 管道（与图形界面同一份内存状态，状态最准）
   ├─ Agent 在运行 → 直连 Agent 管道
   └─ 都没运行    → 自动拉起 HD2Mimnt.Agent serve（分离进程，常驻守护）
HD2Mimnt.Agent.exe （托管服务端：完整业务服务图 + 打开的数据库）
   ├─ serve：常驻；空闲默认 5 分钟自动退出；UI 启动时自动让位
   └─ run-once：一次性执行（daemon 拉起失败或 HD2MIMNT_NO_DAEMON=1 时的兜底）
```

- **日常命令毫秒级**：薄客户端用 Win32 探测管道（亚毫秒），连接后 RTT <1ms；托管客户端的进程启动约 100-300ms，数据库初始化只发生在 Agent 首次拉起（约 1-2 秒，一次性）。
- **长任务有流式进度**：导入/部署/转换等长操作实时输出进度帧；`--json --stream` 时为 NDJSON 事件流（`progress` × N → `result`）。
- **破坏性操作必须 `--yes`**；AI 守则见 [AGENT-SAFETY.md](AGENT-SAFETY.md)。
- 管道按"当前用户 + 数据目录"派生名字（`CurrentUserOnly` 限本机本用户）；同一时刻只处理一条连接，命令天然串行。
- `HD2MIMNT_DATA_PATH` 参与管道命名与数据目录解析，CLI/Agent/UI 三方必须一致。`HD2MIMNT_AGENT_EXE` / `HD2MIMNT_UI_EXE` 覆盖可执行文件定位；`HD2MIMNT_NO_DAEMON=1` 禁用守护拉起；`HD2MIMNT_AGENT_IDLE_SECONDS` 调整空闲退出秒数（0 = 常驻）。

## 全局选项（GNU 常规风格）

| 选项 | 说明 |
| --- | --- |
| `--json` | 输出 JSON 信封（`exitCode` / `message` / `data` / `requiresYes`），程序与 AI 消费 |
| `--yes`（`-y`） | 确认破坏性操作；缺少时命令被拒绝（exit 2 + `requiresYes`） |
| `--stream` | 流式进度（人类模式进度打 stderr；`--json --stream` 输出 NDJSON 事件流） |
| `--async` | 长任务（导入/部署/清理等）提交到后台任务中心，立即返回任务 ID，用 `task wait` 跟进 |
| `--timeout <秒>` | 命令执行最长时间（默认 600） |
| `--help`（`-h`） | 顶层或任意子命令的帮助（`mod list --help` 等价 `help mod list`） |
| `--version` | 输出 CLI 版本 |
| `--` | 之后的所有 token 按字面位置参数处理（传含特殊字符的路径等） |

选项可放在命令行任意位置；`--flag 值` 与 `--flag=值` 两种写法等价。退出码：`0` 成功；`1` 业务失败；`2` 用法错误或缺少 `--yes`。

`schema` 命令输出机器可读的全命令清单（名称/用法/摘要/破坏性标志），AI 一次调用即可发现全部能力：

```bash
HD2Mimnt.Cli schema --json
```

## 命令一览

### 总览与守护

- `status` — 版本、游戏目录、活动配置、模组统计、部署会话概要、数据目录。
- `schema [--json]` — 机器可读命令清单。
- `help [命令]`、`--help`、`--version` — 帮助与版本（本地解析，无需服务端也可转发执行）。
- `launch` — 启动管理器 UI 并等待 CLI 管道就绪（上限 60 秒）。
- `daemon status | start | stop` — 查看/启动/停止 Agent 守护进程。

### 模组

- `mod list [--search 关键字] [--limit 数量]` / `mod show <ID|名称>` — 库列表与详情（ID 可用名称代替，多义时报错要求 GUID）。
- `mod import <路径...>` — 导入压缩包或文件夹；`--async` 提交后台任务；长操作带流式进度。
- `mod delete <ID|名称>` ✱ / `mod rename <ID|名称> <新名称>`。
- `mod enable|disable <ID|名称>` — 在活动配置中启停。
- `mod tag add|remove <ID|名称> <标签>`、`mod tag colors`、`mod tag color <标签> [--color #RRGGBB]`。
- `mod versions list|switch|delete` — 本地版本记录。
- `mod thumbnail <ID|名称> [--generate | --out 路径]` — 查看/生成/导出缩略图。
- `mod update-check <ID|名称>`、`mod link get|set|fetch`、`mod gpu-scan profile|invalidate`、`mod hash <ID|名称>`。
- `mod create from-dir <目录> <输出manifest>` ✱、`mod create pack <目录> <压缩包>` ✱ — 从目录制作模组。

### 配置（Profile）

- `profile list` / `show` / `create` ✱ / `activate` ✱ / `delete` ✱ / `rename` / `duplicate --name`。
- `profile add-mod|remove-mod <配置> <模组>`、`profile move-mod <配置> <分隔符> <模组...>`、`profile reorder <配置> <模组...>`。
- `profile separator add|update|remove|collapse` — 分隔符分组管理。
- `profile compare <A> <B>`、`profile normalize <配置>`。

### 部署

- `deploy [--profile 配置] [--method copy|symlink|hardlink] [--order top|bottom]` ✱ — `--async` 提交后台。
- `deploy status`、`undeploy` ✱（`--async` 支持）。

### 冲突与兼容性

- `conflicts scan [配置]` — 补丁资源覆盖扫描（只读，结构化输出分组与 owner）。
- `compat check <ID|名称|all> [--reference 版本]`、`compat repair <ID|名称>` ✱。
- `compat repair-batch <ID|名称|all> [--dry-run]` ✱、`compat backups <ID|名称>`、`compat restore-backup <ID|名称> <路径|时间>` ✱、`compat refresh-names`。

### 搜索

- `search <关键字> [--fuzzy] [--pinyin]`。

### 任务中心

- `task list [--limit]` / `task show <id>` / `task cancel <id>` / `task kill <id>` / `task clear` / `task wait <id> [--timeout 秒]`。

### 设置

- `settings list` — 全量白名单键 + 类型 + 当前值（外观类键标注"仅 UI 生效"）。
- `settings get <键>` / `settings set <键> <值>` ✱（类型校验）/ `settings reset [前缀]` ✱。

### 游戏与备份

- `game launch` ✱ / `game detect` / `game validate <目录>` / `game version`。
- `game scheduling status|topology` — 只读查看 CPU 调度设置、拓扑与当前状态；`strictAffinityAvailable`/`strictGroupMasksAvailable` 分别表示硬亲和（单组）与组掩码（跨组，需 Windows 11+）可用性。
- `game scheduling set [--mode off|prefer|strict] [--enabled true|false] [...]` ✱ / `apply [--mode prefer|strict]` ✱ / `restore` ✱ — 保存、应用或恢复游戏 CPU 调度；性能核跨处理器组时 `strict` 自动按 CPU Set 组掩码软限制。
- `backup list` / `backup create` / `backup restore <路径>` ✱ / `backup delete <路径>` ✱。

### 应用更新

- `app update-check`、`app update-apply` ✱（下载暂存新版本）。

### 平台浏览与下载

- `browse platforms` / `browse select <平台>` / `browse search <关键字>` / `browse details <ID|链接>` / `browse download <ID|链接> [--file 序号]` ✱。
- `download direct <URL> [--out 目录]` ✱。
- `account nexus api-key [--key KEY] | logout | status` ✱、`account ayaka api-key | logout | status` ✱（OAuth 浏览器授权请用 GUI 账号页）。

### HellForge

- `hellforge status` / `hellforge profiles` / `hellforge profile create <名称> [模板]` ✱ / `hellforge profile select <名称>` ✱。
- `hellforge session start|stop` ✱、`hellforge mods list|add|remove|switch|order` ✱、`hellforge settings get|set` ✱、`hellforge logs`。
- `hellforge sync <配置> [--order top|bottom] [--remove]` ✱ — Worker 任务同步。

### HD2CL 角色库 / MMD

- `hd2cl candidates` / `hd2cl variants <ID|名称>` / `hd2cl convert <ID|名称> --variant 变体名 | --folders 列表` ✱ / `hd2cl export <ID|名称> <目录>`。
- `mmd inspect|convert|export`（convert/export ✱）。

### HD2IAS

- `ias status` / `ias components` / `ias manage status`。
- `ias install <包路径>` ✱ / `ias uninstall` ✱ / `ias generate` ✱ / `ias companion-regenerate` ✱。
- `ias runtime list` / `ias runtime set <运行时ID>`。

### 插件

- `plugin list` / `plugin permissions <id>` / `plugin commands <id>` / `plugin history <id>`。
- `plugin install <包>` ✱ / `enable|disable|uninstall <id>` ✱ / `grant|revoke <id> <权限>` ✱ / `exec <id> <命令> [--args JSON]` ✱。
- 插件的声明式 UI 面板与权限弹窗只在 GUI 呈现；CLI 走参数路径。

### 外部工具与迁移

- `tool list` / `tool register <名称> <exe>` ✱ / `tool delete <id>` ✱ / `tool launch <id> [文件]` ✱。
- `tool soundfix scan`（只读）/ `tool soundfix repair` ✱ / `tool lua-extract` ✱。
- `migration import preview|execute <源目录>`（execute ✱）、`migration export preview|execute <目标目录>`（execute ✱）。

### 加载器与 IAS 系统（旧）

- `loader list` — 加载器识别配置。

✱ = 破坏性操作，必须带 `--yes`；完整清单与 AI 守则见 [AGENT-SAFETY.md](AGENT-SAFETY.md)。

## 输出格式

默认人类可读文本；`--json` 输出信封：

```json
{
  "exitCode": 0,
  "message": "共 3 个模组。",
  "data": [ { "id": "…", "name": "…", "enabled": true } ],
  "requiresYes": false
}
```

`--json --stream` 时为 NDJSON 事件流，最后一帧是同一结构的 `result`。

## 示例

```bash
HD2Mimnt.Cli --version
HD2Mimnt.Cli schema --json             # AI 发现全部命令
HD2Mimnt.Cli status
HD2Mimnt.Cli mod list --json           # 拿模组 GUID
HD2Mimnt.Cli mod import "D:\mods\某模组.zip" --json --stream   # 流式进度
HD2Mimnt.Cli mod import "D:\mods\大包.zip" --async --json      # 提交后台任务
HD2Mimnt.Cli task wait <任务ID> --timeout 1800
HD2Mimnt.Cli profile activate "季票配置" --yes
HD2Mimnt.Cli deploy --yes
HD2Mimnt.Cli conflicts scan --json
HD2Mimnt.Cli daemon status
```

## 故障排查

- **"没有运行中的服务端"** — 首条命令会自动拉起 Agent；若失败可用 `HD2MIMNT_AGENT_EXE` 指定路径，或 `HD2MIMNT_NO_DAEMON=1` 走一次性执行。
- **Agent 行为异常** — 看日志 `<AppData>/Logs/agent.log`（`<AppData>` 为 `%LOCALAPPDATA%\HD2Mimnt`，Debug 为 `HD2Mimnt.dev`）。
- **"与运行中的服务端通信中断"** — 命令可能已执行，先确认结果再决定是否重试，避免重复执行破坏性命令。
- **命令长时间无响应** — 导入/部署耗时属正常；`--timeout` 控制等待上限，服务端单命令上限 30 分钟。
- **UI 与 Agent 互斥** — UI 启动时会请求 Agent 退出；Agent 空闲 5 分钟自动退出，均为正常行为。
