# 性能测试

服务、真实 SQLite 压力、业务回归和合成资源扫描在本项目中运行。带图片及实际控件的联合测试位于 `../HD2Mimnt.UITests/LargeLibraryPerformanceUiTests.cs`，复用本目录的 `LargeLibraryWorkload.cs`；它使用现有的唯一 Avalonia Headless 测试应用，避免跨 xUnit 版本创建第二个 UI 宿主。

当前报告：[2026-10-17：1 万模组与图片、SQL 联合测试](../../docs/reports/large-library-performance-2026-10-17.md)。

## 运行

以下 PowerShell 命令在仓库根目录顺序执行。首次运行先正常还原依赖。输出目录必须是本次新建且只供本次使用的目录；不要指向用户已有文件夹。

```powershell
$perfOutput = Join-Path ([IO.Path]::GetTempPath()) ('HD2Mimnt-Perf-' + [Guid]::NewGuid().ToString('N'))
$perfResults = Join-Path $PWD ('perf-results-' + [Guid]::NewGuid().ToString('N'))
try {
    $env:HD2MIMNT_PERF_RESULTS = $perfResults
    dotnet build HD2Mimnt.slnx --no-restore -c Release -m:1 "-p:OutputPath=$perfOutput/"
    if ($LASTEXITCODE -ne 0) { throw '解决方案构建失败' }
    dotnet build tests/HD2Mimnt.PerformanceTests/HD2Mimnt.PerformanceTests.csproj -c Release -m:1 "-p:OutputPath=$perfOutput/"
    if ($LASTEXITCODE -ne 0) { throw '性能测试构建失败' }
    dotnet test tests/HD2Mimnt.PerformanceTests/HD2Mimnt.PerformanceTests.csproj --no-build --no-restore -c Release "-p:OutputPath=$perfOutput/" --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) { throw '性能测试失败' }
    $env:HD2MIMNT_RUN_LARGE_PERF = '1'
    dotnet test tests/HD2Mimnt.UITests/HD2Mimnt.UITests.csproj --no-build --no-restore -c Release "-p:OutputPath=$perfOutput/" --filter 'FullyQualifiedName~LargeLibrary_TenThousandImagesWithSqlFlood' --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw '大数据 UI 测试失败' }
} finally {
    Remove-Item Env:HD2MIMNT_PERF_RESULTS -ErrorAction SilentlyContinue
    Remove-Item Env:HD2MIMNT_RUN_LARGE_PERF -ErrorAction SilentlyContinue
    # 此变量只由上方固定临时根目录和 Guid 生成；先确认归属再递归删除。
    $resolvedOutput = [IO.Path]::GetFullPath($perfOutput)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedOutput.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedOutput).StartsWith('HD2Mimnt-Perf-')) {
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force -ErrorAction Stop
    }
}
```

`perf-results-*` 是有意保留的报告 JSON，审阅后自行删除或移入注明日期的报告目录。测试样本本身通过 `finally` / `UiScope.DisposeAsync` 删除。UI 测试可占用约 8 GiB 私有内存，并生成约数 GiB 临时图片；普通 UI 回归默认跳过该测试，不会无意运行大样本。

只运行真实数据库基线时筛选 `FullyQualifiedName~LargeLibraryServicePerformanceTests`；只运行冲突扫描时筛选 `FullyQualifiedName~ConflictScanPerformanceTests`。不要与其他构建/测试并行，也不要同时复用同一 SQLite 连接。

## 自动检查冲突的联合对照

沿用上面的构建、`HD2MIMNT_RUN_LARGE_PERF=1` 和结果目录设置，分别在两个 `dotnet test` 进程中运行下列筛选（不能合为一次运行，静态图片缓存会影响后续样本）：

```powershell
dotnet test tests/HD2Mimnt.UITests/HD2Mimnt.UITests.csproj --no-build --no-restore -c Release "-p:OutputPath=$perfOutput/" --filter 'FullyQualifiedName~LargeLibrary_ConflictPatches_AutomaticCheckingDisabled'
dotnet test tests/HD2Mimnt.UITests/HD2Mimnt.UITests.csproj --no-build --no-restore -c Release "-p:OutputPath=$perfOutput/" --filter 'FullyQualifiedName~LargeLibrary_ConflictPatches_AutomaticCheckingEnabled'
```

两组均生成 10,000 张图片、10,000 个补丁及 1,000,000 条合成资源，当前配置全部启用；10% 的资源由相邻模组共享，共 50,000 组覆盖。合成 TOC 无游戏正文。两组执行相同的启停、搜索、排序、选择、刷新与滚动；开启组通过已保存的设置走实际自动调度，观察启动扫描和两次实际启停后的重检，在重检进行中滚动。SQL 负载覆盖全部操作。

输出为 `large-library-ui-auto-conflicts-off.json` 和 `large-library-ui-auto-conflicts-on.json`，保留扫描启动/完成次数、每轮完整扫描管线耗时（含读取、展示构建与 UI 回填）、扫描期间的输入探针与截图工作时间。扫描结果须有 50,000 组覆盖、零读取警告及全部卡片角标；最后静置 5 秒检查没有额外重扫。关闭组断言扫描次数为零。原始无补丁场景的数据不能直接作为这个样本的开关对照。

## 解释结果

`large-library-services.json` 为服务与 SQL 分布；`large-library-ui.json` 为操作、截图工作时间、输入调度延迟、进程内存和并发 SQL 记录。SQL 联合负载至少运行 100,000 条并延续至全部 UI 操作结束；查询错误会让测试失败。

服务测试 7 轮中的首轮与后续 6 轮分开；内存分配为累计分配，不是峰值。UI 截图工作时间不等于原生桌面 FPS；全场输入 P95 需要同时查看最大值和分阶段记录，避免漏掉长冻结。机器间性能不同，测试断言检查数据正确性、图片成功加载、虚拟化与清理，性能达标需按报告中的实测数据另行判断。

外部参考与依赖许可证见报告末尾；测试源码使用仓库 [LICENSE](../../LICENSE)，本轮没有增加外部依赖。
