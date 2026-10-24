# HD2Mimnt

## Helldivers 2 专用 Mod 管理器

### 完整开发计划 / 架构规范 / 性能规范 / 开发路线

---

# 0. 项目总定义

HD2Mimnt 是：

> 一个专门针对 Helldivers 2 Mod 工作流设计的 Mod 管理、Mod Library、Profile、分析、验证、部署、清理、Task 管理、Workflow 扩展、Plugin 扩展、Mod 平台接入、外部工具协同和可选 AI 辅助平台。

核心链路：

```text
                    ┌──────────────┐
                    │  Mod Platform│
                    └──────┬───────┘
                           ↓
                      Import / Download
                           ↓
                    ┌──────────────┐
                    │  Mod Library │
                    └──────┬───────┘
                           │
                    Analysis / Validation
                           │
              ┌────────────┴────────────┐
              ↓                         ↓
          Profile A                 Profile B
              │                         │
              └────────────┬────────────┘
                           ↓
                        Workflow
                           ↓
                  Pre-Clean / Cleanup
                           ↓
                     Deployment
                           ↓
                    Helldivers 2
```

外围：

```text
Task Center
Resource Explorer
Plugin
Platforms
External Tools
AI
Logging
Diagnostics
```

---

# 1. 项目的核心原则

整个项目以后所有代码、功能、模块设计，都应该遵守以下原则。

## 1.1 HD2 专用

不为了“未来可能支持其他游戏”设计成通用 Mod Manager。

---

## 1.2 Mod Library 是核心资产层

Library 保存：

> 用户拥有、保存、管理的 Mod。

---

## 1.3 Profile 是使用配置层

Profile 保存：

> 用户当前想使用哪些 Mod，以及这些 Mod 的启用状态和顺序。

---

## 1.4 Deployment 是执行层

Deployment 负责：

> 把 Profile 中启用的 Mod 按规则实际部署到游戏。

---

## 1.5 Cleanup 是 Deployment 的配套能力

清理不是独立得完全割裂的系统。

它与 Deployment 是同一套部署管理体系：

```text
Deploy
  ↓
先清理旧的 Mod 部署
  ↓
重新部署
```

同时用户也可以单独执行：

```text
Clean Deployed Mods
```

---

## 1.6 Task Center 是所有后台工作的统一出口

凡是：

* 有持续时间
* 批量
* 后台执行
* 有进度
* 可以取消
* 有错误

都应该是 Task。

---

## 1.7 UI 永远不能因为后台任务而失效

这是一个**一级架构要求**。

不是：

> “尽量不卡”。

而是：

> **后台任务无论是慢、异常、阻塞甚至卡死，都不能拖死主 UI。**

---

## 1.8 性能不是最后才优化

以下内容从项目初期就必须考虑：

* 文件复制
* 文件扫描
* 搜索
* 图片
* 大型列表
* SQLite
* UI 虚拟化
* Task
* 大规模 Mod Library

---

# 2. 明确的项目边界

## 2.1 要做

### Mod

* Import
* Delete
* Search
* Filter
* Sort
* Category
* Tags
* Version
* Metadata
* Source
* Validation
* Analysis
* Resource View
* Deployment
* Cleanup

### Profile

* Create
* Rename
* Delete
* Duplicate
* Add Mod
* Remove Mod
* Enable
* Disable
* Reorder

### Deployment

* Copy
* Symbolic Link
* Hard Link
* Deployment Order
* Automatic Cleanup
* Progress
* Error Handling
* History

### Workflow

* Legacy
* V1
* Integrated Armor
* Future workflows

### Plugin

* Plugin SDK
* Plugin Host
* IPC
* Sandbox
* Full Access

### Platform

* Search
* Download
* Metadata
* Version
* Update

### External Tools

* Tool Registration
* Launch
* Resource → Tool

### AI

* Translation
* Analysis
* Log Explanation
* Summary
* Security Assistance

---

# 3. 明确不做

不做：

* 通用 Mod Manager
* 游戏修改器
* Cheat
* Trainer
* Anti-Cheat bypass
* 破解工具
* Server
* Mod CDN
* Mod Marketplace
* 云账号系统
* 在线插件认证平台
* Plugin Certificate
* 专业 3D 编辑器
* Photoshop
* 专业材质编辑器
* 专业动画编辑器
* 专业骨骼编辑器
* 通用 AI Agent

---

# 4. 三层最核心的数据结构

必须始终保持：

```text
Mod Library
     ↓
Profile
     ↓
Deployment
```

---

# 5. Mod Library

## 5.1 Library 的定义

Mod Library 是长期存在的本地 Mod 资产库。

例如：

```text
Mod Library

Armor A
Armor B
Weapon A
Weapon B
Character A
Test Mod
Old Mod
```

即使：

```text
Armor B
```

现在没有加入任何 Profile：

它也依然存在。

---

# 6. Profile

Profile 只是：

> 对 Library 中 Mod 的引用集合。

例如：

```text
Library

Armor A
Armor B
Weapon A
Weapon B
Character A
```

Profile：

```text
Daily

Armor A
Weapon A
```

Testing：

```text
Armor B
Weapon B
Character A
```

同一个 Mod 可以被多个 Profile 使用。

---

# 7. Library / Profile / Deployment 严格关系

必须永远保持：

```text
加入 Library
≠
加入 Profile

加入 Profile
≠
启用

启用
≠
部署

移出 Profile
≠
删除 Library

删除 Library
=
删除该 Mod 的完整本地生命周期
```

---

# 8. 特别重要：删除 Mod Library 中的 Mod

你新增的第 2 条要求必须进入核心规则。

用户执行：

```text
Delete Mod
```

实际上执行：

```text
删除 Library Mod
       ↓
解除所有 Profile 引用
       ↓
清理该 Mod 已保存的部署记录
       ↓
清理该 Mod 已部署文件
       ↓
删除该 Mod 的 Library 文件
       ↓
删除该 Mod 的 Metadata
       ↓
删除该 Mod 的 Cache
       ↓
删除完成
```

也就是说：

> **从 Mod Library 删除 Mod，就代表删除这个 Mod 在 HD2Mimnt 中的完整本地存在。**

---

# 9. Library 删除不能留下“幽灵数据”

删除后不能出现：

```text
Library 没有 Armor A
        ↓
Profile 仍然引用 Armor A
```

也不能出现：

```text
Mod A 已从 Library 删除

但：
DeploymentRecords
Cache
Thumbnail
Metadata
Profile Reference
```

还留着大量残留数据。

因此需要一个完整的：

```text
Delete Mod
```

生命周期。

---

# 10. 删除行为必须通过正式服务执行

UI 不应该：

```text
UI
 ↓
File.Delete()
```

而应该：

```text
UI
 ↓
Application
 ↓
ModService.Delete()
 ↓
Cleanup
 ↓
Library Delete
```

这样以后任何地方删除 Mod：

```text
Library
Profile
Plugin
Platform
```

都不会出现逻辑不一致。

---

# 11. Deployment 设计

Deployment 和 Cleanup 是一组核心功能。

建议 UI：

```text
[Deploy Mods]    [Clean Deployed Mods]
```

但：

```text
Deploy Mods
```

内部默认包含：

```text
Cleanup
 ↓
Deploy
```

---

# 12. Deployment 完整流程

```text
用户点击 Deploy Mods
        ↓
读取 Settings
        ↓
读取 Deployment Method
        ↓
读取当前 Profile
        ↓
获取 Enabled Mods
        ↓
读取 Deployment Order
        ↓
创建 Deployment Task
        ↓
Cleanup Previous Deployment
        ↓
检查游戏目录中已有的已管理 Mod
        ↓
清理
        ↓
开始 Deployment
        ↓
按指定顺序部署 Mod
        ↓
记录每个 Mod 的部署结果
        ↓
显示当前 Mod
        ↓
出现错误
        ↓
记录
        ↓
继续下一个 Mod
        ↓
完成
        ↓
生成 Summary
```

---

# 13. 三种 Deployment Method

部署方法有且只有三种：

```text
Copy
Symbolic Link
Hard Link
```

三者：

> **互斥。**

即一个 Deployment Task 中只能使用一种方式。

Settings：

```text
Deployment Method

○ File Copy
○ Symbolic Link
○ Hard Link
```

不能：

```text
Mod A → Copy
Mod B → Hard Link
Mod C → Symbolic Link
```

至少当前设计下不允许。

---

# 14. Copy

## 14.1 工作方式

```text
Mod Library
   ↓
文件复制
   ↓
Game Directory
```

---

## 14.2 Copy 的目标

不是普通：

```text
File.Copy()
```

然后一个一个同步执行。

而是：

> **异步化、高吞吐、尽可能减少不必要等待的文件复制系统。**

---

# 15. Copy 的高性能设计

目标：

```text
UI Thread
    ↓
Task
    ↓
Copy Scheduler
    ↓
Async File IO
    ↓
Parallel Copy Workers
```

但不能无脑：

```text
1000 个 Task
同时复制
```

需要根据：

* 文件数量
* 文件大小
* 磁盘类型
* IO 队列
* 系统情况

控制并发。

---

# 16. Copy 性能重点

应该避免：

```text
File 1
等待完全结束
File 2
等待完全结束
File 3
...
```

可以采用：

```text
Copy Queue
   ├── Worker 1
   ├── Worker 2
   ├── Worker 3
   └── Worker N
```

但是并发数量必须可控。

目标：

> 最大化吞吐量，而不是最大化线程数量。

---

# 17. Copy 中的文件分类

未来可以根据文件大小使用不同策略：

```text
Small Files
→ 并发处理

Large Files
→ 流式异步复制
```

不要让一个超大文件阻塞所有其他任务。

---

# 18. Copy 的进度

进度应该支持：

```text
Overall
████████████░░░ 75%

Mod
Armor A

Files
741 / 980

Current File
resources/...
```

---

# 19. Symbolic Link

第二种：

```text
Mod Library
     ↓
Symbolic Link
     ↓
Game Directory
```

优势：

* 几乎不需要复制文件内容
* 大量 Mod 部署非常快
* 减少磁盘写入
* 不产生大量重复文件

---

# 20. Symbolic Link 的限制

需要：

> 管理员权限。

因此程序必须：

1. 检查权限
2. 如果没有权限，明确提示
3. 不应该偷偷失败
4. Task Center 中显示错误

例如：

```text
Deployment Failed

Method: Symbolic Link

Reason:
Administrator privileges are required.
```

---

# 21. Hard Link

第三种：

```text
Mod Library
      ↓
Hard Link
      ↓
Game Directory
```

优势：

* 不需要重新复制文件数据
* 相比普通 Copy 减少磁盘写入
* 不像 Symbolic Link 那样依赖链接目标解析

---

# 22. Hard Link 的限制

必须：

> Mod Library 和游戏目标文件位于同一硬盘分区 / Volume。

因此：

```text
C:\HD2Mimnt\Mods
C:\Steam\steamapps\...
```

可以。

但：

```text
D:\HD2Mimnt\Mods
C:\Steam\steamapps\...
```

不能使用 Hard Link。

---

# 23. Hard Link 使用前检查

Deployment 前检查：

```text
Library Volume
=
Game Volume
```

否则：

```text
Hard Link unavailable
```

并明确告诉用户原因。

不是 Deployment 到一半才失败。

---

# 24. Deployment Method 设置

设置中：

```text
Deployment Method

File Copy
Symbolic Link
Hard Link
```

选择一项后：

> 所有 Mod 按同一模式部署。

---

# 25. 三种方式的行为必须统一

无论使用：

```text
Copy
Symlink
Hardlink
```

上层都只能看到统一语义：

```text
Deploy Mod
Clean Mod
```

具体实现由：

```text
Deployment Method
```

决定。

---

# 26. Deployment Method 不污染 Workflow

不要写成：

```text
LegacyCopy
LegacySymlink
LegacyHardlink
V1Copy
V1Symlink
V1Hardlink
ArmorCopy
ArmorSymlink
...
```

这会产生爆炸式组合。

应该是：

```text
Workflow
+
DeploymentMethod
```

例如：

```text
Legacy + Copy
Legacy + HardLink

V1 + Copy
V1 + SymbolicLink

IntegratedArmor + HardLink
```

---

# 27. Deployment Order

用户要求两种模式：

```text
Top → Bottom
Bottom → Top
```

只能二选一。

默认：

> **Top → Bottom**

---

# 28. 为什么默认 Top → Bottom

因为：

> 游戏读取顺序中，数字越大优先级越高。

因此：

```text
列表

Mod A
Mod B
Mod C
Mod D
```

从上到下部署：

```text
A
↓
B
↓
C
↓
D
```

最终：

```text
D = 最高优先级
```

---

# 29. Deployment Order 设置

Settings：

```text
Deployment Order

● Top to Bottom
○ Bottom to Top
```

默认：

```text
Top to Bottom
```

---

# 30. Profile 中的 Order

Profile 中保存：

```text
ProfileMod
├── ModId
├── Enabled
└── Order
```

例如：

```text
Order 0 → Armor A
Order 1 → Weapon A
Order 2 → Character A
```

UI 显示：

```text
Armor A
Weapon A
Character A
```

---

# 31. 实际 Deployment 顺序

选择：

```text
Top → Bottom
```

则：

```text
Armor A
Weapon A
Character A
```

选择：

```text
Bottom → Top
```

则：

```text
Character A
Weapon A
Armor A
```

---

# 32. 同一路径冲突时的行为

假设：

```text
Mod A
 └── abc.file

Mod B
 └── abc.file
```

并且：

```text
A
B
```

从上到下部署。

那么：

```text
A
 ↓
abc.file

B
 ↓
abc.file
```

最终：

> B 的文件生效。

也就是说：

> **Deployment Order 同时决定覆盖优先级。**

---

# 33. Cleanup

清理需要分为：

```text
Manual Cleanup
Automatic Pre-Deployment Cleanup
Library Mod Removal Cleanup
```

---

# 34. Manual Cleanup

用户点击：

```text
Clean Deployed Mods
```

执行：

```text
Create Task
   ↓
读取当前 / 已记录部署
   ↓
删除已经部署的 Mod 文件
   ↓
清理部署记录
   ↓
Summary
```

---

# 35. Automatic Cleanup

用户点击：

```text
Deploy Mods
```

自动：

```text
Cleanup
 ↓
Deploy
```

不能直接：

```text
旧 Mod 还在那里
 ↓
新 Mod 直接继续复制
```

必须先处理上一轮部署状态。

---

# 36. 为什么 Cleanup 必须在 Deploy 前

避免：

```text
旧 Profile
   ↓
旧 Mod A
旧 Mod B
旧 Mod C

切换新 Profile
   ↓
只部署 Mod D
```

最终变成：

```text
A
B
C
D
```

而用户实际上只想：

```text
D
```

因此：

```text
Deploy
=
Clean old deployment
+
Deploy current profile
```

---

# 37. Previous Deployment Cleanup

HD2Mimnt 必须记录：

```text
哪个 Mod
部署了什么
到哪里
采用什么方式
什么时候部署
```

不是为了做 Rollback。

而是为了：

> **知道以后应该清理什么。**

---

# 38. Deployment Record

例如：

```text
DeploymentRecord

SessionId
ProfileId
ModId
Workflow
DeploymentMethod
StartTime
EndTime
Status
```

对应：

```text
DeploymentFile

ModId
TargetPath
SourcePath
```

---

# 39. 这不是 Snapshot / Rollback

非常明确：

```text
DeploymentRecord
```

不是：

```text
Backup
Snapshot
Rollback System
```

它只是：

> **Mod 文件所有权 / 部署关系记录。**

用于：

```text
Cleanup
Delete Mod
Diagnostics
History
```

---

# 40. 处理“游戏目录里已经存在 Mod”

如果：

> 上一轮部署记录存在。

优先使用：

```text
DeploymentRecord
```

进行 Cleanup。

---

# 41. 处理旧版本 / 记录缺失

例如：

```text
旧版 Mod Manager 部署过文件
```

但是 HD2Mimnt 的数据库完全不知道。

这时候不能简单：

```text
把游戏目录所有奇怪文件全部删除
```

而应该：

```text
Workflow
+
Manifest
+
Known Deployment Paths
+
Mod Recognition
```

进行识别。

---

# 42. Cleanup 的安全边界

原则：

> **HD2Mimnt 只能删除自己明确识别为 Mod 部署产物的文件。**

不能因为：

```text
某文件存在于 Mod 目录
```

就直接认为：

```text
游戏里的同路径文件一定是 Mod。
```

---

# 43. 特殊的游戏原始文件问题

如果未来某个 Workflow 存在：

```text
Mod
 ↓
覆盖游戏原始文件
```

那么这个 Workflow 必须定义：

```text
原始文件如何保护
原始文件如何恢复
```

否则 Cleanup 可能误删原始游戏文件。

这属于：

> Workflow 的安全部署规则。

不是让整个 Deployment 系统自己猜。

---

# 44. Deployment Task 状态

例如：

```text
Preparing Cleanup
      ↓
Cleaning
      ↓
Preparing Deployment
      ↓
Deploying
      ↓
Completed
```

如果 Cleanup 本身有失败：

```text
Cleanup Failed
```

则：

> 不应盲目继续 Deployment。

因为旧 Mod 可能还留在游戏目录。

只有确认 Cleanup 阶段达到允许继续的状态后，才进入 Deploy。

---

# 45. Mod 单项失败

Deployment：

```text
A ✓
B ✓
C ✗
D ✓
E ✓
```

最终：

```text
Total: 5
Success: 4
Failed: 1
```

C 失败：

> 不阻止 D、E。

---

# 46. Task Center 设计升级

Task Center 是：

> **HD2Mimnt 的公共任务基础设施。**

任务包括：

```text
Import
Scan
Validation
Analysis
Deployment
Cleanup
Download
Update
Delete
Plugin
AI
Hash
Resource Analysis
```

---

# 47. Task 与 Task Group

Deployment 本身可以显示成一个 Task：

```text
Deploy Mods
```

下面内部有阶段：

```text
Cleanup
Deployment
```

而不是创建一个复杂的工作流引擎。

---

# 48. Task 数据

```text
Task
├── Id
├── Name
├── Type
├── Status
├── Progress
├── Stage
├── CurrentItem
├── Message
├── StartTime
├── EndTime
├── Errors
└── Result
```

---

# 49. Task 状态

```text
Waiting
Running
Completed
PartiallyFailed
Failed
Cancelled
ForceTerminated
```

---

# 50. 最重要：后台任务与 UI 隔离

你第 8 条是一个非常重要的设计要求。

不能只是：

```text
async
```

就认为问题解决了。

因为：

```text
async ≠ 一定不会卡死
```

例如后台出现：

* 死循环
* Native 崩溃
* 某个库死锁
* 外部进程无限等待
* 网络库异常阻塞
* 文件系统异常
* 第三方 Plugin 卡死

主程序依然可能受到影响。

---

# 51. HD2Mimnt 的进程结构

建议核心采用：

```text
┌─────────────────────────────┐
│       HD2Mimnt.exe          │
│                             │
│ UI                          │
│ Navigation                  │
│ ViewModel                   │
│ Task Center UI              │
│ Application Orchestration   │
└──────────────┬──────────────┘
               │ IPC
               ↓
┌─────────────────────────────┐
│      HD2Mimnt.Worker.exe    │
│                             │
│ Background Tasks             │
│ Import                       │
│ Scan                         │
│ Analysis                     │
│ Cleanup                      │
│ Deployment                   │
│ Heavy IO                     │
└─────────────────────────────┘
```

这样：

> UI 和后台执行环境不是一个生命体。

---

# 52. 为什么这里值得引入 Worker

这不是为了“架构看起来高级”。

而是直接服务于你的硬要求：

> **后台任务卡死，主程序和 UI 仍然必须可以使用。**

---

# 53. Worker 崩溃

如果：

```text
HD2Mimnt.Worker.exe
```

崩溃：

主程序：

```text
HD2Mimnt.exe
```

继续运行。

Task Center：

```text
Deployment
✗ Worker terminated unexpectedly
```

而不是：

```text
整个程序一起崩
```

---

# 54. Force Terminate

如果：

```text
Task
 ↓
卡死
```

可以：

```text
Cancel
```

如果无效：

```text
Force Terminate
```

主程序：

```text
继续运行
```

Worker：

```text
被终止
```

然后：

```text
Task = ForceTerminated
```

---

# 55. Task 隔离原则

不是：

```text
杀整个 HD2Mimnt
```

而是：

```text
结束对应 Worker / Execution Context
```

这样：

```text
Task A 卡死
```

不会影响：

```text
Task Center
Mod Library
Settings
Profile
Search
UI
```

---

# 56. Worker 里的任务也不能互相拖死

不能：

```text
Task A
   ↓
死锁
   ↓
Worker 整体死锁
   ↓
Task B / C 全没了
```

因此 Task 执行器仍然需要：

* CancellationToken
* Timeout
* 独立执行上下文
* 明确资源释放
* 错误捕获

---

# 57. UI Thread 禁止做的事情

绝对禁止：

```text
UI Thread
 ↓
大规模文件扫描
```

禁止：

```text
UI Thread
 ↓
大量文件复制
```

禁止：

```text
UI Thread
 ↓
大批量 Image Decode
```

禁止：

```text
UI Thread
 ↓
大量数据库查询
```

禁止：

```text
UI Thread
 ↓
同步等待 Task
```

尤其禁止：

```text
.Result
.Wait()
Thread.Sleep()
```

这种方式阻塞 UI。

---

# 58. UI 永远不能等待后台操作

错误：

```text
点击按钮
 ↓
await
 ↓
UI 等待逻辑
```

虽然 `await` 本身没问题，但不能把整个 ViewModel 操作链设计成：

```text
UI Command
 ↓
等待一个很长的业务操作
 ↓
期间整个功能不可用
```

正确：

```text
UI Command
 ↓
创建 Task
 ↓
立即返回
 ↓
Task Center 订阅进度
```

---

# 59. UI 应该始终保持导航能力

例如正在：

```text
Deployment 72%
```

用户仍然可以：

```text
打开 Mod Library
打开 Settings
搜索 Mod
查看 Profile
查看日志
查看 Task
```

不能出现：

```text
正在部署
↓
整个 UI 灰掉
```

---

# 60. 需要禁用的按钮要精确到功能

例如：

```text
当前正在删除 Armor A
```

可以暂时禁用：

```text
Delete Armor A
```

但不能把整个 UI：

```text
全部 Disabled
```

---

# 61. SQLite 与 UI 可用性

数据库也不能成为单点卡死位置。

建议：

```text
SQLite WAL
```

并且：

* 短事务
* 批量写入
* 避免长时间持锁
* UI 查询与后台写入尽量不互相阻塞
* 数据库操作有超时
* 查询失败必须返回错误而不是无限等待

---

# 62. Mod Library 的性能要求

你提出的搜索要求非常明确：

> **搜索几乎要达到毫秒级反馈。**

目标：

```text
输入关键词
 ↓
几毫秒级
 ↓
显示结果
```

这个目标主要针对：

> 已经建立本地索引后的交互式搜索。

不应该每次搜索都：

```text
扫描整个硬盘
```

---

# 63. 搜索系统不能只依赖数据库全表 LIKE

不要：

```sql
SELECT *
FROM Mods
WHERE Name LIKE '%abc%'
```

每按一个字就扫描大量数据。

数据库可以提供底层查询，但交互搜索应该有专门的索引。

---

# 64. 搜索类型

至少支持三种：

```text
精准搜索
模糊搜索
标签搜索
```

---

# 65. 精准搜索

例如：

```text
Armor A
```

应该直接命中：

```text
Armor A
```

应该尽量走：

```text
HashMap / Dictionary
```

或者：

```text
Indexed Key
```

而不是全表扫描。

---

# 66. 模糊搜索

例如：

```text
armr
```

可以匹配：

```text
Armor A
Armor B
Armored Character
```

具体匹配可考虑：

```text
Normalized Name
Prefix Index
Token Index
N-gram / Trigram Candidate Index
```

最终只对候选项进行评分。

---

# 67. 搜索必须“先缩小候选，再排序”

错误：

```text
10,000 Mods
 ↓
逐个模糊计算
 ↓
排序全部
```

更合理：

```text
Query
 ↓
Index
 ↓
Candidate Set
 ↓
Fuzzy Score
 ↓
Top Results
```

这样才能接近毫秒级。

---

# 68. 搜索结果排序

可以优先：

```text
Exact
↓
Prefix
↓
Token
↓
Fuzzy
```

例如：

```text
Armor A
Armor
Armor C
Armored Character
```

---

# 69. 标签搜索

例如：

```text
tag:armor
```

或者：

```text
tag:anime
```

必须直接使用标签索引。

不应该：

```text
读取所有 Mod
 ↓
逐个解析 JSON
 ↓
重新判断 Tag
```

---

# 70. 标签组合

以后可以支持：

```text
tag:armor tag:physics
```

变成：

```text
Armor
AND
Physics
```

或：

```text
tag:armor OR tag:weapon
```

是否做复杂查询语法，可以以后增加。

第一版先保证：

> 标签过滤速度极快。

---

# 71. 搜索索引的生命周期

Library 数据发生变化：

```text
Import
Delete
Rename
Tag Change
Version Update
```

只更新受影响的索引。

不要：

```text
修改一个 Mod
 ↓
重建 10,000 个 Mod 的全部索引
```

---

# 72. Search Index 建议

可以采用：

```text
Library Database
      ↓
Search Index
```

内存中维护：

```text
Exact Index
Prefix Index
Tag Index
Fuzzy Candidate Index
```

对于 10,000 级 Mod Library：

> 这种规模非常适合内存索引。

---

# 73. 搜索性能目标

建议定义 benchmark：

```text
10,000 Mods
```

测试：

```text
Exact Search
Prefix Search
Fuzzy Search
Tag Search
Combined Search
```

目标：

> 常规交互搜索结果应达到毫秒级反馈。

不要把“毫秒级”写成绝对保证值，而是作为 Benchmark 目标。

---

# 74. Mod Library 图片问题

这一点也应该直接进入架构，而不是：

> “以后再优化”。

因为你的 Mod Library 图片数量会非常大。

例如：

```text
10,000 Mods
```

可能就是：

```text
10,000+ thumbnails
20,000+
50,000+
```

如果处理方式错误：

```text
一次加载所有原图
```

内存会直接爆。

---

# 75. 图片绝对不能这样设计

禁止：

```text
打开 Mod Library
 ↓
一次加载所有图片原图
 ↓
全部放进内存
```

---

# 76. 图片应该分为三个层级

```text
Original Image
       ↓
Thumbnail
       ↓
UI Display
```

---

# 77. Thumbnail Cache

每个 Mod 图片生成：

```text
Thumbnail
```

例如：

```text
128×128
256×256
```

实际尺寸根据 UI 决定。

---

# 78. Disk Cache

缩略图应该有独立缓存：

```text
Cache
└── Thumbnails
    ├── hash_a
    ├── hash_b
    ├── hash_c
    └── ...
```

使用：

```text
Image Content Hash
+
Thumbnail Size
+
Decoder Version
```

作为 Cache Key。

这样：

> 同一图片不需要每次重新解码。

---

# 79. 内存图片缓存

使用：

```text
LRU Cache
```

只保存当前近期访问图片。

例如：

```text
用户当前看到的几十张
+
附近的一小部分
```

而不是：

```text
10,000 张全部常驻内存
```

---

# 80. 图片加载应该按可视区域进行

例如：

```text
当前屏幕只显示：

1
2
3
4
5
6
7
8
```

那么优先加载：

```text
1~8
```

其次：

```text
9~15
```

而不是一口气：

```text
1~10,000
```

---

# 81. 图片任务优先级

图片加载可以有优先级：

```text
Visible
   ↓
Near Visible
   ↓
Far
```

这样快速滚动时：

> 不会浪费资源加载用户已经滚过去的图片。

---

# 82. 图片解码不得发生在 UI Thread

必须：

```text
Image Request
 ↓
Background Decoder
 ↓
Thumbnail
 ↓
UI
```

UI 只接收：

```text
ImageSource
```

或者轻量引用。

---

# 83. 图片加载必须支持取消

用户快速滚动：

```text
Armor A
Armor B
Armor C
Armor D
```

如果 A、B 已经滚走：

相关未完成 Decode 应允许：

```text
Cancel
```

避免后台继续浪费 CPU。

---

# 84. 图片列表必须使用 Virtualization

例如：

```text
10,000 Mods
```

UI 实际创建：

```text
几十个可视元素
```

而不是：

```text
10,000 Controls
```

---

# 85. Mod Library UI

推荐类似：

```text
┌────────────────────────────────────────────┐
│ 搜索...                         [筛选]     │
├────────────────────────────────────────────┤
│                                            │
│  ┌────────┐  ┌────────┐  ┌────────┐      │
│  │  Image │  │  Image │  │  Image │      │
│  │ Armor A│  │ Armor B│  │ Weapon A      │
│  └────────┘  └────────┘  └────────┘      │
│                                            │
│  ┌────────┐  ┌────────┐  ┌────────┐      │
│  │  Image │  │  Image │  │  Image │      │
│  │ Mod D  │  │ Mod E  │  │ Mod F  │      │
│  └────────┘  └────────┘  └────────┘      │
│                                            │
└────────────────────────────────────────────┘
```

但内部必须是：

> Virtualized ItemsControl / DataGrid / ListView 等适合大数据量的虚拟化方案。

---

# 86. Mod Library 页面不能因为图片而卡

必须做到：

```text
进入页面
 ↓
文字数据先出现
 ↓
缩略图逐渐加载
```

而不能：

```text
等所有图片加载完
 ↓
页面才显示
```

---

# 87. 图片坏了也不能影响 Library

如果：

```text
Mod A thumbnail
```

损坏：

应该：

```text
显示默认占位
```

而不是：

```text
整个列表报错
```

---

# 88. 图片缓存清理

以后设置里可以有：

```text
Clear Thumbnail Cache
```

但不是第一阶段就必须做。

Cache 可以根据：

```text
Size
Age
```

自动维护。

---

# 89. Mod Import 性能

Import 也必须 Task 化：

```text
Import Mod
 ↓
Task
 ↓
Detect
 ↓
Scan
 ↓
Manifest
 ↓
Hash
 ↓
Thumbnail
 ↓
Analysis
 ↓
Add to Library
```

但不同步骤可以按实际需要执行。

不要为了导入一个 Mod 就强制做几十秒全量分析。

---

# 90. Import 应支持分阶段

例如：

```text
Stage 1
Identify

Stage 2
Basic Scan

Stage 3
Metadata

Stage 4
Thumbnail

Stage 5
Optional Deep Analysis
```

这样：

> Mod 可以尽快进入 Library。

深度分析可以在后台继续。

---

# 91. Library 的 Mod 状态

可以显示：

```text
Ready
Scanning
Analyzing
Invalid
Missing Files
```

但是：

> 不要因为某个 Mod 正在分析，就阻塞整个 Library。

---

# 92. 大型 Library 的 UI 原则

10,000+ Mod 时：

必须：

```text
Virtualization
Incremental Search
Lazy Image Loading
Incremental Refresh
Async Loading
```

避免：

```text
Reset Entire Collection
```

导致整个 UI 重建。

---

# 93. Profile 也必须高性能

Profile 可能只有几十个 Mod，但不能因此写得很差。

拖动排序：

```text
不要重建整个列表
```

只更新：

```text
Order
```

---

# 94. Profile 顺序变化

例如：

```text
A
B
C
D
```

拖动：

```text
D → 第一位
```

只更新必要 Order。

不要：

```text
删除全部
重新插入全部
```

---

# 95. Deployment 的顺序计算

部署时：

```text
Profile Mods
      ↓
Filter Enabled
      ↓
Sort by Order
      ↓
Reverse if needed
      ↓
Deployment
```

这是非常明确的流程。

---

# 96. Cleanup 与 Profile 的关系

Cleanup 不能只清理当前 Profile。

例如：

```text
昨天 Profile A
今天 Profile B
```

如果昨天部署了：

```text
A
B
C
```

今天使用：

```text
D
E
```

点击 Deploy：

应该清理：

```text
A
B
C
```

然后部署：

```text
D
E
```

因此 Cleanup 面向：

> **实际已经部署到游戏的 Mod。**

不是只看当前 Profile。

---

# 97. Deployment History

Task Center 中保留：

```text
Deploy Profile: Daily
Completed
```

可以看到：

```text
Time
Profile
Method
Mods
Success
Failed
```

但不做：

```text
完整 Snapshot
Rollback
```

---

# 98. Mod 删除与 Deployment Record

用户删除：

```text
Armor A
```

必须先：

```text
查找 Armor A 的 DeploymentRecord
```

如果已部署：

```text
Clean deployed files
```

然后：

```text
Delete record
```

再：

```text
Delete Library
```

---

# 99. Delete Task

由于删除 Mod 可能包含：

* 多个文件
* 多个 Profile 引用
* 部署文件
* 图片缓存
* Metadata

所以：

```text
Delete Mod
```

也必须是：

```text
Task
```

不能让 UI 卡在那里。

---

# 100. Workflow 系统

Workflow 负责：

> “这个 Mod 应该如何识别、验证、分析、部署。”

初期：

```text
Legacy
V1
```

后期：

```text
Integrated Armor
```

---

# 101. Workflow 不应该包含 Deployment Method

正确：

```text
Workflow
+
DeploymentMethod
+
DeploymentOrder
```

而不是：

```text
LegacyCopy
LegacyHardlink
V1Copy
V1Hardlink
...
```

---

# 102. Legacy

实现：

* Detection
* Manifest
* Validation
* File Structure
* Deployment
* Cleanup

---

# 103. V1

实现：

* Detection
* Manifest
* `.patch_0`
* `.gpu_resources`
* `.stream`
* Validation
* Deployment
* Cleanup

---

# 104. Integrated Armor

作为独立大型 Workflow。

包括：

```text
Custom Skeleton
Physics
Body Variant
Head Variant
Armor Illusion
Anti Dismemberment
Lua-ReShade
Batch
SDK
```

---

# 105. ArmorSelection

不能把所有东西揉成一个 Armor。

应该：

```text
ArmorSelection
├── GameplayArmor
├── AppearanceArmor
├── BodyVariant
└── HeadVariant
```

这样支持：

```text
Gameplay = Armor A
Appearance = Armor B
Body = 3
Head = 2
```

---

# 106. Resource Explorer

提供：

```text
Mod
 ↓
Resource Explorer
```

可以：

* 文件树
* 文件信息
* Resource Metadata
* Hash
* 引用关系
* 依赖关系
* Preview

不负责专业编辑。

---

# 107. External Tools

例如：

```text
Resource
 ↓
Open With
 ↓
Blender
```

HD2Mimnt 负责：

* 工具注册
* 路径
* 启动
* 参数
* 文件关联

---

# 108. Mod Platform

Platform 采用 Adapter：

```text
IModPlatform
      │
 ┌────┼─────┐
 ↓    ↓     ↓
P-A  P-B   P-C
```

能力：

```text
Search
Download
Metadata
Version
Update
Files
```

平台缺什么能力：

> UI 就不要显示对应功能。

---

# 109. Plugin

第三方 Plugin 不能访问：

```text
内部 DB Schema
Core 私有类
私有 UI
内部文件结构
```

只能使用：

```text
Public Plugin SDK
```

---

# 110. Plugin Host

```text
HD2Mimnt
    ↕
HD2PluginHost.exe
    ↓
Plugin
```

目的：

> Plugin 崩了，主程序别跟着一起去世。

---

# 111. Plugin 两种模式

```text
Sandbox
Full Access
```

Sandbox 默认。

Full Access 必须用户明确允许。

---

# 112. Plugin Manifest

至少：

```text
Id
Name
Version
Author
ApiVersion
Runtime
PermissionMode
RequestedPermissions
```

不做数字签名和在线认证。

---

# 113. AI

AI 是可选外挂。

Core：

```text
没有 AI
```

也必须：

> 完整运行。

---

# 114. AI Architecture

```text
Analyzer
    ↓
Structured AnalysisContext
    ↓
AI Provider
    ↓
Explanation
```

不要：

```text
AI
 ↓
Core Tools
 ↓
自动操作
```

---

# 115. AI 功能

支持：

* 翻译
* README 总结
* Changelog 总结
* Mod 分析解释
* 冲突解释
* 日志解释
* 安全辅助检查

不支持自动：

* 安装
* 删除
* 部署
* 修改游戏
* 执行脚本

---

# 116. 数据库

初始：

```text
Mods
Profiles
ProfileMods
Settings
Tasks
TaskHistory
DeploymentRecords
DeploymentFiles
```

后续：

```text
Resources
Dependencies
Platforms
Plugins
AI Config
ExternalTools
```

---

# 117. DeploymentFiles 表

这一张表对于新需求非常重要。

例如：

```text
DeploymentFiles

Id
DeploymentId
ModId
SourcePath
TargetPath
Method
```

用途：

```text
Cleanup
Delete Mod
Diagnostics
Deployment History
```

---

# 118. 为什么需要 DeploymentFiles

因为 HD2Mimnt 不能靠猜：

```text
游戏目录里这个文件是不是 Mod？
```

而应该有：

```text
HD2Mimnt 明确知道：

Mod A
→
部署了
→
target/file/abc
```

这样以后：

```text
Clean
```

可以精准删除。

---

# 119. DeploymentFiles 的性能

如果一个 Mod 有：

```text
100,000 files
```

需要：

> 批量事务写入。

不要：

```text
100,000 次单独 COMMIT
```

应该：

```text
Begin Transaction
 ↓
Batch Insert
 ↓
Commit
```

---

# 120. 删除大量 DeploymentFiles

同样使用：

```text
Batch Delete
```

而不是逐条提交。

---

# 121. Hash

Hash 用于：

* 文件识别
* Mod 识别
* Cache
* Thumbnail Cache
* 变化检测

未来真正耗时的大批量 Hash 可以：

```text
Worker
```

甚至必要时再使用：

```text
Zig Native
```

---

# 122. Native / Zig

C#：

> 主体。

Zig：

> 真有性能瓶颈再使用。

例如：

```text
C#
 ↓
HD2Native.dll
 ↓
Zig
```

适合：

* Fast Scan
* Hash
* Binary Parsing
* Resource Analysis
* Patch Processing

不要一开始把整个项目拆成：

```text
C#
+
Zig
+
各种 IPC
```

否则自己先把自己折腾死。

---

# 123. UI 架构

主界面：

```text
配置文件
模组库
设置
更多
```

Global：

```text
Task Center
```

---

# 124. UI 层职责

只负责：

* View
* ViewModel
* Navigation
* User Input
* Display
* Binding

不直接：

* File IO
* SQLite
* Deployment
* Plugin
* Platform
* Worker 内部逻辑

---

# 125. Application 层

负责：

```text
Command
 ↓
Service
 ↓
Create Task
 ↓
Return Immediately
```

例如：

```text
DeployProfileCommand
DeleteModCommand
ImportModCommand
CleanDeploymentCommand
```

---

# 126. Core

负责：

* Mod
* Profile
* Version
* Validation
* Dependencies
* Conflicts
* Workflow abstraction
* Deployment semantics
* Task model

不依赖：

* UI
* AI
* 具体 Platform
* Plugin UI

---

# 127. Infrastructure

负责：

* SQLite
* File System
* Logging
* Configuration
* Process
* Cache
* IPC

---

# 128. 图片缓存 Infrastructure

单独成为基础能力：

```text
ImageService
 ├── Thumbnail Generator
 ├── Disk Cache
 ├── Memory Cache
 ├── Decoder
 └── Request Scheduler
```

因为图片数量巨大：

> 不能把它当作一个普通 `ImageSource` 属性就完事。

---

# 129. Logging

记录：

```text
Import
Delete
Cleanup
Deployment
Workflow
Plugin
Platform
AI
Worker
External Process
```

错误必须包含：

```text
Task
Operation
Mod
File
Exception
Message
```

---

# 130. 测试体系

测试从开发第一阶段就开始。

---

# 131. Unit Test

测试：

```text
Mod
Profile
Manifest
Version
Dependency
Conflict
Task
Deployment Order
Deployment Method
Validation
```

尤其测试：

```text
Top → Bottom
Bottom → Top
```

以及：

```text
Enabled / Disabled
```

---

# 132. Deployment Test

必须测试：

```text
Copy
Symlink
Hardlink
```

分别测试：

* 成功
* 文件不存在
* 权限不足
* 路径冲突
* Disk Error
* Volume 不同
* Link 创建失败

---

# 133. Cleanup Test

测试：

```text
Clean previous deployment
Clean manually
Clean after failed deployment
Clean after Profile change
Clean after Mod deletion
```

---

# 134. Delete Mod Test

模拟：

```text
Mod A
 ↓
Library
 ↓
Profile A
Profile B
 ↓
Game deployment
```

执行：

```text
Delete Mod A
```

必须得到：

```text
Library 删除
Profile A 删除引用
Profile B 删除引用
Deployment 删除
Game 文件清理
Cache 删除
```

---

# 135. Deployment Recovery Test

例如：

```text
Cleanup
 ↓
成功
 ↓
Deployment
 ↓
第 5 个 Mod 崩溃
```

检查：

```text
Task = PartiallyFailed
```

并且：

```text
第 6、7、8 个 Mod
```

继续部署。

---

# 136. Worker Test

测试：

```text
Worker 正常
Worker Timeout
Worker Crash
Worker Hang
Worker Force Terminate
```

最重要：

> Worker 挂了，主 UI 仍然正常操作。

---

# 137. UI Stability Test

模拟：

```text
10,000 Mods
+
10,000 Thumbnails
+
Deployment Task
+
Background Scan
```

同时用户：

```text
搜索
切换页面
打开 Settings
修改 Profile
打开 Task Center
```

必须保持：

> UI 可交互。

---

# 138. Search Benchmark

测试：

```text
1K Mods
5K Mods
10K Mods
```

分别：

```text
Exact
Prefix
Fuzzy
Tag
Combined
```

记录：

```text
Latency
Result Count
CPU
Memory
```

目标：

> 普通搜索交互达到毫秒级反馈。

---

# 139. Image Benchmark

测试：

```text
1,000 thumbnails
5,000 thumbnails
10,000 thumbnails
50,000 thumbnails
```

记录：

```text
Startup
First Paint
Scroll FPS
Memory
Decode CPU
Cache Hit Rate
```

---

# 140. Library Benchmark

测试：

```text
Import
Delete
Search
Sort
Filter
Scroll
Tag
Open Details
```

尤其：

```text
10,000+
```

---

# 141. Deployment Benchmark

测试：

```text
100 files
1,000 files
10,000 files
100,000 files
```

每一种：

```text
Copy
Symbolic Link
Hard Link
```

记录：

```text
Duration
CPU
RAM
Disk IO
```

---

# 142. Copy Benchmark

特别测试：

```text
大量小文件
少量超大文件
混合文件
```

因为：

> 小文件和大文件的性能瓶颈完全不同。

---

# 143. Hard Link Benchmark

测试：

```text
Same Volume
Different Volume
```

并确保：

```text
Different Volume
```

能快速在 Deployment 开始前给出明确错误。

---

# 144. Symbolic Link Benchmark

测试：

```text
Admin
No Admin
```

无权限时：

> 不应该部署到一半才失败。

---

# 145. 长时间测试

运行：

```text
1 Hour
4 Hours
8 Hours
```

期间持续：

```text
Search
Task
Image Loading
Import
Background Scan
```

检查：

* Memory Leak
* Handle Leak
* Thread Leak
* UI Lag
* SQLite Lock
* Cache Leak

---

# 146. Release Gate

发布必须通过：

```text
Build
 ↓
Unit
 ↓
Integration
 ↓
Workflow
 ↓
Deployment
 ↓
Cleanup
 ↓
Plugin
 ↓
Platform
 ↓
AI
 ↓
Performance
 ↓
Stress
 ↓
Regression
 ↓
Release
```

---

# 147. 开发阶段重新排序

由于现在新增了：

* Cleanup
* 三种 Deployment Method
* Deployment Order
* Image System
* 高性能 Search
* Worker Isolation

开发阶段建议调整成下面这样。

---

# Phase 0

## 项目边界冻结

确定：

```text
Mod
Profile
Deployment
Cleanup
Task
Workflow
Worker
Library Search
Image Cache
```

这些是基础。

---

# Phase 1

## 项目骨架

建立：

```text
Avalonia.UI
.NET 10
SQLite
MVVM
```

完成：

```text
MainWindow
Navigation
Settings
Library
Profile
Task Center
```

---

# Phase 2

## Mod Library

完成：

```text
Import
Delete
Metadata
Category
Tags
Version
Source
```

同时：

```text
Thumbnail
Virtualization
Search
```

一并建立基础版本。

---

# Phase 3

## Search Engine

建立：

```text
Exact Search
Prefix Search
Fuzzy Search
Tag Search
```

建立 benchmark。

这个阶段结束后：

> 10,000 Mod 搜索必须已经接近目标体验。

---

# Phase 4

## Image System

完成：

```text
Thumbnail
Disk Cache
Memory LRU
Virtualization
Async Decode
Cancellation
Visible Priority
```

目标：

> 10,000+ 图片仍然可以流畅滚动。

---

# Phase 5

## Profile

完成：

```text
Create
Delete
Duplicate
Add
Remove
Enable
Disable
Reorder
```

---

# Phase 6

## Task Center + Worker

完成：

```text
Task
Worker
IPC
Progress
Cancel
Force Terminate
History
Error
```

这一阶段结束后：

> 后面的所有重型操作都必须使用这套机制。

---

# Phase 7

## Deployment Engine

先只实现统一 Deployment 抽象：

```text
Deploy
Cleanup
Order
Tracking
```

---

# Phase 8

## Copy Deployment

完成：

```text
Async Copy
Parallel IO
Progress
Failure Handling
DeploymentFiles
Cleanup
```

---

# Phase 9

## Symbolic Link

完成：

```text
Permission Check
Symlink
Cleanup
Error
```

---

# Phase 10

## Hard Link

完成：

```text
Volume Check
Hard Link
Cleanup
Error
```

---

# Phase 11

## Deployment Order

完成：

```text
Top → Bottom
Bottom → Top
```

默认：

```text
Top → Bottom
```

同时测试：

```text
Same Target Path
Overwrite Priority
```

---

# Phase 12

## Legacy Workflow

---

# Phase 13

## V1 Workflow

---

# Phase 14

## Analysis / Resource Explorer

---

# Phase 15

## Integrated Armor

分阶段：

```text
Basic Workflow
SDK
Skeleton
Physics
Variants
Illusion
Lua-ReShade
Batch
Anti-Dismemberment
```

---

# Phase 16

## Platform

---

# Phase 17

## Plugin

---

# Phase 18

## External Tools

---

# Phase 19

## AI

---

# Phase 20

## 全面性能测试

重点：

```text
Search
Image
Library
Deployment
Worker
SQLite
```

---

# Phase 21

## 长时间稳定性 + Regression

---

# Phase 22

## Release

---

# 148. 推荐的最终程序结构

最终可以逐步形成：

```text
HD2Mimnt
│
├── HD2Mimnt.UI
│
├── HD2Mimnt.Application
│
├── HD2Mimnt.Core
│
├── HD2Mimnt.Infrastructure
│
├── HD2Mimnt.Worker
│
├── HD2Mimnt.Workflows
│   ├── Legacy
│   ├── V1
│   └── IntegratedArmor
│
├── HD2Mimnt.Platforms
│
├── HD2Mimnt.PluginSDK
│
├── HD2Mimnt.PluginHost
│
├── HD2Mimnt.ExternalTools
│
├── HD2Mimnt.AI
│
└── HD2Mimnt.Native
```

但必须强调：

> 不要第一天就建立全部项目。

随着功能进入对应阶段再拆。

---

# 149. 最终模块依赖方向

```text
UI
 ↓
Application
 ↓
Core
```

Worker：

```text
Application
 ↓
Worker
```

Workflow：

```text
Application
 ↓
Workflow
 ↓
Core
```

Platform：

```text
Application
 ↓
Platform Adapter
```

Plugin：

```text
Plugin
 ↓
Public SDK
```

AI：

```text
AnalysisContext
 ↓
AI
```

Native：

```text
Service
 ↓
Native Abstraction
 ↓
Zig
```

---

# 150. 最终 Deployment 模型

最终可以把 Deployment 理解成：

```text
                    Current Profile
                          │
                          ↓
                    Enabled Mods
                          │
                          ↓
                  Sort by Profile Order
                          │
                ┌─────────┴─────────┐
                │                   │
        Top → Bottom          Bottom → Top
                │                   │
                └─────────┬─────────┘
                          ↓
                 Pre-Deployment Cleanup
                          ↓
                    Deployment Method
                  ┌────────┼────────┐
                  ↓        ↓        ↓
                Copy    Symlink   Hardlink
                  │        │        │
                  └────────┼────────┘
                           ↓
                     Deploy Mod
                           ↓
                  Record DeploymentFiles
                           ↓
                     Next Mod
                           ↓
                        Summary
```

---

# 151. 最终 Cleanup 模型

```text
Manual Cleanup
       │
       ↓
Deployment Records
       ↓
Find Target Files
       ↓
Remove Managed Mod Files
       ↓
Remove Deployment Records
       ↓
Summary
```

Library Delete：

```text
Delete Mod
    ↓
Remove from Profiles
    ↓
Cleanup Deployments
    ↓
Remove Deployment Records
    ↓
Remove Cache
    ↓
Remove Library Mod
```

---

# 152. 最终 Task 模型

```text
Main UI
   │
   ↓
Create Task
   │
   ↓
Worker
   │
   ├── Progress
   ├── Message
   ├── Current Item
   ├── Error
   └── Result
   │
   ↓
Task Center
```

异常：

```text
Worker Hang
    ↓
Cancel
    ↓
Still Hang
    ↓
Force Terminate Worker
    ↓
Task = ForceTerminated
    ↓
Main UI continues
```

---

# 153. 最终 Search 模型

```text
Library
   ↓
Index
 ┌───────────────┬───────────────┬──────────────┐
 ↓               ↓               ↓
Exact           Prefix          Tag
 ↓               ↓               ↓
         Candidate Set
                ↓
            Fuzzy Score
                ↓
              Result
```

目标：

```text
10,000 Mods
↓
Search
↓
Milliseconds
```

---

# 154. 最终 Image 模型

```text
Original
   ↓
Thumbnail Generator
   ↓
Disk Cache
   ↓
Memory LRU
   ↓
Virtualized UI
```

同时：

```text
Visible Item
   ↓
High Priority

Near Visible
   ↓
Medium Priority

Far
   ↓
Low Priority
```

---

# 155. 整个项目最终可以浓缩为六层

### 第一层：资产

```text
Mod Library
```

### 第二层：配置

```text
Profile
```

### 第三层：规则

```text
Workflow
Deployment Method
Deployment Order
```

### 第四层：执行

```text
Task
Worker
Deployment
Cleanup
```

### 第五层：扩展

```text
Platform
Plugin
External Tools
AI
```

### 第六层：支撑

```text
SQLite
Cache
Logging
Search Index
Image System
Native
```

---

# 156. 最终 MVP

真正第一个可以使用的版本，不需要一次完成 Integrated Armor / Plugin / AI。

第一版应该做到：

```text
启动
 ↓
创建 Profile
 ↓
Import Mod
 ↓
Mod 进入 Library
 ↓
图片显示
 ↓
搜索
 ↓
加入 Profile
 ↓
Enable / Disable
 ↓
调整顺序
 ↓
选择 Deployment Method
 ↓
Deploy
 ↓
自动 Cleanup 旧 Mod
 ↓
部署当前 Mod
 ↓
Task Center 显示进度
 ↓
错误继续处理
 ↓
完成
```

同时：

```text
Clean Deployed Mods
```

必须已经可以单独工作。

---

# 157. 第一版完成标准

当以下链路稳定后：

```text
Library
    ↓
Profile
    ↓
Cleanup
    ↓
Deployment
    ↓
Task Center
```

HD2Mimnt 的“骨架”就已经完成。

尤其必须同时满足：

```text
10,000 Mods
+
大量图片
+
快速搜索
+
后台部署
+
UI 始终可用
```

---

# 158. HD2Mimnt 最终产品哲学

它不是：

> “我把所有工具都做进一个程序。”

而是：

> “我把 Helldivers 2 Mod 工作流里最麻烦的管理、识别、配置、清理、部署、分析和工具协同做好。”

因此：

```text
Mod Library
负责资产

Profile
负责选择

Workflow
负责理解 Mod 工作方式

Deployment
负责部署

Cleanup
负责清理

Task Center
负责透明管理后台工作

Worker
负责隔离重型任务

Search
负责瞬时查找

Image System
负责海量缩略图

Plugin
负责扩展

Platform
负责内容来源

External Tools
负责专业编辑

AI
负责辅助理解
```

最终边界：

> **HD2Mimnt 管理 Mod，而不是替所有 Mod 工具活一遍。**

---

# 159. AI 开发 HD2Mimnt 时的最终硬规则

以后每次让 AI 写代码，都应该拿下面这套规则检查一次。

```text
1. 这是 HD2 专用功能还是无意义通用化？

2. 这个模块真的有存在必要吗？

3. 谁调用它？

4. 它负责什么？

5. 它不负责什么？

6. UI 有没有直接碰数据库？

7. UI 有没有直接碰文件系统？

8. UI 有没有同步等待后台操作？

9. 长任务有没有 Task？

10. Task 有没有取消？

11. Task 卡死能不能处理？

12. Worker 挂掉后 UI 还能不能用？

13. 一个 Mod 失败有没有阻止整个批处理？

14. Library / Profile / Deployment 有没有混在一起？

15. Delete Mod 有没有把相关部署和配置一起清掉？

16. Deployment Method 是否保持 Copy / Symlink / Hardlink 三选一？

17. Deployment Order 是否明确？

18. 搜索有没有走索引？

19. 图片有没有虚拟化和缓存？

20. 10,000 项数据时 UI 是否依然可用？

21. 有没有为了“未来”创建没有真实调用者的接口？

22. 有没有为了性能过早使用 Native / Zig？

23. 有没有把简单问题做成复杂状态机？

24. AI 是否仍然只是辅助？

25. Plugin 是否依赖内部实现？

26. Platform 是否污染 Core？

27. 是否增加了本来不应该由 HD2Mimnt 负责的功能？
```

---

# 160. 一句话总架构

最终 HD2Mimnt 的核心应该永远是：

```text
              Mod Library
                   ↓
                Profile
                   ↓
          Workflow + Order
                   ↓
        Pre-Cleanup / Cleanup
                   ↓
         Deployment Method
       ┌───────────┼───────────┐
       ↓           ↓           ↓
     Copy       Symlink     Hardlink
       └───────────┼───────────┘
                   ↓
               Deployment
                   ↓
              Helldivers 2
```

旁边贯穿始终：

```text
Task Center
    ↓
Worker
    ↓
保证后台工作永远不拖死 UI
```

而性能底座：

```text
Search Index
Image Cache
Virtualization
Async IO
SQLite WAL
```

这样整个 HD2Mimnt 才会形成一个非常完整、而且以后不容易失控的结构。