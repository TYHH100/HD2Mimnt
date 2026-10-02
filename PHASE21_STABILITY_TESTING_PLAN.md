# Phase 21: 长时间稳定性测试 + Regression - 实施计划

**开始日期**: 2026-10-03
**状态**: 📋 待开始

---

## Phase 21 目标

根据 `HD2Mimnt v2 总体开发计划.md`，Phase 21 要求：

### 长时间稳定性测试

运行时长：
- 1 Hour
- 4 Hours
- 8 Hours

持续执行的操作：
- Search
- Task
- Image Loading
- Import
- Background Scan

检查项目：
- Memory Leak（内存泄漏）
- Handle Leak（句柄泄漏）
- Thread Leak（线程泄漏）
- UI Lag（UI 卡顿）
- SQLite Lock（数据库锁）
- Cache Leak（缓存泄漏）

### Regression 测试

确保之前实现的功能没有退化。

---

## 实施策略

### 1. 创建稳定性测试项目

```
tests/HD2Mimnt.StabilityTests/
├── HD2Mimnt.StabilityTests.csproj
├── LongRunningTests.cs
├── MemoryLeakTests.cs
├── ResourceLeakTests.cs
├── RegressionTests.cs
└── StabilityTestRunner.cs
```

### 2. 长时间测试场景

#### 场景 1: 持续搜索测试 (1h/4h/8h)
- 每秒执行搜索操作
- 随机搜索关键词
- 监控内存使用
- 监控响应时间

#### 场景 2: 持续任务执行 (1h/4h/8h)
- 持续创建任务
- 更新任务状态
- 查询任务历史
- 监控 Worker 进程稳定性

#### 场景 3: 持续图片加载 (1h/4h/8h)
- 持续加载缩略图
- 模拟滚动场景
- 监控图片缓存
- 检查内存释放

#### 场景 4: 持续导入操作 (4h/8h)
- 模拟 Mod 导入
- 检查文件句柄
- 监控磁盘 I/O
- 验证数据完整性

#### 场景 5: 综合压力测试 (8h)
- 同时执行上述所有操作
- 模拟真实用户行为
- 监控整体系统稳定性

### 3. 资源监控指标

```csharp
public class ResourceMonitor
{
    // 内存
    long WorkingSet;
    long PrivateMemorySize;
    long GCTotalMemory;
    
    // 句柄
    int HandleCount;
    
    // 线程
    int ThreadCount;
    
    // 性能
    TimeSpan ResponseTime;
    double CpuUsage;
    
    // SQLite
    int ActiveConnections;
    int LockWaitCount;
}
```

### 4. 泄漏检测

#### 内存泄漏检测
- 记录测试开始时的内存基线
- 每小时采样内存使用
- 分析内存增长趋势
- 使用 dotMemory 或 perfview 分析

#### 句柄泄漏检测
- 监控文件句柄数
- 监控数据库连接数
- 检查未释放的资源

#### 线程泄漏检测
- 监控线程池状态
- 检查未关闭的 Task
- 验证 Worker 进程回收

### 5. Regression 测试套件

创建回归测试，覆盖所有已实现的功能：

```
RegressionTests/
├── ModLibraryRegressionTests.cs
├── ProfileRegressionTests.cs
├── DeploymentRegressionTests.cs
├── CleanupRegressionTests.cs
├── WorkerRegressionTests.cs
├── PluginRegressionTests.cs
├── ExternalToolsRegressionTests.cs
├── AIIntegrationRegressionTests.cs
└── SearchRegressionTests.cs
```

---

## 实施步骤

### Step 1: 创建测试基础设施 ✅
- [ ] 创建 HD2Mimnt.StabilityTests 项目
- [ ] 实现 ResourceMonitor 工具
- [ ] 实现 StabilityTestRunner
- [ ] 配置测试环境

### Step 2: 实现短期稳定性测试 (1 Hour)
- [ ] SearchStabilityTest (1h)
- [ ] TaskStabilityTest (1h)
- [ ] ImageLoadingStabilityTest (1h)
- [ ] 运行并收集数据

### Step 3: 实现中期稳定性测试 (4 Hours)
- [ ] 扩展到 4 小时测试
- [ ] 添加 Import 操作
- [ ] 综合场景测试
- [ ] 运行并分析结果

### Step 4: 实现长期稳定性测试 (8 Hours)
- [ ] 扩展到 8 小时测试
- [ ] 全场景压力测试
- [ ] 运行过夜测试
- [ ] 完整泄漏分析

### Step 5: Regression 测试
- [ ] 创建回归测试套件
- [ ] 覆盖所有已实现功能
- [ ] 建立自动化 CI 流程
- [ ] 记录基线性能

### Step 6: 修复发现的问题
- [ ] 修复内存泄漏
- [ ] 修复资源泄漏
- [ ] 优化性能瓶颈
- [ ] 重新验证

---

## 成功标准

### Phase 21 完成标准

1. ✅ 1 小时稳定性测试通过（无崩溃、无明显泄漏）
2. ✅ 4 小时稳定性测试通过
3. ✅ 8 小时稳定性测试通过
4. ✅ 内存增长率 < 10% per hour
5. ✅ 无句柄泄漏
6. ✅ 无线程泄漏
7. ✅ UI 始终可响应
8. ✅ SQLite 无死锁
9. ✅ 回归测试套件建立
10. ✅ 所有 Regression 测试通过

---

## 预期时间

- 测试基础设施: 2-3 小时
- 1h 测试实施 + 运行: 4-5 小时
- 4h 测试实施 + 运行: 8-10 小时
- 8h 测试实施 + 运行: 16-20 小时
- Regression 套件: 4-6 小时
- 问题修复: 视发现的问题而定

**总计**: 约 2-3 天（包含过夜测试）

---

## 当前 Phase 20 遗留问题

在进入 Phase 21 前，建议先修复 Phase 20 发现的问题：

### 🔴 高优先级（建议修复）
1. Worker 并发写入 SQLite 锁问题
2. SQLite SearchByName 性能优化

这些问题可能影响 Phase 21 的长时间稳定性测试。

---

## 下一步行动

**选项 1**: 先修复 Phase 20 问题（推荐）
- 修复 Worker 并发锁
- 优化 SearchByName
- 然后进入 Phase 21

**选项 2**: 直接进入 Phase 21
- 开始实施稳定性测试
- 在测试中可能会再次遇到 Phase 20 的问题

---

**文档创建时间**: 2026-10-03
**状态**: 待用户确认后开始实施
