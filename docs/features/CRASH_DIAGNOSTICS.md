# 游戏崩溃排查

入口：**工具 → 游戏崩溃排查**。内置用户提供的 Windows x64 `minidump-stackwalk 0.27.0`，使用 `--json` 离线分析。运行、发布均携带工具与 MIT 许可证，不依赖下载目录，不上传转储，也不配置在线符号服务器。

## 使用

1. 在设置中配置游戏安装目录，进入排查页刷新转储与部署历史。默认查找 `%APPDATA%/Arrowhead/Helldivers2/dumps`、`crash_data`、`%LOCALAPPDATA%/CrashDumps/helldivers2*.dmp` 与游戏根目录、`bin` 直属的 `.dmp`；也可手动选择 `.dmp` / `.mdmp`。
2. 正常游玩确认某次部署无异常后，在历史列表选中对应部署，点击“将选中部署标记为正常”。标记是用户验证结果，不由“部署完成”自动推断。
3. 选中崩溃转储并分析。页面显示异常、地址、崩溃线程、已加载模块、崩溃前部署及其变化；优先与更早的已确认正常部署比较，没有时才与上一次完成部署比较并提示尚未验证。
4. 优先复现新增、更新、选项变化和相对顺序变化的模组；移除项也列出，以提醒依赖可能被停用。每次停用一半候选并重新部署，加载器与必需依赖保持成组。需要分享时导出格式化 JSON 报告。

## 数据与证据边界

- 新表 `CrashDeploymentSnapshots` 独立于清理用的 `DeploymentRecords` / `DeploymentFiles`，包含会话、配置、游戏目录、UTC 时间、最终部署状态，以及实际选中模组的 ID、名称、版本、库哈希、修改时间、有效选项、相对顺序和文件元数据指纹。清理部署、移除已部署模组追加新的状态快照。所有快照合计最多保留最近 50 条，每次保存自动删除更早的条目；标记正常的基准也受此上限约束。SQLite 备份自动包含该表。
- 只从本功能接入之后开始记录。不能用当前配置补造过去的部署快照。其他管理器、手动修改、游戏更新及未由本管理器部署的文件不在该时间线内。
- 文件指纹对实际选中文件的相对路径、大小、最后修改时间求 SHA-256，库哈希取模组现有值。**不是**每次重新计算所有大文件内容哈希；保留大小、时间戳且未更新库记录的外部修改可能无法识别。
- 使用 minidump 文件头中的 UTC 时间匹配相同游戏目录、崩溃前最近部署；文件复制时间不参与归因。部署晚于转储时不关联当前 DLL；崩溃发生时仍未完成的部署显示单独提示。
- DLL 文件名关联到部署记录只算线索。同名模块、`scan` 推测帧、仅加载未出现在崩溃线程的模块均不能提高为确定原因。`dxgi.dll` 等可能是系统组件或注入模块，名称本身不证明 ReShade 或某个模组。
- 资源补丁、BSL / MDL 的 Lua 脚本通常不作为独立 DLL 出现在模块表中。游戏主模块内发生访问冲突或未发现模块线索，均不能排除资源模组。部署变化只确定优先排查对象，最终仍需控制变量复现。
- 原始工具 JSON 不按文本正则解析；所有字段允许缺失 / null，缺少主模块身份时不做 HD2 模组归因。展示最多 128 帧，JSON 输出读取上限 32 MiB，进程超时为两分钟。取消和应用关闭会终止分析子进程并等待清理临时目录。
- 导出包含本地转储路径、部署信息、模块与证据边界字段；不包含转储原始内存。源转储以只读方式打开，分析不改变模组启用状态或游戏文件。

## 实现与验证

- `IMinidumpAnalyzer` / `MinidumpStackwalkAnalyzer`：进程、格式校验、JSON 读取与清理。
- `CrashDiagnosticsService`：转储发现、部署关联与报告导出。
- `CrashDeploymentHistoryService` / `ICrashHistoryRepository`：部署快照、正常基准与差异比较。
- `CrashDiagnosticsViewModel` / `CrashDiagnosticsView`：三语界面、取消与导出。
- 测试：`CrashDiagnosticsTests`；`CrashDiagnostics_ShouldUseActualDiCompareMarkExportAndCancelThroughView` 使用真实 DI、界面绑定和可控分析器验证交互。

参考：[工具源码及许可证](https://github.com/rust-minidump/rust-minidump/tree/v0.27.0)、[0.27.0 JSON 契约](https://github.com/rust-minidump/rust-minidump/blob/v0.27.0/minidump-processor/json-schema.md)、[Windows MINIDUMP_HEADER](https://learn.microsoft.com/windows/win32/api/minidumpapiset/ns-minidumpapiset-minidump_header)、[Process.WaitForExitAsync](https://learn.microsoft.com/dotnet/api/system.diagnostics.process.waitforexitasync)。
