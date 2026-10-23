# HD2Mimnt 代码质量 / 安全 / 项目结构统一审计报告

- 日期：2026-10-23
- 基线：master @ 4e8a520 (Phase 6.9)，工作区干净
- 范围：全部 11 个 csproj（src 6.5 万行 / tests 4.1 万行），只读审查，未修改任何代码
- 方法：4 路并行专项审查（结构分层、安全、Application+Infrastructure 质量、UI 质量）+ 构建与测试实测 + 高严重度结论人工抽查复核

> **执行状态（2026-10-24 更新）**
> - ✅ Phase 0 已完成并提交（`2335e0f`）：slnx 补齐 3 项目；UI 引用 PluginHost/Worker 保证 exe 随主程序输出；删除 4 个 Class1.cs；移除 EFCore.InMemory；.gitignore 修正；UITests 命名空间归位。
> - ✅ Phase 1 已完成并提交（`1e120b1`）：CrashGuard 三件套全局异常兜底 + SafeAsync 包装；退出序列接入 Host Dispose 与主 VM 释放；TaskService 进度节流（死锁诱因消除）；PluginHostManager/WorkerHostManager 生命周期修复；IPC 改 Type 分发（原抽象基类反序列化必炸）；ThumbnailService 边缘伪影修复；28 个失败测试全部修复（ImageCache 簇迁 UITests 改 [AvaloniaFact]——其原静态构造在 xunit 工作线程初始化 Avalonia 即炸，亦是整套件偶发挂起的根源）。
> - 验证：HD2Mimnt.Tests **1442/1442 连续两次全绿、无挂起**。
> - ⚠️ 遗留：UITests 存在 **137 个预存环境性失败**（"Cannot get KeyValueStorage on the idle test context"，在 Phase 0 之前的基线上同样失败，与本方案改动无关），修复需调查 headless 平台配置，建议纳入 Phase 4。
> - ✅ Phase 2 已完成并提交（`23da11e`）：AI ApiKey 迁 DPAPI（数据库列恒空、旧明文自动迁移）；DownloadOptions 哈希校验能力（ExpectedHash）与默认拒绝明文 http（用户直连通道显式放行）；HD2IAS 更新链路以 GitHub 官方 Contents API 的 git blob SHA-1 为内容锚点，代理投毒整体拒绝、无锚点仅官方直连；解压防护（路径越界拒绝 + 单条目 2GB/总量 4GB/压缩比 1000× 配额）；插件/工作者管道 CurrentUserOnly；插件安装记录程序集 SHA-256 并新增 StartPluginAsync 启动前校验；VACUUM INTO 参数化。新增 13 个安全测试。
> - 待执行：Phase 3（数据层与资源）、Phase 4（结构治理）。

---

## 0. 总体结论

**整体评价：代码纪律明显高于同类项目平均水平**。安全基本面良好（DPAPI 凭据加密、日志主动脱敏、全量参数化 SQL、ArgumentList 进程启动、URL 协议白名单、无危险序列化器、无 TLS 校验禁用）；异步纪律好（无 async void 业务方法、无 throw ex、取消令牌覆盖率高、订阅/退订普遍配对）。

但存在**四类系统性风险**，集中在：① 稳定性致命点（无全局异常兜底、进程生命周期泄漏、测试套件可复现死锁）；② 数据一致性（仓储层缺事务 + N+1）；③ 密钥与供应链（AI ApiKey 明文、下载/更新无哈希校验）；④ 工程治理（解决方案不完整、无中央包管理、测试基线红着）。

### 基线实测

| 检查 | 结果 |
| --- | --- |
| `dotnet build HD2Mimnt.slnx` | ✅ 0 错误，122 警告（集中在 UITests 的 System.Drawing CA1416 平台兼容警告） |
| `dotnet test HD2Mimnt.Tests` | ❌ **1450 个测试中 28 个失败**（详见 §E） |
| 测试套件稳定性 | ❌ **第二次运行死锁挂起**，已抓取托管堆栈定位（详见 §E.2） |

### 问题统计

| 严重度 | 数量 | 代表问题 |
| --- | --- | --- |
| 高 | 10 | 无全局异常处理、Host 不 Dispose、TaskService 同步阻塞、仓储无事务+N+1、测试死锁 |
| 中 | 21 | ApiKey 明文、下载无哈希校验、插件无来源校验、巨型 ViewModel、LocalizedBinding 反射绑定 |
| 低 | 18 | 模板残留、命名空间错位、重复实现、TODO 等 |

---

## A. 工程与项目结构

### A1.【高】解决方案不完整，Worker 部署链路断裂

`HD2Mimnt.slnx` 仅列 8 个项目，磁盘上实际 11 个。未列入：`src/HD2Mimnt.Worker`、`tests/HD2Mimnt.PerformanceTests`、`tests/HD2Mimnt.StabilityTests`。

- `WorkerHostManager.cs:209` 运行时按路径 `Path.Combine(appDirectory, "HD2Mimnt.Worker.exe")` 拉起 Worker，但 UI 不引用 Worker 项目，**没有任何构建机制保证 Worker.exe 被复制到应用目录**，且 `dotnet build HD2Mimnt.slnx` 根本不构建它 → 正式发布大概率缺 Worker.exe，功能静默失败。
- PerformanceTests / StabilityTests 不构建则质量门长期失效；UITests 还以 `Compile Include` 跨项目链接了 PerformanceTests 的源文件（`UITests.csproj:19-21`），依赖一个不在解决方案里的目录，脆弱。

### A2.【中】分层耦合超约定

- `Application → Infrastructure`：78 个 Application 文件中 20 个直接 `using HD2Mimnt.Infrastructure.*`（Storage/Archive/Platforms/SoundFix/Hd2IAS 等），耦合面大。按仓库约定（Application 是聚合点）可接受，但应有意识控制。
- `Infrastructure` 引用 **Avalonia 12.0.3**（推测服务缩略图缓存）——数据层依赖 UI 框架，属分层污染。
- `SettingsViewModel.cs:6,19,114`：ViewModel 直接 `new DownloadSettingsService(settingsService)`（Infrastructure 类型），是全 UI 层唯一超出组合根的违规点（已人工复核属实）。修复成本极低：改构造注入即可（DI 已注册）。
- `BackupService.cs:122-129`：Application 层用**全限定名** `new Microsoft.Data.Sqlite.SqliteConnection(...)` 直接操作 SQLite，绕过 Infrastructure 抽象也绕过 using 检测——全 Application 唯一一处，应下沉到 Infrastructure（如 `IDatabaseMaintenance` 接口）。

### A3.【中】包管理与分析器治理缺失

- 无 `Directory.Build.props` / `Directory.Packages.props`（无中央包管理）。
- **Microsoft.Extensions 三版本混用**：9.0.0（Core）/ 10.0.0（PluginHost、Worker）/ 10.0.11（UI、Tests）；测试栈两代并存（Tests/UITests 用 TestSdk 17.14.1 + xunit 2.9.3/3.2.2，Perf/Stability 用 17.12.0 + xunit 2.9.2）。
- `tests/HD2Mimnt.Tests.csproj:13` 引用 `Microsoft.EntityFrameworkCore.InMemory 9.0.0`，但 src 无任何项目使用 EF Core——疑似遗留依赖。
- 全仓库无 `TreatWarningsAsErrors`、无 `.editorconfig`，122 个构建警告无人治理。

### A4.【低】卫生问题

- 4 个模板残留 `Class1.cs`：Core/Application/Infrastructure/**PluginSDK（泄漏到 SDK 公开面）**。
- `tests/HD2Mimnt.UITests` 全部 45 个文件命名空间错位（声明为 `HD2Mimnt.Tests.UI`）。
- `NexusModsPlatformAdapter.cs:10` 目录与命名空间割裂（有注释说明是有意保留，可接受但应记录）。
- `.gitignore:154` 时间戳硬编码 `.tmp-platform-download-1792644419808/`（未用通配符，下个时间戳目录不会被忽略）、`.claude/` 重复条目。
- 本地磁盘约 2.3GB 构建垃圾（`.tmp-platform-download-*` 765MB、`.clean-code-check` 766MB、`.layout-validation` 766MB、`.vs` 12MB）——**未被 git 跟踪**（git ls-files 核实为 0），仅本地卫生问题。
- CLAUDE.md 与 AGENTS.md 双真相源无同步机制；AGENTS.md 的分层约定与 SettingsViewModel 现状不符。

---

## B. 安全

基本面良好项（不再展开）：DPAPI 凭据加密正确（`PlatformCredentialStore.cs`、`ArchivePasswordProtection.cs`，CurrentUser + 熵 + 内存清零）；日志三重脱敏（`PlatformResponseLog.cs` 全量正则替换 token/cookie/bearer）；SQL 全参数化；7z/minidump 全走 ArgumentList 且密码走 stdin；浏览器 URL 仅 http/https 且无 userinfo；无 BinaryFormatter/Newtonsoft；无 TLS 校验禁用；临时目录全 GUID 命名；asInvoker 不提权。

### B1.【中】AI Provider ApiKey 明文存 SQLite

`AIProviderConfigRepository.cs:90-177`：`ApiKey` 列直接明文读写（已人工复核，文件内无任何加密调用），与 Nexus/Ayaka 的 DPAPI 待遇不一致。`hd2mimnt.db` 被拷贝/同步/提交即泄露第三方 LLM 密钥。
**修复**：复用 `IPlatformCredentialStore`（DPAPI）存密文，DB 只存引用；带一次性迁移。

### B2.【中】下载与更新无完整性校验（供应链）

- `HttpDownloadService.cs` / `DirectDownloadService.cs` / `DownloadManagerService.cs`：下载完成后**不与 Nexus 元数据中的校验和比对**（元数据里本就有可用的哈希字段）。
- `GitHubUpdateSource.cs:12-18`：HD2IAS 更新以 `api.gitproxy.dev`、`gh-proxy.com`、`fastgit.cc` 三个第三方"公益加速"代理作为 raw.githubusercontent.com 的回退源，**且下载的 zip 无任何哈希/签名固定**——代理方可投递替换的更新包。这是本仓库最大的供应链面。
- 对比正面样例：Lua 校验钉住游戏自带 `lua51.dll` 的 SHA-256（`LuaCompilerValidator.cs:15-18`），说明项目已有"哈希固定"实践，只是没有覆盖更新链路。

### B3.【中】插件系统无来源校验，权限模型不落地

`PluginLoader.cs:45,61` 直接 `LoadFromAssemblyPath + Activator.CreateInstance`，无签名/哈希/白名单；manifest 声明的 `PermissionMode/RequestedPermissions` 只写 DB（`PluginService.cs:86-97`），宿主运行时**不做任何强制**——插件在子进程内以当前用户完整权限运行。进程隔离 + STJ IPC + 窄主机对象面（Storage 限文件名、ModAccess 未实现）是好的，但"权限"目前只是 UI 装饰。
**修复**（择一）：发布者哈希/签名清单校验 + 安装时确认落到运行时强制；或文档明示"插件=完全信任"。

### B4.【中】解压缺少应用层防护

`SharpCompressArchiveExtractor.cs:76,92-94`：`WriteToDirectory(ExtractFullPath=true)` 完全依赖 SharpCompress 0.50.4 内置的穿越校验（该版本已包含对 CVE-2026-44788 变体的修复，版本钉住目前安全），但应用层不做二次校验；同时**无压缩炸弹防护**（无总解压字节数/压缩比上限，恶意 mod 包可填满磁盘）。
**修复**：写入前对 `entry.Key` 做 `GetFullPath` 前缀校验 + 累计字节上限（如 4GB）。

### B5.【低】IPC 与进程启动细节

- `PluginHostManager.cs:84-86` 与 `NamedPipeServer.cs:49-51`：管道消息长度前缀**无上限/负数校验**（恶意长度直接 `new byte[length]`），管道未设 `PipeOptions.CurrentUserOnly`。仓库内 `BrowserProtocolHost.cs:92-100` 已有正确范本（CurrentUserOnly + 8192 上限 + 负数拒绝），照搬即可。
- `PluginHostManager.cs:305-308` / `PluginHost.cs Program.cs:92-95`：IPC 消息 `Deserialize<PluginHostMessage>`（基类）后做 `is PluginCompletedResponse` 判断**永远为 false**（STJ 无多态配置）——既是功能 bug 也是后续按类型分发前必须修的点。
- `PluginHostManager.cs:175,183`：`Arguments = $"--plugin-id {pluginId}..."` 字符串拼接，且 `Guid.Parse` 在进程已启动后才执行——校验应前置。
- `ExternalToolService.cs:186-195`：用户自定义工具参数经 `Arguments` 字符串替换（自配置自我伤害面，低危），建议改 ArgumentList 或加引号。
- `HttpDownloadService.cs:338`：允许 http:// 明文下载，建议默认仅 https。

### B6.【低/信息】其余

- `BackupService.cs:68-69`：`VACUUM INTO '{escapedPath}'` 手工转义，改参数化 `VACUUM INTO @path` 可消除。
- 断点续传文件名为 URI 的 SHA-256（可预测，`HttpDownloadService.cs:41`），位于应用数据目录，同用户进程可预置符号链接——低危，可用随机后缀。
- 备份恢复直接替换 DB/settings.json（备份功能的固有信任边界，建议恢复前显示来源与时间）。

---

## C. Application / Infrastructure 代码质量

### C1.【高】TaskService 进度回调同步阻塞（UI 卡顿源，已复核）

`TaskService.cs:151-164`：`SynchronousProgress` 回调内 `stateGate.Wait()` + `_taskRepository.UpdateAsync(...).GetAwaiter().GetResult()`——**每次进度报告都在调用线程上同步阻塞并同步写库**。Worker 在 UI 上下文同步 Report 即卡 UI；高频进度时线程池被系统性阻塞。这与 §E 测试死锁互为印证。
**修复**：进度节流（≥250ms 或百分比变化）+ Channel 后台写库，或 `WaitAsync(token)` + 真 await。

### C2.【高】PluginHostManager 生命周期缺陷群

`PluginHostManager.cs`：
- `:218-224` 启动失败路径直接 return，不杀进程、不 Dispose 管道、不清理 `_runningHosts` → 进程泄漏，后续重试永远返回 "Plugin already running"。
- `:218/:221` 同一管道双读者竞态（监听循环 + 直接 `ReceiveMessageAsync` 抢初始响应）→ 命令结果错乱或等待者永不完成（**测试死锁的头号嫌疑**，见 §E.2）。
- `:237` `process.WaitForExit(5000)` 同步阻塞 UI 最多 5 秒。
- `:157-160` 重定向子进程 stdout/stderr 但从不读取 → 子进程输出填满管道缓冲后挂死。
- `:271-277` `StartNewCommand` 直接替换 TCS，上一个未完成的等待者**永远挂起**（测试死锁二号嫌疑）。

### C3.【高】数据层一致性与 N+1

- `LogicalModRepository.cs:20-58`：聚合写入无事务（主行/组件/部署文件/包各开连接逐条写），中途失败留残缺聚合；`DeleteAsync`(:136-160) 同样 4 条 DELETE 无事务。
- `LogicalModRepository.cs:112-184`：`GetAllAsync`/`SearchByNameAsync` 对每个 mod 各发 2 条查询（N+1），库列表页是核心路径。
- `DeploymentRepository.cs:58-109`：`GetBySessionIdAsync`/`GetByModIdAsync`/`GetLatestDeploymentsAsync` 循环调 `GetDeploymentFilesAsync`（每次新开连接）——崩溃诊断/部署排查路径的放大器。
- 连接策略"每操作新连接 + Pooling=false"与 WAL 兼容，但被 N+1 与高频进度写（C1）放大成热点。

### C4.【高】大文件全量读入内存

- `SoundFixInspector.cs:85`：确认含 sound bank 后 `File.ReadAllBytesAsync` 全量读入——音效补丁正是数百 MB 级文件，前面精心做的流式头部扫描被这一步抵消。改按需读取区间或 MemoryMappedFile。
- `SoundFixTransaction.cs:108`：恢复流程逐文件 `ReadAllBytesAsync` 常驻内存直到提交。Stage 改临时文件、提交时移动。

### C5.【中】取消与资源

- `ImageLoaderService.cs:15,80-83`：全局 `_globalCts` 被 `CancelPendingLoads()` 一次性 Cancel 后**永久失效**（单例），此后所有新加载静默返回 null；类无 Dispose。改"代际 CTS"（Cancel 后重建）。
- `WorkerHostManager.cs:54-61`：`Task.Delay(10000)` 与连接任务竞速，外部取消误抛 TimeoutException；只 `process.Kill()`，pipeServer/connectTask/timeoutTask 均不释放；`:195-203` `WaitForCompletionAsync` 忽略 CT。
- `ImageCacheService.cs:31-63` + `LruCache.cs:70-78`：LRU 驱逐直接丢弃 `Bitmap`（原生内存）不 Dispose，长期运行内存压力。驱逐回调补 `(v as IDisposable)?.Dispose()`。
- `LocalizationService.cs:194`：`Dispose()` 内 `GetAwaiter().GetResult()` 阻塞等监控任务——埋着的死锁种子，改 DisposeAsync。
- `TaskService.cs:270-299`：`_taskStateGates` GetOrAdd/TryRemove 竞态致条目残留、SemaphoreSlim 不 Dispose。
- `DownloadManagerService.cs:193-201`：`lock (task)` 内做 `File.Move`（同步 IO 可达秒级持锁），移出锁外。

### C6.【中】重复实现

- **12+ 处**各自手写"路径必须位于根内"校验（大小写/分隔符处理不一）→ 提取共享 `PathGuard.IsInsideRoot`。
- `NamedPipeServer.cs:43-57` 与 `PluginHostManager.cs:82-96`：两份相同的长度前缀帧读取，且都只 ReadAsync 一次不处理短读 → 合并为带 `ReadExactlyAsync` 循环 + 长度上限的帧读取器。
- SHA-256 计算散布 3+ 处，未复用 Core 的哈希工具。

### C7.【低】杂项

- `GameDetectionService.cs:116` TODO：游戏版本号读取缺失（版本相关诊断大概率恒空）。
- `ArchiveInspector.cs:176` TODO 过时（SharpCompress 已支持 7z/RAR），两个 Inspector/Extractor 并存属架构性重复，判断可能不一致。
- `TestDataService.cs` 开发用数据服务混在生产 Application 程序集（含直接写模组目录）→ 移测试支持项目或 `#if DEBUG`。
- `ImageLoaderService.cs:120-128`：catch 全部异常静默转 null 且零日志。
- 库层 1895 处 await 仅 99 处 `ConfigureAwait(false)`——系统性提示（当前被 Task.Run 包裹掩盖，未发现可复现死锁）。

---

## D. UI 层代码质量

### D1.【高】无全局异常兜底 + 关键 async void 无 catch

- 全项目 **0 处** `Dispatcher.UIThread.UnhandledExceptionHandler` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`。
- `MainWindow.axaml.cs:77-92` `OnFileDrop`（async void）**无 catch**——拖放导入抛异常直接崩进程；`:135-145` `OnClosing`（async void）无 catch 且无重入闸——关闭路径异常则窗口永远关不掉。
- 其余 40+ 个 async void 事件处理器几乎全部无 catch（典型：`LibraryView.axaml.cs:31,79,292`、`SettingsView.axaml.cs:15-50`、`LibraryViewModel.cs:515` 防抖 Tick）。
**修复**：Program/App 注册三件套全局处理器 + 提供 `SafeAsync` 包装统一套用。这是**整个 UI 稳定性的放大器**，收益/成本比最高的一项。

### D2.【高】Host/ServiceProvider 从不 Dispose，全部清理路径是死代码

`Program.cs:33-38` + `App.axaml.cs:104-112`：退出只显式 Dispose 了 3 个服务，`Host` 从不 `StopAsync/Dispose` → 容器内所有 `IDisposable/IAsyncDisposable` 单例（DbContext、FileLogProvider、`MainWindowViewModel.DisposeAsync`、`Hd2IASManagementViewModel.DisposeAsync`…）的清理代码**正常退出时不执行**。§C 的多个"泄漏"因此被放大。
**修复**：`desktop.Exit` 中 `await host.StopAsync(); host.Dispose();`，并在其中 `await mainViewModel.DisposeAsync()`。

### D3.【中】LocalizedBinding 全量反射绑定

`LocExtension.cs:48-50`：`{loc:LocalizedBinding}`（全项目数百处主要文本绑定）内部固定走 `ReflectionBindingExtension`——`AvaloniaUseCompiledBindingsByDefault=true` 对最常用绑定实际失效，属性改名不报编译错。修复：为 LocalizedBinding 提供基于 `x:DataType` 的编译路径，或 CI 加属性存在性检查。

### D4.【中】巨型 ViewModel 与重复 UI 基建

| 文件 | 行数 | 备注 |
| --- | --- | --- |
| ProfileViewModel（3 个 partial） | 1939 | |
| LibraryViewModel | 1598 | 15 个构造依赖 |
| MainWindowViewModel | 1011 | **28 个构造参数**，上帝对象 |

重复实现（均建议抽公共组件）：两套拖拽排序引擎（ProfileView 781 行 code-behind vs ModCreationView.Sorting.cs，同构到透明度 0.35/滚轮 ×3 都一样）；两套卡片多选语义；两套右键菜单构建；**8+ 处复制粘贴的"IsBusy+CTS+取消+异常→通知"忙碌执行器**；14 处独立忙碌 UI（属性名从 IsBusy 到 IsScanning 到 IsDeploying 不一）；3 处 UI 线程 marshal 帮助函数。

### D5.【中】事件订阅与 Dispose

- `MainWindowViewModel.DisposeAsync`（`:420-433`）从未被调用（D2 的后果），且退订清单不完整（10+ 订阅只退 7 个）。
- Transient VM 订阅单例事件无退订（`NotificationPanel.axaml.cs` 内嵌的 ViewModel、`LibraryViewModel.cs:295`、`ProfileViewModel.cs:425`、`LogViewModel.cs:40`）——当前一次性创建掩盖问题，VM 重建即泄漏。
- 全项目无 CompositeDisposable，全部手工配对，漏配概率高。

### D6.【低】其余

- `MainWindowViewModel.cs:1000-1010`：VM 直接 `new DevToolsWindow` 开窗口——VM 反向依赖 Views。
- `PluginManagementViewModel.cs:52,306`：生产类内嵌自动同意的 `TestConfirmationService` 兜底构造——误用即静默跳过所有确认。
- 机制外英文硬编码：`MainWindowViewModel.cs:48,57,61,71,75,85,362-366,582,997`（窗口标题/GameStatus 状态机全英文），中文用户会看到英文；设置分类用中文字符串当枚举（`SettingsViewModel.cs:66,93-103`）。
- 中文源串本地化方案本身可行，但缺"源串未命中目录"的构建期校验，漏译会静默显示中文。

---

## E. 测试健康度（本次实测）

### E1. 28 / 1450 失败（干净工作区 HEAD 上）

失败聚集在：`ModServiceTests.ImportMod_WithoutDeclaredPreview_*`（缩略图推断，断言 Expected: 1073742078 之类数值差异）、`ProfileModOptionsTests.DeployProfileAsync_NoManifest_WithSubDirs_*` 与 `ModImportServiceTests.ImportModAsync_WithoutManifest_WithSubDirs_*`（期望 Legacy 选项推断、实际得到 V1/null）——指向**导入/部署的"无 manifest 子目录推断 Legacy 选项"功能回归**，且三个测试类围绕同一功能。完整清单见文末附录（本次运行结果）。

### E2. 测试套件可复现死锁（已抓堆栈定性）

同日第二次运行 `HD2Mimnt.Tests`，测试宿主**完全死锁**（CPU 零增量、日志零增长 >10 分钟）。`dotnet-stack` 托管堆栈显示：**没有任何线程处于产品代码中**——所有线程均为测试基建（xunit 运行器、消息泵、空闲线程池）。定性结论：**某个异步测试在 await 一个永不完成的 Task**（异步挂起而非线程互锁）。头号嫌疑即 §C2 的 PluginHostManager 双读者竞态 / TCS 替换（`PluginHostManager.cs:218-224, 271-277`），二号嫌疑 `TaskService` 的 `stateGate.Wait()`（§C1）。
**修复方向**：先修 C1/C2 的三个挂起点；测试侧给 PluginHost/Worker 相关集成测试加 xunit `Timeout` 或集合串行化，防止单个挂起拖死整套 CI。

### E3. 其它

- 两代测试基建并存（A3）；Performance/Stability 不在解决方案内，长期不跑等于不存在。
- 122 个构建警告无门禁（全部在 UITests 的 System.Drawing CA1416——要么加 `[SupportedOSPlatform]` 标注要么换图像库）。

---

## 执行方案

原则：先止血（稳定性+测试基线），再安全，再数据层，最后结构治理；每阶段结束构建 + 全量测试必须回到绿色（0 失败、无挂起）再进入下一阶段；遵循 Conventional Commits，小步提交。

### Phase 0 — 卫生与止血（半天，纯低风险清理）

1. slnx 补齐 Worker / PerformanceTests / StabilityTests 三个项目；为 Worker 增加构建期复制（UI 或发布脚本引用/Content）。
2. 删除 4 个 `Class1.cs`（含 PluginSDK 公开面）；移除 `EFCore.InMemory` 遗留引用。
3. `.gitignore` 修正：`.tmp-platform-download-*` 通配符、去重 `.claude/`；清理本地 2.3GB 构建垃圾目录。
4. UITests 45 文件命名空间归位 `HD2Mimnt.UITests`。
- 验证：`dotnet build HD2Mimnt.slnx` 0 错误 + 全项目构建齐 11 个；测试数量不减。

### Phase 1 — 稳定性致命点（1-2 天）

1. 全局异常兜底：`Program`/`App` 注册 `Dispatcher.UIThread.UnhandledExceptionHandler` + `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`，统一写日志+通知；提供 `SafeAsync` 包装并覆盖 `MainWindow.OnFileDrop/OnClosing`、LibraryView 防抖等 async void 热点（OnClosing 加重入闸）。
2. 退出清理：`desktop.Exit` 中 `await host.StopAsync(); host.Dispose();` + `mainViewModel.DisposeAsync()`，补全其退订清单。
3. `TaskService` 进度回调改节流 + Channel 异步写库（C1）；`_taskStateGates` 补 Remove/Dispose。
4. `PluginHostManager`：失败路径统一清理（杀进程/弃管道/移字典）；消除双读者；`StartNewCommand` 替换前 `TrySetCanceled`；`WaitForExit` 改异步；消费或关闭子进程输出重定向（C2 全项）。
5. `WorkerHostManager` 取消/超时区分 + 资源 finally 清理。
6. 修复 28 个失败测试指向的"无 manifest 子目录 Legacy 选项推断"回归；给 PluginHost/Worker 集成测试加超时。
- 验证：全量测试 ≥2 次连续 0 失败且无挂起（防 flaky）；手动冒烟：拖放导入异常文件、导入中关窗、插件启动失败后重试。

### Phase 2 — 安全加固（2-3 天）

1. AI ApiKey 迁 DPAPI（带一次性 DB 迁移：读到明文→加密→覆写）（B1）。
2. 下载完整性：下载完成比对平台元数据哈希，失败即删（B2 前半）。
3. HD2IAS 更新链路：zip 哈希固定清单或仅官方源，代理仅作为允许哈希命中的加速（B2 后半）。
4. 解压防护：应用层 entry 全路径前缀校验 + 总解压字节上限 + 压缩比阈值（B4）。
5. IPC 加固：帧长度上限 + 负数拒绝 + `CurrentUserOnly`（照搬 BrowserProtocolHost 模式）；修 IPC 多态反序列化失效（按 Type 字段分发）；`Guid.Parse` 前置（B5）。
6. 插件来源校验：安装时记录哈希，宿主启动前校验；权限模型二选一（强制或文档明示完全信任）（B3）。
- 验证：新增单测覆盖每个校验路径（路径穿越用例、超长帧用例、哈希不匹配用例）；安全项补进 `docs/agent-guides/platforms-and-downloads.md` 易错点。

### Phase 3 — 数据层与资源（2-3 天）

1. `LogicalModRepository.AddAsync/DeleteAsync` 包事务；`GetAllAsync`/`SearchByNameAsync`/`DeploymentRepository` N+1 改批量 IN/JOIN（C3）。
2. `SoundFixInspector` 按需读区间替代 ReadAllBytes；`SoundFixTransaction` Stage 改临时文件（C4）。
3. `ImageLoaderService` 代际 CTS + Dispose；`LruCache` 驱逐回调 Dispose Bitmap；`DownloadManagerService` File.Move 出锁（C5）。
4. 合并帧读取器（含短读处理）；提取 `PathGuard` 收敛 12+ 处路径校验（C6）。
- 验证：全量测试绿 + 大库性能测试（PerformanceTests 现在已在 slnx 内）对比 N+1 修复前后耗时。

### Phase 4 — 结构治理（持续，按需排期）

1. 中央包管理：`Directory.Packages.props` + `Directory.Build.props`（统一 net10.0/Nullable/版本矩阵，消灭三版本混用）；加 `.editorconfig`，警告分级治理（先清零 CA1416，再 `TreatWarningsAsErrors=nullable` 起步）。
2. UI 基建收敛：`BusyOperationRunner`（吃掉 8+ 处复制）、`DragSortController`、共享多选/右键菜单、统一忙碌组件；`PathGuard` 全量替换。
3. 巨型 VM 拆分：MainWindowViewModel（28 参数）按页面聚合拆中介对象；ProfileView 781 行拖拽引擎随 DragSortController 下沉。
4. `SettingsViewModel` 改构造注入；`BackupService` 的 SQLite 直连下沉 Infrastructure；评估 Infrastructure 去 Avalonia 依赖。
5. LocalizedBinding 编译绑定路径 + 本地化源串构建期校验；机制外英文硬编码归位（B/D6）。
6. 文档同步：AGENTS.md 分层约定与代码对齐；CLAUDE.md/AGENTS.md 增加互相指向；`ArchiveInspector` 与 SharpCompress 实现合并。

### 验收门（每阶段通用）

```bash
dotnet build HD2Mimnt.slnx --no-restore        # 0 错误，警告数不增
dotnet test tests/HD2Mimnt.Tests --no-build     # 0 失败（连续 2 次）
dotnet test tests/HD2Mimnt.UITests --no-build   # Phase 1 起纳入
```

---

## 附录：本次测试运行明细

三次运行 `HD2Mimnt.Tests`（同二进制、干净工作区）：

| 运行 | 结果 |
| --- | --- |
| 第 1 次 | 28 失败 / 1422 通过，2m17s |
| 第 2 次 | **死锁挂起**（>10 分钟无进展，已抓堆栈定性见 §E.2，人工终止） |
| 第 3 次（--blame-hang 3m） | 28 失败 / 1422 通过，2m21s，未触发挂起检测 |

失败为**确定性回归**（两次完成运行结果完全一致）；挂起为**偶发**（3 次中 1 次）。

### 28 个失败测试按簇分组

| 簇 | 数量 | 测试 | 指向 |
| --- | --- | --- | --- |
| 缩略图缓存 | 8 | `Services.ImageCacheServiceTests`（ClearDiskCache/ClearMemoryCache/Concurrent_Access/DiskCache_Persist/GetAsync_Null/GetOrLoadAsync/GetStats/PreloadAsync，**整类全灭**） | ImageCacheService 本身或其测试基建已坏 |
| Manifest V1 推断 | 10 | `Services.ModImportServiceManifestV1Tests`（InferManifestFromDirectory×4、UpgradeModManifestToV1Async×3、UpgradeAllMods×1）、`Services.ModImportServiceTests.ImportModAsync_WithoutManifest_WithSubDirs_ShouldInferLegacyOptions`、`ProfileModOptionsTests.DeployProfileAsync_NoManifest_WithSubDirs_ShouldTreatSubDirsAsLegacyOptions` | "无 manifest 子目录→Legacy 选项推断 / V1 升级"功能回归（期望 Legacy，实际得到 V1/null） |
| 缩略图推断 | 3 | `ModServiceTests.ImportMod_WithoutDeclaredPreview_UsesOnlyRootImage(icon/rootImage/nestedImage 参数化)` | 导入时预览图推断（期望数值与实际不符） |
| 插件管理 VM | 7 | `ViewModels.PluginManagementViewModelTests`（LoadPlugins×2、Enable×2、Disable、Uninstall、ViewExecutionHistory，**整类全灭**） | PluginManagementViewModel 与测试脱节；注意审计 D6 发现该类内嵌"自动同意"的 TestConfirmationService 兜底构造，疑与断言失败相关 |

### 死锁运行现场记录（第 2 次）

- 现象：日志停在 00:00:47（ModImportServiceTests 失败后），testhost CPU 冻结（60 秒 0 增量），无泄漏的 PluginHost/Worker 进程。
- `dotnet-stack report`：12 个线程全部位于测试基建（xunit 运行器、消息泵、空闲线程池），**无一线程处于产品代码**→ 某异步测试 await 永不完成的 Task（异步挂起）。
- 头号嫌疑：`PluginHostManager` 双读者竞态与 TCS 替换（§C2），二号嫌疑：`TaskService` 的 `stateGate.Wait()`（§C1）。修复后需以连续多次全量运行验证挂起消失。
