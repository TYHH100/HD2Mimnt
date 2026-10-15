# 测试与后台生命周期

运行构建/测试，或修改 TaskService、轮询、SQLite 生命周期、备份、日志及性能采样时先读本文件。

## 易错点与正确做法

- 日志记录级别在“设置 → 日志”保存到 SQLite，默认 Warning，显示筛选不改变记录级别。`ILogger` 筛选只读取 `DiagnosticLogSettings` 的内存快照，禁止在筛选回调读取 SettingsService/SQLite，避免设置服务记录日志时重入设置锁。读取日志从尾部限制条数和字节数，允许共享写入；多行堆栈合并为同一条，级别判断来自记录头，不搜索正文中的“错误”字样。来源：[.NET 日志筛选](https://learn.microsoft.com/dotnet/core/extensions/logging#configure-logging)、[FileShare](https://learn.microsoft.com/dotnet/api/system.io.fileshare)。

- Headless 实际点击验证在工具栏、弹窗等改变布局后，应先排空 Dispatcher、更新布局并调用 `CaptureRenderedFrame` 刷新合成命中场景，再执行 `InputHitTest` 和鼠标输入。单独 `UpdateLayout` 或 `RenderTargetBitmap.Render` 可能显示新位置却命中旧场景，不能据此误改业务绑定。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Headless/Avalonia.Headless/HeadlessWindowExtensions.cs

- 导入、部署、清理等耗时操作必须可取消并保持 UI 可响应；临时解压目录和测试目录必须在流程结束后清理。

- 修改后先顺序执行 `dotnet build HD2Mimnt.slnx --no-restore` 和相关测试；共享 SQLite 输出的测试程序集已禁用类级并行，避免全局池清理互相干扰。

- 仅在实际需要时添加文档和测试，不用占位页面或“Coming Soon”文案冒充完成。

- 测试异步进度回调时不要用默认 `Progress<T>` 后立即断言，使用同步测试进度实现或显式等待回调完成。

- 可取消的排队任务要在后台执行体开始时再次检查状态，避免取消后被异步启动逻辑恢复为运行中。

- TaskService 的进度回调不能用并发异步写库；同一任务的进度、完成和取消状态必须串行持久化，避免 SQLite 行状态被旧进度覆盖。

- BenchmarkDotNet 会生成额外产物；基准完成后清理临时目录，并在报告中记录实际数据集规模和测量边界。

- UI 大数据验收使用 `HD2MIMNT_DATA_PATH` 隔离目录；开发工具测试模组必须有稳定的 `HD2Mimnt Test ` 名称前缀，清理时按前缀匹配，不能按用户模组路径或模糊目录名删除；导入源目录必须在每项完成或失败后清理。

- 性能采样脚本默认输出到 `%TEMP%\HD2MimntPerformance`；短时启动样本只能验证采样链，长时验收必须记录持续时间、采样间隔、数据集和 UI 操作范围，完成后删除临时输出。

- 托管堆采样只在显式设置 `HD2MIMNT_METRICS_PATH` 时启用，使用 `GC.GetTotalMemory(false)` 记录；不要在普通运行路径中无条件启动诊断定时器。

- 备份必须通过 `BackupService` 生成临时 SQLite 快照后再压缩，恢复前必须校验路径位于备份目录，并在覆盖当前数据库前要求确认。

- 应用设置由 `SettingsService` 通过 `ISettingsRepository` 保存到 SQLite `Settings` 表，`Value` 是保留类型的 JSON 文本。旧 `settings.json` 仅迁移一次，数据库已有键优先；导入与 `SettingsMigrations` 标记同事务提交，成功加载后才删除旧文件。不能只凭空表判断是否迁移，否则重置后残留 JSON 会复活旧值。新备份只含数据库；旧备份先在临时数据库导入其 JSON 并校验，再覆盖当前库，不能混入当前目录的旧 JSON。恢复覆盖与设置写入共用设置闸门，成功后重载并发布变化。来源：https://www.sqlite.org/lang_transaction.html 、https://www.sqlite.org/lang_upsert.html

- `Microsoft.Data.Sqlite` 的异步 API 实际同步执行，设置写事务在后台线程使用短连接；启动加载直接同步读取，禁止用 `Wait()` 等待会回到 UI 调度器的异步方法。短连接禁用连接池，避免备份恢复/目录搬迁时留下文件占用。数据库失败测试使用 SQLite 事务/触发器制造写入错误，不能再锁 `settings.json`。启动定位用的 `storage-locations.json` 负责在数据库打开前定位数据目录，不属于应用偏好设置。来源：https://learn.microsoft.com/dotnet/standard/data/sqlite/async 、https://learn.microsoft.com/dotnet/standard/data/sqlite/connection-strings

- 日志页面只能读取有限的尾部记录，文件日志读取要允许共享写入；不要一次性把整个日志文件加载进 UI 内存。

- 插件 manifest 的解析和校验属于 `IPluginService`，View 只负责文件选择和权限确认，不要在 code-behind 中复制安装逻辑。

- SQLite 上下文的连接初始化和释放必须受生命周期闸门保护；不要在生产 `DbContext.Dispose` 中调用全局 `ClearAllPools` 或强制 GC，否则会干扰并行测试和其他上下文的连接。

- Avalonia Headless 测试在同一进程只能进行一次 `AppBuilder.SetupWithoutStarting()`；新增窗口布局测试必须复用现有测试应用或拆分到独立测试程序集，不能再创建第二个 AppBuilder，否则会出现初始化冲突、资源缺失或测试宿主不退出。

- Avalonia Headless 测试的 `AppBuilder.SetupWithoutStarting()` 必须在测试宿主拥有的 UI 线程上初始化；若测试程序集并行发现或从错误线程触发静态初始化，可能出现 `The calling thread cannot access this object because a different thread owns it`，应先隔离该测试程序集/关闭并行后再判断业务代码是否回归。

- 共享 `bin/obj` 输出的构建和测试必须顺序执行；并行启动会锁定 Core 或 UI 程序集，产生误导性的 CS2012 文件占用错误。

- 运行中的 UI 或 Visual Studio 锁住常规输出时，可用 `dotnet build HD2Mimnt.slnx --no-restore -m:1 -p:OutputPath=<独立临时绝对目录>/` 保留现有 obj 并隔离输出；随后 `dotnet test <测试项目> --no-build --no-restore -p:OutputPath=<同一目录>/ --filter <相关用例>` 顺序运行。确认输出中的测试程序集路径与实际通过数量，不能将无测试执行的 exit 0 当作通过；完成后删除该临时目录。来源：https://learn.microsoft.com/dotnet/core/tools/dotnet-test

- 任务中心或其它覆盖层的自动刷新必须有明确的 Start/Stop 生命周期；关闭面板和应用退出时取消刷新令牌，并串行化加载，不能留下后台轮询或让旧快照覆盖新状态。

- 任务完成事件必须在最终状态写入数据库并从运行任务集合移除后发布；UI 通知处理器只负责呈现结果和刷新视图，不能在事件回调中重新执行任务状态持久化。

- 强制终止任务必须与后台执行体的最终写入使用同一任务级闸门；先标记并持久化 `ForceTerminated`，后台取消回调不能覆盖终止状态，也不能重复发布完成事件。

- Avalonia 12 的 `Avalonia.Headless.XUnit` 依赖 xUnit v3，与现有 xUnit v2 单元测试分项目运行；UI 测试放在 `HD2Mimnt.UITests`，通过 `AvaloniaTestApplication` 复用宿主 UI 线程，不能给旧测试程序集引入第二套 xUnit。来源：https://www.nuget.org/packages/Avalonia.Headless.XUnit/12.0.3

- Headless 多用例合并运行若出现 `Cannot get KeyValueStorage on the idle test context`，先逐用例分进程、顺序运行以区分宿主上下文错误与业务断言失败；不得将隔离通过表述为合并运行已修复，也不要通过再创建 AppBuilder 绕过宿主。当前 Avalonia.Headless.XUnit 12.0.3 / xUnit v3 3.2.2 组合已复现此问题。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Headless/Avalonia.Headless.XUnit/AvaloniaTestRunner.cs
