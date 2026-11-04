# HD2Mimnt CLI 命令参考

HD2Mimnt.Cli 是管理器的命令行入口，供外部工具与 AI 智能体操作管理器，**不要求先打开管理器**：

- **管理器在运行时**：命令通过命名管道转发给 UI 进程，由 UI 内同一套 Application 服务执行，保证与图形界面看到的状态一致。
- **管理器未运行时**：CLI 在自己的进程内用同一套 Application/Infrastructure 服务直接执行命令，数据库与存储目录仍按当前用户和数据目录解析。

管道按"当前用户 + 数据目录"派生名字，只允许本机当前用户连接（`CurrentUserOnly`）；管理器在运行时同一时刻只处理一条 CLI 连接，命令天然串行执行。`launch` 仍可用于显式启动管理器 UI（等待就绪上限 60 秒），但不是使用 CLI 的前提。

## 全局选项

| 选项 | 说明 |
| --- | --- |
| `--json` | 以 JSON 输出结果（`exitCode` / `message` / `data` / `requiresYes`），供程序与 AI 消费 |
| `--yes`（`-y`） | 确认破坏性操作；缺少时命令被拒绝并返回用法说明 |
| `--timeout <秒>` | 命令执行的最长时间，默认 600（导入/部署可能较慢，可加大） |

退出码：`0` 成功；`1` 业务失败（如模组不存在、部署失败）；`2` 用法错误或缺少 `--yes` 确认。

## 命令一览

### 总览

- `status` — 版本、游戏目录、活动配置、模组统计、部署会话概要、数据目录。

### 模组

- `mod list [--search 关键字] [--limit 数量]` — 列出模组库；`data` 含每个模组的完整 GUID、启用状态。
- `mod show <ID|名称>` — 模组详情。ID 可以用名称代替（全名唯一优先，其次唯一子串；多义时报错要求 GUID）。
- `mod import <路径...>` — 导入压缩包或文件夹；可一次多个，逐个报告成败。
- `mod delete <ID|名称>` ✱ — 从模组库删除模组。
- `mod enable <ID|名称>` / `mod disable <ID|名称>` — 在活动配置中启用/禁用；模组不在配置内时 enable 会先加入。
- `mod rename <ID|名称> <新名称>` — 重命名。

### 配置（Profile）

- `profile list` / `profile show <ID|名称>`
- `profile create <名称>` ✱
- `profile activate <ID|名称>` ✱ — 切换活动配置；**不会**自动部署，需另执行 `deploy`。
- `profile add-mod <配置> <模组>` / `profile remove-mod <配置> <模组>`

### 部署

- `deploy [--profile 配置] [--method copy|symlink|hardlink] [--order top|bottom]` ✱ — 部署到游戏目录（默认活动配置、copy、top）。游戏目录未配置或不存在时报错。
- `deploy status` — 当前游戏安装的部署记录概要。
- `undeploy` ✱ — 清除当前游戏安装的全部已部署文件并删除记录。

### 兼容性

- `compat check <ID|名称|all> [--reference 版本]` — 检查模组与游戏引用版本的兼容性。
- `compat repair <ID|名称>` ✱ — 按诊断修复模组结构问题。

### 设置（白名单）

- `settings list` / `settings get <键>`
- `settings set <键> <值>` ✱ — 仅白名单键可用：`Game.Path`（目录必须存在）、`Game.AutoDetect`（true/false）、`Game.Mode`（Official/HellForge）、`Game.HellForgePath`、`IAS.InstallLocation`。

### 查询

- `loader list` — 加载器识别配置（角色、优先级、互斥组、从属关系）。
- `ias status` / `ias components` — IAS 安装位置、活动 Runtime、模型组件。
- `help [命令]` — 命令列表或某条命令的用法（本地执行，无需管理器运行）。
- `launch` — 启动管理器 UI 并等待 CLI 管道就绪。

✱ = 破坏性操作，必须带 `--yes`。破坏性操作的完整清单与 AI 操作守则见 [AGENT-SAFETY.md](AGENT-SAFETY.md)。

## 输出格式

默认输出人类可读文本（列表命令含完整 GUID，可直接用于下一步命令）。`--json` 输出信封：

```json
{
  "exitCode": 0,
  "message": "共 3 个模组。",
  "data": [ { "id": "…", "name": "…", "enabled": true } ],
  "requiresYes": false
}
```

## 示例

```bash
HD2Mimnt.Cli launch                 # 可选：顺便启动管理器 UI
HD2Mimnt.Cli status
HD2Mimnt.Cli mod list --json        # 拿到模组 GUID
HD2Mimnt.Cli mod import "D:\mods\某模组.zip"
HD2Mimnt.Cli mod enable "某模组"
HD2Mimnt.Cli compat check "某模组"
HD2Mimnt.Cli deploy --yes           # 部署活动配置
HD2Mimnt.Cli deploy status
```

## 故障排查

- **"与运行中的管理器通信中断"** — 命令可能已执行但未收到结果。先确认结果再决定是否重试，避免重复执行破坏性命令；也可关闭管理器后重试，此时命令会直接在本进程执行。
- **本地执行与管理器数据不一致** — 两者必须使用同一用户、同一数据目录（`HD2MIMNT_DATA_PATH` 一致）；管理器运行期间以它为准，关闭后 CLI 读取同一份 SQLite 与存储目录。
- **"未找到 HD2Mimnt.UI.exe"** — 把 CLI 与主程序放同一目录，或设置环境变量 `HD2MIMNT_UI_EXE` 指向主程序。
- **命令长时间无响应** — 导入/部署耗时属正常；`--timeout` 控制等待上限。转发到管理器时服务端另有单命令 30 分钟上限，本地执行则只受 `--timeout` 约束。
