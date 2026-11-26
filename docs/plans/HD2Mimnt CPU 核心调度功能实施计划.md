# HD2Mimnt — 游戏 CPU 核心调度管理功能实施计划（可执行版）

| 项目 | 内容 |
| --- | --- |
| 模块 | Process Scheduling |
| 目标平台 | Windows 10 / Windows 11 x64 |
| 技术栈 | C# / .NET 10 / Avalonia / Win32 P/Invoke |
| 集成位置 | 设置 → 游戏 → CPU 核心调度 |
| 文档版本 | v2.3（2026-11-22，Phase 0–5 实现完成；停车核心分类修复与跨组严格模式落地；Phase 6 部分受环境限制） |
| 当前状态 | 可构建、可运行；单元 / CLI / Headless UI 验证通过；异构真机与反作弊实测待外部环境 |
| 适用分支 | `dev` |

## 修订说明

v1 的方向正确，但有三类问题会让实现直接走偏：Windows API 语义假设、与仓库现有模型不符、缺少可执行的验证手段。v2 做了以下修订，原有功能目标与安全边界全部保留：

1. 增加 Phase 0 探针，先实测「进程默认 CPU Sets 是否覆盖已有线程」再冻结模式 A 的行为。
2. 补全 API 细节：缓冲探测与 `Size` 步进、`Flags=0`、`Parked`、权限、恢复语义、多处理器组降级。
3. 调整持久化模型：`Profiles` 是模组部署配置，不是游戏启动配置；调度设置改为 `SettingsService` 全局键值。
4. 对齐启动链路：游戏由 Steam 或 HellForge 启动，管理器不是父进程，进程识别必须校验完整映像路径。
5. 补 UI、本地化、CLI、测试和文件落点；明确管理器关闭后的监控边界。

---

## 1. 项目背景

### 1.1 问题描述

部分游戏在 Intel 混合架构 CPU 上存在不理想的线程调度现象。

Intel 混合架构处理器包含：

- P-Core（Performance Core）：性能核，拥有更高的单核性能。
- E-Core（Efficient Core）：能效核，主要用于提高能效和处理适合的工作负载。
- SMT：部分 P-Core 支持超线程，一个物理核心对应多个逻辑处理器。

某些游戏可能无法稳定利用 Windows 的混合架构调度策略，出现游戏关键线程被安排到 E-Core、P-Core 利用率不足等情况。

另外，近期游戏反作弊组件更新后，部分外部调度工具的调度设置可能不再生效。

这里将其视为需要诊断的运行时兼容性问题，不预设反作弊具体采用了何种拦截方式。

### 1.2 功能目标

在 HD2Mimnt 中增加独立的 CPU 调度模块，实现：

1. 自动检测 CPU 拓扑。
2. 自动识别性能核和能效核。
3. 游戏启动后自动应用 CPU 调度策略。
4. 支持严格限制游戏只能运行在性能核上。
5. 检测调度设置是否被改变。
6. 游戏进程重启后自动重新应用。
7. 记录操作结果和 Windows API 错误信息。
8. 不依赖第三方调度软件。

**核心原则：让管理器直接管理游戏进程的 CPU 调度，不依赖其他软件替游戏设置亲和性。**

明确不做：

- 不注入 DLL，不修改游戏内存，不加载内核驱动。
- 不绕过、修改或停止反作弊服务。
- 不通过非正规方式获取进程权限。
- 第一版不做 ETW 线程运行时间统计，不宣称「实际一直运行在 P-Core 上」。
- 第一版不做逐线程选择，除非 Phase 0 证明进程默认 CPU Sets 对已有线程无效（见 §2.2）。

### 1.3 已核对的现状（2026-11-22）

#### 仓库事实

| 事项 | 现状 | 计划对策 |
| --- | --- | --- |
| Profiles 语义 | `Profile` 是模组部署配置，不是游戏启动配置（[Profile.cs](../../src/HD2Mimnt.Core/Entities/Profile.cs)） | 调度设置不按 Profile 保存 |
| 设置持久化 | 全局键值存 SQLite `Settings` 表，键定义在 [SettingsService.cs](../../src/HD2Mimnt.Application/Services/SettingsService.cs#L307) | 新增 `Game.CpuScheduling.*` 键 |
| 游戏目录 | 官方与 HellForge 两种来源，有效目录由 `SettingsService.GetEffectiveGamePath()` 统一解析 | 拓扑与进程识别都以有效目录为准 |
| 游戏主程序 | 固定为 `<游戏目录>/bin/helldivers2.exe`（[GameInstallationPaths.cs](../../src/HD2Mimnt.Application/Services/GameInstallationPaths.cs#L43)） | 用完整路径校验，不只匹配进程名 |
| 启动链路 | Steam `steam://rungameid/553850`，或 HellForge CLI `play --wait yes`（[GameDetectionService.cs](../../src/HD2Mimnt.Application/Services/GameDetectionService.cs#L15)） | 管理器不是父进程，只能观察进程出现 |
| 启动入口 | 模组库与配置页的启动按钮（[LibraryViewModel.cs](../../src/HD2Mimnt.UI/ViewModels/LibraryViewModel.cs#L28)） | 按钮只负责启动；调度由常驻观察器负责 |
| 设置 UI | 「游戏」分类已有来源、目录与自动检测（[SettingsView.axaml](../../src/HD2Mimnt.UI/Views/SettingsView.axaml#L140)） | 在该分类下新增「CPU 核心调度」子项 |
| CLI 约定 | CLI 覆盖 GUI 的全部可命令化功能（[docs/cli/README.md](../cli/README.md#L3)） | 新增 `game scheduling` 命令组 |
| 进程文件夹 | `Core/Interfaces`、`Infrastructure/Interop` 等目录已存在 | 沿用现有分层，不新建平行基础设施 |

#### 本机只读探测结果

2026-11-22 在本开发机调用 `GetSystemCpuSetInformation`（只读，未修改任何进程）：

- CPU：Intel Core i5-12400F，6 个 P-Core，无 E-Core。
- 12 个逻辑处理器，全部在处理器组 0。
- `EfficiencyClass` 全部为 0（12/12），`Parked` 全部为 0。
- 首次以 `NULL/0` 调用返回 `ERROR_INSUFFICIENT_BUFFER`（122），需求 384 字节，每条记录 32 字节。

结论：

- 按本计划的分类规则，本机会判定为「未检测到异构核心」，功能应显示不支持并保持关闭。
- 开发期可以完整验证拓扑解析、权限失败、关闭与降级路径；P/E 分类与 SMT 保留必须依赖固定 fixture 和异构真机验收，不能靠本机。
- 解析必须按记录 `Size` 步进，不能假设固定条数或固定缓冲。

---

## 2. 技术方案

### 2.1 CPU 拓扑识别

使用 Windows 原生 API：

- `GetSystemCpuSetInformation`
- 物理核心与 SMT 关系可由 CPU Sets 的 `Group + CoreIndex` 得到；如需更权威的 SMT 拓扑，可选用 `GetLogicalProcessorInformationEx(RelationProcessorCore)` 交叉校验。

重点字段：

| 字段 | 用途 |
|---|---|
| `Size` | 每条记录的实际长度，必须用它步进 |
| `Type` | 只处理 `CpuSet`；未知类型跳过 |
| `Id` | CPU Set ID，设置进程默认集合时使用 |
| `Group` | 处理器组 |
| `LogicalProcessorIndex` | 组内逻辑处理器编号 |
| `CoreIndex` | 组内物理核心编号；同一核心的 SMT 线程相同 |
| `EfficiencyClass` | 核心性能等级；数值越高越快、越不省电 |
| `Parked` | 是否处于停车状态，不纳入可用集合 |

调用与解析规则：

1. 先传 `Information = NULL, BufferLength = 0`，预期返回 `ERROR_INSUFFICIENT_BUFFER`，从 `ReturnedLength` 取得所需字节数。
2. 分配缓冲后再次调用；`Flags` 必须为 `0`。
3. `Process` 参数可传 `NULL`；它只影响 `AllocatedToTargetProcess` 标记，查询系统拓扑不需要打开游戏进程。
4. 逐条读取时以当前记录的 `Size` 前进；遇到未知 `Type` 直接跳过，不能中断整个枚举。
5. 过滤 `Parked` 核后，按 `Group + CoreIndex` 聚合物理核心；同一核心保留全部逻辑处理器（SMT）。

核心分类规则：

- `EfficiencyClass` 较高的核心视为高性能核心。
- `EfficiencyClass` 较低的核心视为高能效核心。
- 保留同一物理 P-Core 对应的所有逻辑处理器，包括 SMT 线程。
- 不允许假设 CPU 0–7 一定属于 P-Core。
- 不允许根据逻辑处理器编号推测 P/E-Core 类型。
- 如果所有可用核心的 `EfficiencyClass` 相同（包括全部为 0），识别为未检测到异构核心，明确报告不支持。

分类伪代码：

```text
sets       = 枚举结果.Where(s => s.Type == CpuSet && !s.Parked)
cores      = sets.GroupBy(s => (s.Group, s.CoreIndex))
classes    = cores.Select(c => c.EfficiencyClass).Distinct()
if classes.Count <= 1: return Unsupported("未检测到异构核心")
best       = classes.Max()
pCores     = cores.Where(c => c.EfficiencyClass == best)
eCores     = cores.Where(c => c.EfficiencyClass < best)
topology   = 由 pCores / eCores / 全部 sets 生成的统一模型
```

说明：

- Windows 文档只承诺 `EfficiencyClass` 在支持异构处理器的系统上有区分意义；非异构系统可能全为 0，不能据此反推「同一档位就是异构」。
- 若未来出现多于两个档位，最高档视为性能核，其余归入能效核，并在模型中保留原始档位供诊断。
- 拓扑每次启动、每次切换游戏来源时重新识别；不持久化 CPU 编号或 Set ID。

### 2.2 模式 A：性能核优先（Windows CPU Sets）

调用：

- `GetSystemCpuSetInformation`
- `SetProcessDefaultCpuSets`
- `GetProcessDefaultCpuSets`
- 需要逐线程补设时：`SetThreadSelectedCpuSets`、`GetThreadSelectedCpuSets`

语义与限制：

- CPU Sets 是软亲和性，不禁止线程在其他处理器上短暂运行；UI 必须标示「性能核优先」。
- Process Default 是进程内「没有 Thread Selected 赋值」的线程的默认集合。CPU Sets 概览文档把它描述为对进程内后台线程重新设定亲和性的机制；`SetProcessDefaultCpuSets` 参考页则强调之后创建的线程会继承。两处措辞不完全一致，因此必须由 Phase 0 实测已有线程是否被迁移。
- 线程若被单独设置 CPU Sets，线程级选择优先于进程默认；进程或线程存在更严格的 Affinity 掩码时，Affinity 优先于 CPU Sets。
- 被保留（Allocated）的 CPU Set 对普通进程请求会被忽略，线程会被调度到别处。这是「设置成功但实际无效」的主要来源之一。
- `SetProcessDefaultCpuSets` 文档明确「参数合法时不会失败」，不能靠返回值判断成功，必须回查 `GetProcessDefaultCpuSets`。
- 设置后只有回查结果一致才可标记「配置已应用」；这不等于线程实际分布已验证。

恢复：

- 设置前用 `GetProcessDefaultCpuSets` 保存原始 ID 列表；没有默认集合时返回 0 个 ID。
- 恢复时若原始列表为空，调用 `SetProcessDefaultCpuSets(handle, NULL, 0)` 清空；否则写回原 ID。
- 写回前比较当前值是否仍等于最后一次应用值，避免覆盖其他软件后来的修改。

Phase 0 实测结果（2026-11-22，见附录 A）：

- 进程默认 CPU Sets 会覆盖设置前已存在的线程，也会覆盖设置后新建的线程；两组实验分别得到 527/527、526/526 采样落在允许集合内，已有线程 469/469，新建线程 58/58 与 57/57。
- 因此 v1 模式 A 按「进程默认集合」实现，不做逐线程 `SetThreadSelectedCpuSets`。
- 如果以后在其他系统上发现已有线程未迁移，再启用逐线程补设路径；不得在无证据时默认退化。

### 2.3 模式 B：仅性能核（Process Affinity）

调用：

- `GetProcessAffinityMask`（需要 `PROCESS_QUERY_INFORMATION` 或 `PROCESS_QUERY_LIMITED_INFORMATION`）
- `SetProcessAffinityMask`（需要 `PROCESS_SET_INFORMATION`）

规则：

- 掩码由性能核的全部逻辑处理器（含 SMT）生成，位序号是处理器组内的逻辑处理器编号，不是 CPU Set ID。
- 必须保留性能核的全部 SMT 逻辑处理器，不要只分配一个物理核心。
- 掩码必须是系统掩码的子集；否则返回 `ERROR_INVALID_PARAMETER`。
- 单组环境下可直接使用；进程主组不包含全部性能核时，严格模式不可用。
- 多处理器组（>64 逻辑处理器，或性能核跨组）：
  - `GetProcessAffinityMask` 只能表达主组掩码；目标进程的线程跨组时，函数对两个掩码都返回 0；
  - `SetProcessAffinityMask` 在跨组场景返回 `ERROR_INVALID_PARAMETER`；
  - Windows 11 / Server 2022 放宽了默认跨组行为，但仍在显式跨主组设置线程亲和性时失败。
  - 结论：严格模式暂不支持跨组，UI 明确提示，只提供模式 A；Win11 可评估 `SetProcessDefaultCpuSetMasks`（按 `GROUP_AFFINITY` 表达，最低 Windows 11）。
  - 2026-11-22 v2.3：已落地 `SetProcessDefaultCpuSetMasks` 路径——性能核跨组时，Windows 11 / Server 2022+ 按 `UsablePerformanceGroupMasks` 逐组设置进程默认 CPU Set 掩码（软限制），校验与恢复走 `GetProcessDefaultCpuSetMasks`；旧系统返回 `ERROR_PROC_NOT_FOUND`（127）时明确报告"需要 Windows 11"，不静默失败。UI「调度方式」在该场景显示 `CPU Set Masks`。
- Process Affinity 会被子进程继承。游戏派生的子进程也会受限，这是该模式的固有效果，需在提示中说明。
- 严格模式需要明确告知用户可能影响游戏性能或稳定性；不要为了强制绑定而关闭 SMT。

恢复：

- 保存原始进程掩码；应用后回查一致才标记已应用。
- 关闭调度、切换模式或管理器退出时，若当前掩码仍等于最后应用值，写回原始掩码。

### 2.4 模式选择与降级矩阵

| 环境 | 模式 A：性能核优先 | 模式 B：仅性能核 |
| --- | --- | --- |
| 单组、检测到异构核心 | 可用 | 可用 |
| 单组、未检测到异构核心（含本机 i5-12400F） | 不可用，提示不支持 | 不可用，提示不支持 |
| 多组、性能核在同一组 | 可用 | 可用 |
| 多组、性能核跨组 | 可用 | 不可用，提示降级 |
| 无法打开游戏进程句柄 | 权限不足 | 权限不足 |
| 反作弊/系统保留 Set 覆盖 | 配置已应用但可能无效 | Affinity 优先，通常有效 |

原则：无法可靠识别时明确报告不支持，不猜测；严格模式不可用时不能静默产生错误结果。

### 2.5 API 可用性

| API | 最低客户端 | 访问权限 |
| --- | --- | --- |
| `GetSystemCpuSetInformation` | Windows 10 | 无（`Process` 句柄可选，需 `PROCESS_QUERY_LIMITED_INFORMATION`） |
| `GetProcessDefaultCpuSets` | Windows 10 | `PROCESS_QUERY_LIMITED_INFORMATION` |
| `SetProcessDefaultCpuSets` | Windows 10 | `PROCESS_SET_LIMITED_INFORMATION` |
| `SetThreadSelectedCpuSets` | Windows 10 | `THREAD_SET_LIMITED_INFORMATION` |
| `GetProcessAffinityMask` | Windows XP | `PROCESS_QUERY_INFORMATION` 或 `PROCESS_QUERY_LIMITED_INFORMATION` |
| `SetProcessAffinityMask` | Windows XP | `PROCESS_SET_INFORMATION` |
| `SetProcessDefaultCpuSetMasks` | Windows 11 / Server 2022 | `PROCESS_SET_LIMITED_INFORMATION` |

---

## 3. 游戏进程管理

### 3.1 启动链路与观察方式

现有启动链路：

1. 官方：`steam://rungameid/553850`（Shell 启动，管理器不是父进程）。
2. HellForge：优先调用 `HellForge.Cli.exe play --wait yes`，由 `HellForgeSessionService` 观察会话；不支持时打开启动器。

因此：

- 不能依赖「管理器启动的进程就是游戏进程」。
- 只要调度功能启用，观察器就应在管理器运行期间持续工作；用户从 Steam 直接启动游戏也应被识别。
- 启动按钮（[LibraryViewModel.cs](../../src/HD2Mimnt.UI/ViewModels/LibraryViewModel.cs#L28)）只负责发起启动，不负责应用调度；调度由观察器异步完成。
- 启动按钮路径不得因调度失败而报错或阻塞。

### 3.2 进程识别

1. 以 `SettingsService.GetEffectiveGamePath()` 解析有效游戏目录。
2. 目标映像路径规范化为 `Path.GetFullPath(<游戏目录>/bin/helldivers2.exe)`。
3. 枚举 `Process.GetProcessesByName("helldivers2")`；对每个候选：
   - 用 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` 打开；
   - 用 `QueryFullProcessImageNameW` 取得完整路径；
   - 以 `OrdinalIgnoreCase` 比较规范化路径。
4. 不能使用 `Process.MainModule`：访问被拒绝时会抛异常，且不满足最小权限原则。
5. 不能把反作弊服务、Steam 或启动器误认为游戏进程。
6. 同名的其他程序必须因路径不符而被排除。
7. PID 复用：记录进程启动时间（`Process.StartTime` 或 `GetProcessTimes`）；同一 PID 但启动时间不同视为新进程。

### 3.3 进程权限

按模式申请最小必要权限：

- CPU Sets：`PROCESS_QUERY_LIMITED_INFORMATION`、`PROCESS_SET_LIMITED_INFORMATION`。
- 严格 Affinity：`PROCESS_QUERY_INFORMATION`、`PROCESS_SET_INFORMATION`。

`OpenProcess` 或后续 API 返回访问被拒绝时：

- 记录 Win32 错误代码、PID、映像路径和模式。
- 状态置为「权限不足」，显示兼容性提示。
- 不进行注入，不尝试驱动级操作，不尝试绕过反作弊。
- 不阻止游戏正常启动。
- 不进行无限重试。

### 3.4 进程生命周期

必须处理：

- 游戏进程尚未出现：等待，不报错。
- 游戏正常启动：识别、应用、记录。
- 游戏进程异常退出：状态回到「等待游戏启动」。
- 游戏重新启动或 PID 变化：视为新进程，重新获取句柄并应用，不能复用旧 PID 或旧句柄。
- 用户在运行中关闭调度：按恢复规则尽力还原，然后停止观察。
- 切换 Official / HellForge 来源：重新识别拓扑与目标路径。

### 3.5 管理器生命周期边界

- 观察器运行在 UI 进程内，管理器关闭后自动停止。
- 这不会影响已经应用的游戏进程设置；但重新启动游戏后不会再自动应用。
- UI 必须在状态区明确：「保持管理器运行才能持续监测与重新应用」。
- 第一版不把该功能放进 `HD2Mimnt.Agent`（Agent 是 CLI 守护进程，职责与生命周期不同）；需要常驻时另立计划。

---

## 4. 调度状态监测

### 4.1 状态定义

| 状态 | 含义 |
| --- | --- |
| `Disabled` | 功能关闭 |
| `WaitingForGame` | 已启用，等待目标进程出现 |
| `Applying` | 正在应用设置 |
| `Applied` | 回查确认配置与最后一次应用值一致 |
| `Changed` | 配置被其他软件或进程改变 |
| `PermissionDenied` | 打开进程或设置被拒绝 |
| `Unsupported` | CPU 拓扑或模式不支持 |
| `Ineffective` | 设置成功但复查未生效 |
| `Restored` | 已恢复原始设置 |
| `Error` | 其他 Win32 或内部错误 |

### 4.2 监测策略

- 等待进程出现：2 秒间隔，低开销轮询；用户关闭功能立即取消。
- 应用成功后：10 秒间隔复查，不高频轮询。
- 发现设置变化：最多自动恢复 3 次；每次恢复前确认当前值仍是「非我方最后应用值」才覆盖，避免与其他软件打架。
- 连续 3 次权限拒绝：停止自动重试，保持 `PermissionDenied`，等待用户手动重试或重新启用。
- 每次状态变化写一条结构化日志，包含时间、PID、模式、Win32 错误码。
- 不允许持续高频调用 Windows API，影响游戏自身性能。

### 4.3 配置已应用与实际运行分布

必须区分：

- **配置已应用**：`GetProcessDefaultCpuSets` 或 `GetProcessAffinityMask` 查询结果符合管理器配置。
- **实际运行分布**：线程实际在哪些逻辑处理器上运行。

两者不等价。不能仅通过查询接口就宣称游戏实际一直运行在 P-Core 上。UI 文案必须写成「配置已应用」，并补充说明「未统计线程实际运行分布」。ETW 统计留待后续独立评估。

---

## 5. 兼容性与恢复

### 5.1 反作弊兼容性

本功能只使用 Windows 正规的进程调度 API。

- 不注入 DLL，不修改游戏内存，不加载驱动，不改反作弊服务，不绕过反作弊。
- 如果反作弊阻止进程句柄操作，进入 `PermissionDenied`，记录错误码，停止重试。
- 如果 API 设置成功但后续查询发现设置被修改，记录变化时间与结果，在恢复次数上限内处理，不无限重试。
- 不能保证设置一定生效；UI 不承诺强制绑定（模式 A），严格模式也需提示稳定性风险。

### 5.2 恢复

设置前保存目标进程原始状态：

- 原始 Process Affinity。
- 原始 Process Default CPU Sets（可能为空）。

恢复时机：

- 用户在运行中关闭调度。
- 运行中切换模式。
- 管理器退出时尽力恢复（短超时，失败只记录）。

恢复前检查当前状态是否仍是管理器最后应用的状态，避免覆盖其他软件后来作出的修改。游戏正常退出后进程已结束，不需要恢复该进程的调度配置。

原始状态只保存在内存中，不持久化；PID 与句柄不跨进程重启复用。

---

## 6. UI 设计

### 6.1 落点

设置页「游戏」分类下新增「CPU 核心调度」展开项（[SettingsView.axaml](../../src/HD2Mimnt.UI/Views/SettingsView.axaml#L140) 现有游戏来源与目录项之后）。不新建导航页，不放在模组配置页。

可选增强：在模组库 / 配置页的启动按钮旁显示一行只读调度状态；不影响启动流程。

### 6.2 控件

**调度模式**

- 关闭
- 性能核优先（CPU Sets，软限制）
- 仅使用性能核（Process Affinity，严格限制；不支持时禁用并说明原因）

**自动识别**

| 项目 | 示例 |
| --- | --- |
| CPU | Intel Core i5-12400F（只读，来自注册表 `ProcessorNameString`） |
| P-Core | 6 |
| E-Core | 0 |
| 逻辑处理器 | 12 |
| 调度方式 | CPU Sets |
| 拓扑状态 | 未检测到异构核心 / 已识别 / 无法读取 |

**当前调度状态**

显示状态文本、原因与建议操作：

- 等待游戏启动
- 正在应用设置
- 已应用（配置）；附注「未统计线程实际运行分布」
- 调度设置发生变化
- 权限不足（附 Win32 错误码）
- 不支持当前 CPU 拓扑
- 调度功能未生效
- 已恢复原始调度
- 已关闭

**可选设置**

- 游戏启动后自动应用。
- 游戏重启后自动重新应用。
- 运行过程中检查设置变化。
- 关闭调度或退出时恢复原始状态。
- 查看调度日志（跳转现有日志页，不新建日志系统）。

默认关闭。用户明确启用后才操作游戏进程。文字行使用 `Grid` + `ColumnDefinitions="Auto,*"`，不要用横向 `StackPanel` 承载可换行文本。

### 6.3 ViewModel 约定

- CPU 调度逻辑不写入 View / ViewModel；ViewModel 只绑定设置项、接收状态事件。
- 状态事件在后台线程产生，ViewModel 通过 `Dispatcher.UIThread.Post` 回 UI 线程。
- 拓扑展示在进入设置页或点击刷新时读取一次；不随状态轮询刷新。
- 设置保存失败沿用现有错误提示；调度失败不得弹到启动流程里。

### 6.4 本地化

新增文案同步三份资源：

- [Strings.resx](../../src/HD2Mimnt.Application/Localization/Resources/Strings.resx)
- [Strings.zh-CN.resx](../../src/HD2Mimnt.Application/Localization/Resources/Strings.zh-CN.resx)
- [Strings.zh-TW.resx](../../src/HD2Mimnt.Application/Localization/Resources/Strings.zh-TW.resx)

键名建议：`Settings.GameScheduling.*`、`CpuScheduling.Status.*`、`CpuScheduling.Hint.*`。不得在 XAML 中硬编码新中文文案。

---

## 7. 软件架构

### 7.1 分层与文件清单

| 层 | 文件 | 职责 |
| --- | --- | --- |
| Core | `src/HD2Mimnt.Core/Entities/CpuScheduling.cs` | 拓扑模型、核心分类、调度模式、状态与结果 |
| Core | `src/HD2Mimnt.Core/Enums/CpuCoreKind.cs`、`CpuSchedulingMode.cs`、`CpuSchedulingStatus.cs` | 枚举 |
| Core | `src/HD2Mimnt.Core/Interfaces/ICpuTopologyProvider.cs` | 拓扑查询接口 |
| Core | `src/HD2Mimnt.Core/Interfaces/IProcessSchedulingController.cs` | 应用、验证、恢复接口 |
| Core | `src/HD2Mimnt.Core/Interfaces/IGameProcessLocator.cs` | 进程定位接口 |
| Core | `src/HD2Mimnt.Core/Interfaces/IGameCpuSchedulingService.cs` | 上层协调器接口与状态事件 |
| Infrastructure | `src/HD2Mimnt.Infrastructure/Interop/CpuSetNativeApi.cs` | CPU Sets P/Invoke 与缓冲解析 |
| Infrastructure | `src/HD2Mimnt.Infrastructure/Interop/ProcessNativeApi.cs` | `OpenProcess`、`QueryFullProcessImageNameW`、Affinity P/Invoke |
| Infrastructure | `src/HD2Mimnt.Infrastructure/Scheduling/Win32CpuTopologyProvider.cs` | 实现拓扑查询与分类 |
| Infrastructure | `src/HD2Mimnt.Infrastructure/Scheduling/Win32ProcessSchedulingController.cs` | 保存原始值、应用、回查、恢复 |
| Infrastructure | `src/HD2Mimnt.Infrastructure/Scheduling/GameProcessLocator.cs` | 进程枚举与映像路径校验 |
| Infrastructure | `src/HD2Mimnt.Infrastructure/Scheduling/GameProcessWatcher.cs` | 轮询生命周期、PID 变化、退出 |
| Application | `src/HD2Mimnt.Application/Services/GameCpuSchedulingService.cs` | 读取设置、编排拓扑 / 观察器 / 控制器、状态机、日志 |
| Application | `src/HD2Mimnt.Application/Services/SettingsService.cs` | 新增 `SettingKeys` 与默认值 |
| UI | `src/HD2Mimnt.UI/ViewModels/SettingsViewModel.GameScheduling.cs` | 绑定、状态展示、命令 |
| UI | `src/HD2Mimnt.UI/Views/SettingsView.axaml` | 「游戏」分类下的新展开项 |
| UI | `src/HD2Mimnt.UI/App.axaml.cs` | DI 注册 |
| CLI | `src/HD2Mimnt.Application/Cli/CliCommandCatalog.cs`、`CliHandlers.System.cs` | 命令与处理 |
| Tests | `tests/HD2Mimnt.Tests/Scheduling/` | 单元测试与 fixture |
| Tests | `tests/HD2Mimnt.UITests/` | 状态绑定 headless 测试 |
| Tools | `tools/CpuSchedulingProbe/` | Phase 0 探针（不加入解决方案） |

`Infrastructure/Interop` 与 `Infrastructure/Scheduling` 是新命名空间，沿用现有 `Interop` 文件夹；不要把这些实现放进 UI 或 ViewModel。

### 7.2 职责边界

- **CpuTopologyProvider**：只读系统拓扑；不打开游戏进程，不做持久化。
- **ProcessSchedulingController**：无状态查询 + 每个进程一份内存状态；所有 Win32 调用集中于此。
- **GameProcessLocator**：只负责找进程和校验映像路径。
- **GameProcessWatcher**：只负责「何时有目标进程、PID 是否变化、是否退出」；不直接调用调度 API。
- **GameCpuSchedulingService**：唯一协调器；读取设置、决定模式、调用控制器、维护状态、写日志、发事件。
- **UI / CLI**：只调用 Application 服务。

### 7.3 线程模型与取消

- 观察器在后台 `Task` 运行，持有 `CancellationTokenSource`。
- 启用、禁用、切换设置、应用退出时传递取消。
- 关闭时等待观察任务结束（设短超时），再释放句柄。
- 任何 Win32 调用不阻塞 UI 线程；状态通知由 ViewModel 调度回 UI 线程。
- 不并行构建、不并发修改共享 `bin/obj`（仓库现有约束）。

### 7.4 DI 注册

在 [App.axaml.cs](../../src/HD2Mimnt.UI/App.axaml.cs) 的 `ConfigureServices` 注册：

- `ICpuTopologyProvider` → `Win32CpuTopologyProvider`（单例）
- `IProcessSchedulingController` → `Win32ProcessSchedulingController`（单例）
- `IGameProcessLocator` → `GameProcessLocator`（单例）
- `IGameCpuSchedulingService` → `GameCpuSchedulingService`（单例）
- `GameCpuSchedulingService` 构造依赖 `SettingsService`、`ICpuTopologyProvider`、`IProcessSchedulingController`、`IGameProcessLocator`、`ILogger<GameCpuSchedulingService>?`

CLI / Agent 通过同一容器解析，不复制实现。

---

## 8. 配置持久化

### 8.1 键定义

沿用 `SettingsService` 与 SQLite `Settings` 表；在 `SettingKeys` 增加：

| 键 | 类型 | 默认值 |
| --- | --- | --- |
| `Game.CpuScheduling.Enabled` | bool | `false` |
| `Game.CpuScheduling.Mode` | enum：`Off` / `PreferPerformanceCores` / `PerformanceCoresOnly` | `Off` |
| `Game.CpuScheduling.ApplyOnLaunch` | bool | `true` |
| `Game.CpuScheduling.ReapplyOnRestart` | bool | `true` |
| `Game.CpuScheduling.MonitorChanges` | bool | `true` |
| `Game.CpuScheduling.RestoreOriginal` | bool | `true` |

CLI `settings` 的 `KeyTypes` 同步加入这些键，保证 `settings list/set/reset` 与 GUI 行为一致（[CliHandlers.System.cs](../../src/HD2Mimnt.Application/Cli/CliHandlers.System.cs#L16)）。

### 8.2 兼容与容错

- 旧配置没有这些键 → 全部取默认值 → 功能关闭，行为与现在一致。
- 配置值损坏 → `SettingsService.ReadSetting` 已做类型转换兜底；调度服务仍要捕获所有异常。
- 调度初始化或应用失败不得抛到游戏启动流程，不得阻止游戏启动。
- 不保存机器上固定的 CPU 编号或 Set ID；每次启动都重新识别拓扑。
- 原始进程状态只存内存，不写数据库。

### 8.3 为什么不用「每个游戏配置独立 JSON」

- 本仓库的 `Profile` 是模组部署配置，不是游戏启动配置；把调度放进 Profile 会让「切换模组配置」意外改变 CPU 策略。
- 当前只支持一个游戏，同一时刻只有一种活动来源（Official / HellForge）。
- v1 采用全局一份设置，作用于当前活动来源；如果以后需要两种来源不同策略，再增加 `Game.CpuScheduling.Official.*` / `Game.CpuScheduling.HellForge.*`，不改变现有键即可迁移。
- `topologyPolicy` 固定为「最高 EfficiencyClass 为性能核」，属于算法规则，不作为用户配置持久化。

---

## 9. 测试计划

### 9.1 Phase 0 探针（先于一切实现）

状态：已于 2026-11-22 在本机执行，结果见附录 A。

目的：验证模式 A 是否覆盖已有线程，并锁定 API 行为。

工具：`tools/CpuSchedulingProbe`（控制台项目，不加入 `HD2Mimnt.slnx`），复用最终要迁入 Infrastructure 的 P/Invoke 代码。

子命令：

```text
dotnet run --project tools/CpuSchedulingProbe -- topology
dotnet run --project tools/CpuSchedulingProbe -- spinner --seconds 60
dotnet run --project tools/CpuSchedulingProbe -- sets --pid <pid>
dotnet run --project tools/CpuSchedulingProbe -- apply --pid <pid> --mode prefer
dotnet run --project tools/CpuSchedulingProbe -- apply --pid <pid> --mode strict
dotnet run --project tools/CpuSchedulingProbe -- restore --pid <pid>
dotnet run --project tools/CpuSchedulingProbe -- denied --pid <受保护进程 PID>
```

验证项：

1. `topology`：缓冲探测、`Size` 步进、`Group/CoreIndex` 聚合、`EfficiencyClass` 输出；与本机已知结果一致（12 套、全 0）。
2. `spinner`：子进程内多个线程按固定间隔报告 `GetCurrentProcessorNumber`，输出前后分布。
3. `apply --mode prefer`：设置前后分别查询 `GetProcessDefaultCpuSets`，并输出每个线程的 `GetThreadSelectedCpuSets`；对照 `spinner` 分布判断已有线程是否迁移。
4. `restore`：原始为空时恢复为 0 个 ID；原始非空时恢复一致。
5. `--mode strict`：设置并回查 Affinity；在 notepad 等可写进程上验证恢复。
6. `denied`：对受保护进程执行 `OpenProcess`，确认返回 `ERROR_ACCESS_DENIED`、不崩溃、不重试。
7. 权限不足路径：分别用两种所需权限打开同一个目标，记录差异。

通过标准：

- 设置 → 回查往返一致；恢复后回到原始状态。
- `spinner` 在应用后只落在允许集合内 → 模式 A 可按「进程默认即可覆盖已有线程」实现。本机已通过（附录 A）。
- 若在其他系统应用后仍散布在全部核心 → 按 §2.2 决策门处理，并把结论追加到本文件。
- 探针中验证过的解析与调用代码原样迁入 Infrastructure，避免二次实现。

### 9.2 拓扑测试（fixture）

固定 JSON 输入，覆盖：

- Intel 全 P-Core（含本机 12 线程、`EfficiencyClass` 全 0）。
- Intel P-Core + E-Core（无 SMT / 有 SMT）。
- 多处理器组（P 核同组、P 核跨组）。
- 存在 `Parked` 核。
- 存在多个效率档位。
- 未知 `Type` 记录混在缓冲中。

断言：P/E 数量、SMT 逻辑处理器完整性、严格模式可用性、`Parked` 排除、未识别时不猜测。

### 9.3 API 测试

- 能否获取 CPU Sets，缓冲不足时是否正确重试。
- 能否设置进程默认 CPU Sets，回查是否一致。
- 能否获取原始 Affinity，设置严格 Affinity 后回查。
- 能否正确恢复默认集合与原始掩码。
- 对不存在 PID 是否安全返回。
- 对访问被拒绝的目标是否降级为 `PermissionDenied` 且不重试。
- `SetProcessAffinityMask` 在跨组环境返回 `ERROR_INVALID_PARAMETER` 时是否降级。

真实 API 测试只在 Windows 集成测试中运行；没有异构硬件的 CI 用 fixture，不伪造 P/E 结果。

### 9.4 进程与状态机测试

用 mock 的 `IGameProcessLocator` / `IProcessSchedulingController`：

- 进程未出现 → `WaitingForGame`。
- 出现但路径不符 → 不应用。
- 正常应用 → `Applied`。
- 配置被改 → `Changed`，恢复次数上限 3。
- 连续权限拒绝 → `PermissionDenied` 并停止重试。
- 进程退出 → 回到 `WaitingForGame`，句柄关闭。
- PID 相同但启动时间不同 → 视为新进程。
- 用户禁用 / 管理器退出 → 按开关尽力恢复。

### 9.5 UI 与 CLI 测试

- Headless UI：状态文本、拓扑数字、模式切换、禁用态与不支持提示的绑定。
- CLI：`schema --json` 中出现新命令；`status/topology` 只读；`set/apply/restore` 需要 `--yes`；`--json` 输出字段稳定。
- 本地化：三份 resx 键齐全，无硬编码新增文案。

### 9.6 游戏实测（需要异构真机）

分别测试：

1. 不使用调度功能。
2. 性能核优先。
3. 仅使用性能核。

保持相同画面设置、游戏场景和运行条件。记录：

- 平均 FPS、1% Low、帧时间波动。
- CPU 总占用、P-Core / E-Core 利用情况。
- 游戏稳定性、反作弊相关错误。
- 调度日志中的权限错误或设置变化。

可用 ETW 或 Windows Performance Recorder / Analyzer 做诊断，但只作为验收辅助，不进入第一版运行时。

不能仅凭 CPU 总占用率判断调度功能是否有效。

---

## 10. 开发阶段与验收

### Phase 0 — API 探针（新增）

状态：已完成（2026-11-22），结论见附录 A。

交付：

- `tools/CpuSchedulingProbe` 可运行。
- 结论：模式 A 对已有线程是否生效；失败码与权限差异。
- 结果追加到本文件，附原始输出摘要。

验收：§9.1 全部通过；未通过则不得进入 Phase 2 实现。

### Phase 1 — CPU 拓扑识别

状态：已完成（2026-11-22）。

交付：

- `CpuTopologyProvider` + 模型 + 分类器。
- JSON fixture 与单元测试。
- 设置页只读拓扑展示。

验收：

- 正确识别 P/E-Core，保留 SMT 信息。
- 不依赖固定 CPU 编号。
- `EfficiencyClass` 全 0 / 无法读取时明确报告不支持。
- 本机显示「未检测到异构核心」。

### Phase 2 — CPU Sets（模式 A）

状态：已完成（2026-11-22）。按 Phase 0 结论使用进程默认 CPU Sets，不做逐线程补设。

交付：

- P/Invoke、控制器、回查与恢复。
- 状态机与日志。
- Phase 0 决策结果落地（逐线程补设或明示限制）。

验收：

- 可以对测试程序应用、回查、恢复。
- 权限不足有明确错误提示与错误码。
- 设置失败不影响目标进程运行。

### Phase 3 — Process Affinity（模式 B）

状态：已完成（2026-11-22）。单组走硬亲和；v2.3 起性能核跨组时在 Windows 11 / Server 2022+ 按 CPU Set 组掩码软限制（`UsesMasks` 分支），旧系统明确降级为 Unsupported。

交付：

- 掩码生成、应用、回查、恢复。
- 多组检测与降级提示。
- 严格模式风险提示。

验收：

- 掩码包含全部 P-Core 的 SMT 线程。
- 跨组环境主动降级，不静默失败。
- 恢复后回查一致。

### Phase 4 — 游戏进程集成

状态：已完成（2026-11-22）。采用后台轮询 + 设置变更唤醒，未单独实现 `GameProcessWatcher` 类。

交付：

- `GameProcessLocator`、`GameProcessWatcher`、`GameCpuSchedulingService` 接入。
- 应用启动时按设置启动观察；启动按钮不阻塞。
- HellForge 模式路径校验。

验收：

- 能识别真正的游戏进程，排除同名程序。
- 游戏重启后重新应用；PID 变化不复用旧上下文。
- 不操作反作弊服务和其他无关进程。
- 管理器关闭后状态明确变为「未监测」。

### Phase 5 — UI、配置与 CLI

状态：已完成（2026-11-22）。

交付：

- 设置页游戏分类下的调度子项。
- 六个设置键与默认值、容错。
- 三份 resx。
- `game scheduling status|topology|set|apply|restore` 命令。
- 更新 [docs/cli/README.md](../cli/README.md) 与 [docs/cli/AGENT-SAFETY.md](../cli/AGENT-SAFETY.md)。

验收：

- 默认关闭；旧配置读取正常。
- 每个设置项保存与重载正确。
- 状态机全部状态可展示，原因与建议可读。
- CLI 与 GUI 读取同一设置、同一状态；写命令需要 `--yes`。

### Phase 6 — 兼容性与真机验收

状态：部分完成（2026-11-22）。权限拒绝路径已用探针实测（14/14 线程 `ERROR_ACCESS_DENIED`，无 setter 调用与重试）；
异构 P/E 真机、反作弊在线环境不可用，相关游戏实测与 ETW 统计未执行，保留为外部验收项。

交付：

- 反作弊环境测试记录。
- 异构真机（Intel P+E）与 SMT 场景记录。
- 问题修复与文档更新。

验收：

- 不引入驱动或注入。
- 访问失败时不崩溃，不阻止游戏启动。
- 日志包含错误代码和进程信息。
- 不出现无限重试。
- 模式 A / B / 关闭三种状态均可复现。

### 实现与验证记录（2026-11-22）

- `dotnet build HD2Mimnt.slnx --no-restore` 通过，0 error。
- 调度单元测试：拓扑 6、控制器 8、服务 16，共 30 项通过；CLI 命令目录 5 项通过；Headless UI 1 项通过。
- 安全恢复：服务层不再强制覆盖；当前值已被其他软件修改时返回 `Changed` 并保留原始状态，不做写入。
- 状态机补强：模式切换会先恢复旧状态再重新应用；用户在权限拒绝 / 等待游戏后修改设置会触发一次显式重试；
  进程退出或 PID 复用时清理控制器内原始状态；设置保存（GUI 与 CLI）会唤醒后台轮询。
- CLI 实机（本机 i5-12400F）：`game scheduling topology` 返回 12th Gen、未检测到异构核心；
  `set --mode strict` 后状态为 `Unsupported`，设置成功持久化且默认关闭语义未被破坏。
  拓扑行「可用逻辑处理器」显示可用集合 / 全部集合，避免把 Windows 当前停车的集合误报为缺失。
- 未执行：异构 P/E 真机分类、SMT 完整保留的实机复核、游戏反作弊在线环境、帧率 / ETW 统计。
  这些项目只由 fixture 与静态路径覆盖，不能据此宣称真实游戏运行分布。

### 实现与验证记录（2026-11-22 v2.3 追加）

- 停车核心分类修复：i7-14650HX 笔记本（用户代他人反馈）截图显示 24 集合仅 6 个可用且全为 `EfficiencyClass=1`，被误判"未检测到异构核心"。根因是 `Analyze` 只用非停车集合分类，而 Windows 停车了全部 E-Core。修复后分类覆盖全部集合，应用目标仍只用未停车集合（`UsablePerformanceSets`）；新增回归 fixture「E-Core 全部停车仍识别大小核」。
- 同构机器展示修正：全 P-Core 机器（如 i5-12400F）`EfficiencyClass` 单档位时，保持 `Supported=false`（调度为无操作），但拓扑展示按"全部核心为性能核"计数，不再显示 0/0。
- 跨组严格模式落地：`SetProcessDefaultCpuSetMasks` / `GetProcessDefaultCpuSetMasks`（kernel32，Win11 / Server 2022+）按 `GROUP_AFFINITY` 逐组应用与回查；旧系统返回 127 时降级为明确的不支持提示。控制器 `AppliedState` 增加 `UsesMasks` 分支，恢复语义与其他模式一致（回查当前值、不覆盖他方修改）。
- 验证：构建 0 错误；调度域 37/37 通过；Headless UI 1/1 通过。跨组与 Win10 缺 API 路径由 fixture 覆盖，异构真机复验仍为外部验收项。

---

## 11. 最终验收

功能交付时必须满足：

1. 能自动识别混合架构 CPU；无法识别时明确报告不支持。
2. 能将游戏限制在用户选择的性能核集合（模式 B），或以软亲和性优先（模式 A）。
3. 支持 CPU Sets 与严格 Affinity 两种模式，并明确各自限制。
4. 能正确处理游戏进程的启动、退出和重启。
5. 能检测配置变化，恢复尝试有上限。
6. 能安全恢复原始设置，且不覆盖其他软件后续修改。
7. 不依赖第三方调度器。
8. 不引入反作弊绕过机制。
9. 不影响原有游戏启动流程；调度失败不阻塞启动。
10. 全部功能均可独立关闭，默认关闭。
11. UI 不宣称「实际运行在 P-Core 上」，只声明「配置已应用」。
12. CLI 覆盖可命令化功能，写命令有安全标记。
13. 三份本地化资源齐全，无新增硬编码文案。
14. 单元测试、CLI 测试、Headless UI 测试通过；异构真机验收单独记录。

最终定位：HD2Mimnt 的游戏运行时 CPU 调度管理模块，而不是一个针对特定 CPU 型号的临时优化补丁。

---

## 12. 风险与决策记录

| 事项 | 决策 |
| --- | --- |
| 设置作用域 | v1 全局一份，作用于当前活动游戏来源；不按 Profile 保存 |
| 模式 A 已有线程 | 已实测覆盖已有线程（2026-11-22，两组实验），v1 不逐线程补设；其他系统保留复查 |
| 多组严格模式 | 单组硬亲和；跨组在 Win11+ 按 CPU Set 组掩码软限制（明确提示），旧系统降级为不支持 |
| 实际运行分布 | v1 不统计，只验证配置；UI 明示 |
| 管理器关闭 | 停止监测；已应用的设置保留，重启游戏后不再自动应用 |
| 进程识别 | 完整映像路径匹配，不使用 `MainModule`，不按进程名单独判定 |
| 失败重试 | 恢复最多 3 次；连续权限拒绝停止自动重试 |
| ETW | 后续独立评估，不进入第一版依赖 |

主要风险：

- 反作弊或系统保留 CPU Set 导致「配置已应用但实际无效」；通过回查与状态区分降低误报。
- 严格模式的 Affinity 会被子进程继承，可能影响游戏派生的子进程。
- 管理器未运行时无法持续监测；需在 UI 明示。
- 异构真机不在当前开发机，P/E 验收依赖外部设备。

---

## 13. 参考

### 官方

- [Microsoft — CPU Sets](https://learn.microsoft.com/en-us/windows/win32/procthread/cpu-sets)
- [Microsoft — GetSystemCpuSetInformation](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemcpusetinformation)
- [Microsoft — SYSTEM_CPU_SET_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-system_cpu_set_information)
- [Microsoft — GetProcessDefaultCpuSets](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocessdefaultcpusets)
- [Microsoft — SetProcessDefaultCpuSets](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessdefaultcpusets)
- [Microsoft — SetProcessDefaultCpuSetMasks](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessdefaultcpusetmasks)
- [Microsoft — SetThreadSelectedCpuSets](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadselectedcpusets)
- [Microsoft — SetProcessAffinityMask](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setprocessaffinitymask)
- [Microsoft — Processor Groups](https://learn.microsoft.com/en-us/windows/win32/procthread/processor-groups)
- [Microsoft — QueryFullProcessImageName](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew)

### 仓库

- [项目基础与常用命令](../agent-guides/project-basics.md)
- [测试与生命周期](../agent-guides/testing-and-lifecycle.md)
- [UI 与 Avalonia 约定](../agent-guides/ui-and-avalonia.md)
- [本地化](../agent-guides/localization.md)
- [CLI 说明](../cli/README.md)
- [AI 智能体操作安全守则](../cli/AGENT-SAFETY.md)

---

## 附录 A — Phase 0 探针设计

### 目标进程

- `spinner` 子命令：自身创建 4 个工作线程，每 100ms 记录一次 `GetCurrentProcessorNumber`，运行 N 秒后输出每个逻辑处理器的采样数。
- 探针用 `apply` 对 `spinner` 的 PID 设置默认 CPU Sets，比较应用前后分布。
- 另用 `notepad.exe` 验证 Affinity 模式与恢复。

### 关键观察点

1. `SetProcessDefaultCpuSets` 返回后，`GetProcessDefaultCpuSets` 是否与写入一致。
2. 已有线程的 `GetThreadSelectedCpuSets` 是否为空（预期为空）。
3. 应用后 `spinner` 的采样是否只落在目标集合。
4. `restore` 后是否回到应用前分布（允许调度延迟，按窗口统计）。
5. 目标进程受保护时的错误码与状态。

### 记录格式

在本文件末尾追加：

```text
日期：2026-XX-XX
机器：CPU 型号 / 逻辑处理器 / 处理器组
结论：模式 A 覆盖已有线程（是/否）
证据：spinner 应用前/后分布、GetProcessDefaultCpuSets 往返、错误码
后续：逐线程补设 / 明示限制 / 其他
```

### 实测结果（2026-11-22）

环境：

- Windows 10.0.26300（x64），Intel Core i5-12400F，6 物理核 / 12 逻辑处理器，1 个处理器组，无 E-Core。
- 探针：`tools/CpuSchedulingProbe`（Release，win-x64），不加入 `HD2Mimnt.slnx`。

执行的命令：

```text
CpuSchedulingProbe topology
CpuSchedulingProbe experiment
CpuSchedulingProbe experiment --sets 261,263,265,267
CpuSchedulingProbe apply --pid <pid> --mode prefer --sets 260,262
CpuSchedulingProbe sets --pid <pid>
CpuSchedulingProbe restore --pid <pid>
CpuSchedulingProbe strict-test --seconds 8
CpuSchedulingProbe denied
```

#### 1. 拓扑与缓冲

```text
CPU: 12th Gen Intel(R) Core(TM) i5-12400F
Buffer probe: required=384 bytes, first error=122 (ERROR_INSUFFICIENT_BUFFER)
Sets: 12 total, 12 usable, 0 parked
Groups: 0 (12)
Cores: 6, logical processors: 12
EfficiencyClass: 0 x6 cores
P-Core: 0 physical / 0 logical
E-Core: 0 physical / 0 logical
Supported: False (no heterogeneous cores (all EfficiencyClass = 0))
Strict mode: unavailable (P-cores missing, split across groups, or index >= 64)
```

结论：缓冲探测与 `Size` 步进正常；本机按规则正确判定为不支持，未猜测 P/E。P/E 与多组行为仍需异构真机。

#### 2. 进程默认 CPU Sets 是否覆盖已有线程

方法：spinner 子进程先创建 4 个工作线程并采样 `GetCurrentProcessorNumberEx`；2.5 秒后由父进程设置进程默认 CPU Sets，持续 4 秒，再恢复空集合并采样 2.5 秒；应用后 2 秒额外创建一个线程，用于区分已有线程与新线程。

第一组（自动选择基线使用最少的 4 个物理核心）：

```text
Allowed sets: 256(g0/lp0/c0/e0), 258(g0/lp2/c2/e0), 265(g0/lp9/c8/e0), 266(g0/lp10/c10/e0)
Original default sets: (none)
Applied default sets: 256,258,265,266 (verify: match)
Thread-selected sets before: 0/13
Thread-selected sets after: 0/13
Applied window: 527 samples [0:0=106, 0:2=130, 0:9=141, 0:10=150]
  in allowed: 527, outside: 0
  existing threads: 469/469 in allowed
  new thread(s): 58/58 in allowed
Default sets after restore: (none) (verify: match)
Restored window: 353 samples, outside allowed: 284
Conclusion: YES - process default covers existing threads
Restore check: restore observed
```

第二组（显式 Set ID，换一组核心）：

```text
Allowed sets: 261(g0/lp5/c4/e0), 263(g0/lp7/c6/e0), 265(g0/lp9/c8/e0), 267(g0/lp11/c10/e0)
Applied window: 526 samples [0:5=146, 0:7=163, 0:9=114, 0:11=103]
  in allowed: 526, outside: 0
  existing threads: 469/469 in allowed
  new thread(s): 57/57 in allowed
Restored window: 354 samples, outside allowed: 195
Conclusion: YES - process default covers existing threads
```

结论：

- `SetProcessDefaultCpuSets` 在进程运行后设置，现有线程与新线程都跟随；模式 A 不需要逐线程补设。
- 写入后 `GetProcessDefaultCpuSets` 回查一致；恢复为空集合后回查为 0 个 ID，恢复窗口重新出现允许集合外的采样。
- 线程级选择在应用前后均为空（0/13），实验没有被线程级设置干扰。
- CPU Sets 仍是软亲和性；本实验只证明调度器跟随默认集合，不证明线程运行时间占比。

#### 3. 独立 apply / sets / restore

```text
Original default sets: (none)
Requested sets: 260,262
Applied sets: 260,262
Verify: match
Default CPU sets: 260,262
Affinity: 0xFFF, system: 0xFFF
Restored sets: (none)
Verify: match
State removed.
```

结论：状态文件按 PID/启动时间保存，恢复前比较最后应用值，恢复后删除状态；无残留。

#### 4. 严格 Affinity

```text
Original affinity: 0xFFF, system: 0xFFF
Applied affinity: 0xF (requested 0xF)
Restored affinity: 0xFFF (original 0xFFF)
Result: PASS
```

独立 `apply --mode strict` / `restore` 状态文件分支：

```text
Original affinity: 0xFFF, system: 0xFFF
Requested affinity: 0xF
Applied affinity: 0xF
Verify: match
Affinity: 0xF, system: 0xFFF
Restored affinity: 0xFFF
Verify: match
State removed.
```

结论：单组 12 逻辑处理器上，掩码设置、回查与恢复均正常，状态文件分支无残留。多组与跨组降级未在本机验证。

#### 5. 权限拒绝

```text
System (PID 4) QUERY_LIMITED: DENIED 5 (ERROR_ACCESS_DENIED)
System (PID 4) SET_LIMITED: DENIED 5 (ERROR_ACCESS_DENIED)
csrss (PID 956) QUERY_LIMITED: DENIED 5 (ERROR_ACCESS_DENIED)
...
winlogon (PID 1068) SET_LIMITED: DENIED 5 (ERROR_ACCESS_DENIED)
Denied opens: 14/14 (no setter was called)
```

结论：受保护进程的两种权限都被拒绝，错误码 5；探针只记录并退出，不重试、不调用 setter，符合兼容性失败路径。

#### 6. 本次未覆盖

- Intel P-Core + E-Core 的分类与 SMT 保留：本机无 E-Core，需 fixture + 异构真机。
- 多处理器组、跨组严格模式降级：本机单组。
- 反作弊实际拦截行为：未在受保护游戏进程上测试，只验证了 `ACCESS_DENIED` 降级路径。
- 线程实际运行时间分布：未使用 ETW，维持「配置已应用」而非「实际运行在 P-Core」的表述。

#### 决策

- 模式 A：按进程默认 CPU Sets 实现，不做逐线程设置。
- 模式 B：设置、回查、恢复与权限拒绝路径已验证；进入 Phase 3 时补跨组测试。
- UI：默认关闭；「性能核优先」标注软限制；不承诺实际分布。

## 附录 B — 拓扑 fixture 示例

```json
{
  "name": "intel-p8-e16-smt",
  "groups": [
    {
      "group": 0,
      "sets": [
        { "id": 256, "logical": 0, "core": 0, "efficiency": 2, "parked": false },
        { "id": 257, "logical": 1, "core": 0, "efficiency": 2, "parked": false },
        { "id": 512, "logical": 16, "core": 16, "efficiency": 1, "parked": false }
      ]
    }
  ],
  "expected": {
    "performanceCores": 1,
    "efficiencyCores": 1,
    "performanceLogicalProcessors": 2,
    "strictModeAvailable": true,
    "supported": true
  }
}
```

fixture 只描述拓扑输入与期望输出，不包含真实机器路径或 Set ID 规则；Set ID 仅用于模型内关联。

## 附录 C — 文件变更清单

| 阶段 | 文件 |
| --- | --- |
| Phase 0 | `tools/CpuSchedulingProbe/` |
| Phase 1 | `Core/Entities/CpuScheduling.cs`、`Core/Enums/CpuSchedulingEnums.cs`、`Core/Interfaces/ICpuTopologyProvider.cs`、`Infrastructure/Interop/CpuSetNativeApi.cs`、`Infrastructure/Scheduling/Win32CpuTopologyProvider.cs`、`tests/HD2Mimnt.Tests/Scheduling/CpuTopologyTests.cs` |
| Phase 2 | `Core/Interfaces/IProcessSchedulingController.cs`、`Infrastructure/Scheduling/IProcessSchedulingNativeApi.cs`、`Win32ProcessSchedulingNativeApi.cs`、`Win32ProcessSchedulingController.cs`、`tests/HD2Mimnt.Tests/Scheduling/Win32ProcessSchedulingControllerTests.cs` |
| Phase 3 | Affinity P/Invoke、掩码生成、跨组降级、严格模式测试 |
| Phase 4 | `Core/Interfaces/IGameProcessLocator.cs`、`IGameCpuSchedulingService.cs`、`Infrastructure/Scheduling/GameProcessLocator.cs`、`Application/Services/GameCpuSchedulingService.cs`、`UI/App.axaml.cs`、`Agent/AgentHost.cs`、`tests/HD2Mimnt.Tests/Scheduling/GameCpuSchedulingServiceTests.cs` |
| Phase 5 | `SettingsService.cs` 键、`SettingsViewModel.GameScheduling.cs`、`SettingsView.axaml`、三份 resx、`CliCommandCatalog.cs`、`CliHandlers.System.cs`、`docs/cli/README.md`、`docs/cli/AGENT-SAFETY.md` |
| Phase 6 | 兼容性测试记录、真机验收记录、问题修复 |