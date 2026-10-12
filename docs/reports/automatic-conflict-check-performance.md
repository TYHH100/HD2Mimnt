# 自动检查冲突与性能评估

2026-10-12。设置入口为“设置 → 配置文件 → 自动检查冲突”，默认关闭，保存后生效。启用时检查正在查看的配置；配置切换、成员/启用/选项/顺序、部署方向或模组库变更后重检，不切换页面。

连续操作等待 750 ms 后合并为一次扫描；过期扫描取消并等待清理后再开始下一轮。手动检查取代排队中的自动检查，关闭设置取消自动任务但不打断手动检查；退出应用取消并等待扫描完成。不做持续轮询或整个模组库的后台文件监视。

分隔符的展开/收起、改名、颜色、添加和删除只改变显示，保存后刷新界面并保留冲突结果，不触发重检。拖动分组导致实际模组顺序改变时仍需重新检查。

## 实测

测试机器：Intel Core i5-12400F、Windows、.NET 10.0.12，Debug 构建。每个模组一个补丁，共 100 万条纹理 TOC 记录，10% 由相邻的两个模组共享。主数据长度为零；使用仓库接口替身，不计数据库、样本生成、真实游戏正文、Unit 版本随机读取或 UI 布局。JIT 已预热，文件刚生成且受操作系统缓存影响，首次解析不等于物理磁盘冷读取。每种规模单次测量，耗时会受磁盘、防病毒与系统负载影响。

| 模组数 × 每模组资源数 | 原首次 / 重复扫描 | 优化后首次 / 重复扫描 | 原重复扫描分配 | 优化后重复扫描分配 |
| --- | --- | --- | --- | --- |
| 1000 × 1000 | 1055.7 / 1166.4 ms | 902.2 / 671.3 ms | 659.2 MiB | 278.8 MiB |
| 5000 × 200 | 2149.3 / 2224.6 ms | 2545.9 / 1700.2 ms | 708.0 MiB | 305.2 MiB |

分配量是 `GC.GetTotalAllocatedBytes` 的每轮增量，不是峰值或保留内存。重复扫描时间降低约 24%～42%，分配降低约 57%；5000 模组的首次扫描没有提速，应避免把缓存收益表述成首次扫描全面提速。结果格式化及卡片关系索引的优化后耗时约 164～314 ms，在后台执行；这部分未包含在上表扫描耗时中。

## 已采用的优化与边界

- 每次批量读取 512 条索引，复用缓冲区；资源记录采用值类型。主文件仅读取索引及必要的 Unit 版本，不读取全部资源正文或伴随大文件。
- 唯一资源不分配持有者列表或展示对象，仅重复资源保留全部补丁来源，维持实际部署顺序和预期生效者的语义。
- 缓存完整解析成功的补丁索引，按完整路径、文件长度、修改时间、创建时间判断复用；约 64 MiB 的索引与条目预算，并最多保留 8192 个文件，LRU 淘汰。超大补丁仍可扫描但不入缓存。缓存不保存模组选项和部署顺序，每轮重新计算这些状态。
- 修改、损坏和删除文件的回归已覆盖。若外部工具同时保留长度、修改时间及创建时间来改写文件，元数据缓存不能保证识别这种变化；关闭应用后缓存消失。需要内容真实性证明时应采用完整哈希，而不是把元数据当作内容哈希。
- 相同关联模组集合共享卡片关系，悬停最多展示 8 个名称，点击角标仍可选择全部关联模组；冲突资源列表、单资源持有者列表及导航选择列表使用虚拟化。UI 回归使用 10000 条冲突与 2000 个持有者，确认只创建视口附近的容器并能滚动到末项。

目前没有足够证据支持引入 Zig：消除重复工作已能改善重检，5000 个文件的目录遍历和文件元数据查询仍然存在，换解析语言不会消除这些开销。若真实用户数据后续显示 Unit 版本随机读取、超出缓存容量或聚合 CPU 成为主要瓶颈，再分别采样和优化，保持当前可取消、完整结果及分层边界。

## 复现

先构建 `tests/HD2Mimnt.PerformanceTests/HD2Mimnt.PerformanceTests.csproj`，再运行：

```powershell
dotnet test tests/HD2Mimnt.PerformanceTests/HD2Mimnt.PerformanceTests.csproj --no-build --no-restore --filter FullyQualifiedName~ConflictScanPerformanceTests --logger "console;verbosity=detailed"
```

输出锁定时使用[测试指南](../agent-guides/testing-and-lifecycle.md)中的独立输出目录，构建和测试指向同一目录并顺序运行。样本目录由测试 `finally` 删除。

参考：[ArrayPool](https://learn.microsoft.com/dotnet/api/system.buffers.arraypool-1)、[CollectionsMarshal](https://learn.microsoft.com/dotnet/api/system.runtime.interopservices.collectionsmarshal.getvaluereforadddefault)、[Avalonia 虚拟化](https://docs.avaloniaui.net/docs/reference/controls/virtualizingstackpanel)、[GC 分配计数](https://learn.microsoft.com/dotnet/api/system.gc.gettotalallocatedbytes)。
