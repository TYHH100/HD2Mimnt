# AI 智能体操作 HD2Mimnt CLI 安全守则

本文写给代替用户操作管理器的 AI 智能体（ZCode、Claude Code 等）。CLI 会在管理器运行时把命令转交给 UI 进程执行，未运行时直接在自己的进程内执行，两种方式都会真实修改数据，**没有撤销功能**——执行前想清楚，执行后验证结果。

## 铁律

1. **破坏性命令必须先向用户复述并获得明确同意**，再带 `--yes` 执行。破坏性命令清单：
   - `mod delete` — 删除模组及其库内文件
   - `profile create` / `profile activate` — 新建或切换活动配置
   - `deploy` — 写入游戏目录
   - `undeploy` — 清空当前安装的全部已部署文件
   - `compat repair` — 修改模组文件结构
   - `settings set` — 修改设置（游戏路径等）
2. **一次只改一个状态**：每执行一条写命令后，用对应的只读命令（`status` / `mod list` / `profile show` / `deploy status`）验证结果，再进行下一步。
3. **先读后写**：动手前先 `status` 和 `mod list` 摸清现状，不要凭记忆操作。
4. **禁用优先于删除**：用户没明确说"删"时，用 `mod disable` 而不是 `mod delete`。
5. **程序化消费一律加 `--json`**，从 `data` 里取 GUID，不要解析人类文本。

## 推荐流程

### 安装一个新模组

```
status                                    # 确认游戏目录、活动配置
mod list --json                           # 确认是否已导入过
mod import "<压缩包路径>"
mod enable "<模组名>"                      # 加入活动配置并启用
compat check "<模组名>"                    # 有问题先看诊断，不要盲目 repair
deploy --yes                              # 部署（先向用户确认）
deploy status                             # 验证部署记录
```

### 用户说"模组坏了/不对劲"

1. `mod show`、`compat check` 只读诊断；
2. 修复结构问题用 `compat repair`（需 `--yes`，先告知用户会改模组文件）；
3. 只是想让它不生效 → `mod disable`，不要 `undeploy`；
4. 部署状态混乱时才考虑 `deploy --yes` 重新部署（幂等，按记录对账）。

### 切换配置

`profile activate` 只是切换指针，**不会改动游戏目录**；真正生效要 `deploy --yes`。切换前告诉用户当前部署会被新配置的内容替换。

## 常见错误

| 错误 | 后果 | 正确做法 |
| --- | --- | --- |
| 对未启用的模组 deploy | 模组不生效，误以为安装失败 | `profile show` 确认成员与启用状态 |
| 在错误的活动配置上操作 | 改错配置 | 每次写操作前 `status` 确认活动配置 |
| 重复 `mod import` 同一压缩包 | 模组库出现重复条目 | 先 `mod list --search` 查重 |
| 名称匹配到多个模组 | 命令报错（安全），AI 却重试改名 | 报错时改用 `mod list --json` 取 GUID |
| `settings set Game.Path` 指向错误目录 | 后续部署写错地方 | set 前确认目录是游戏安装根目录 |

## 错误码约定

- exit 2 且 `requiresYes: true` → 你缺少 `--yes`，这通常意味着该命令是破坏性的，先回到铁律第 1 条。
- exit 2 其他情况 → 用法错误，`help <命令>` 查看用法。
- exit 1 → 业务失败，`message` 里有原因；不要盲目重试同一命令。
