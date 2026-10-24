# HD2IR Phase 6+ (Advanced Features) 实施计划

**计划日期：** 2026-10-03  
**计划人员：** Claude (Opus 5)  
**执行文档：** `docs/HD2Mimnt — HD2IR - 嵌套压缩包兼容专项执行文档.md`  
**优先级：** P5 (可选增强功能)  
**状态：** 📋 待启动

---

## 一、Phase 6+ 定位

### 1.1 与 Phase 1-5 的关系

**Phase 1-5 已完成：**
- ✅ 核心架构（四层架构）
- ✅ 基础导入和检测
- ✅ 统一部署引擎
- ✅ 差异驱动优化
- ✅ Profile 集成
- ✅ 性能优化验证
- ✅ 24/24 验收标准

**Phase 6+ 的作用：**
- 扩展兼容性（更多异常格式）
- 增强鲁棒性（边界情况处理）
- 提升用户体验（UI 集成）
- 验证实际性能（真实大文件测试）

### 1.2 可选性说明

根据执行文档第 2283-2287 节，Phase 6+ 被标记为 **P5 优先级**，属于可选增强功能。

**核心功能已完成：**
- Phase 1-5 已实现所有核心功能
- 24/24 验收标准全部通过
- 架构设计优秀（9.5/10）
- 测试覆盖完整（71/71）

**Phase 6+ 建议在以下情况下实施：**
1. 遇到实际用户场景需要更多格式兼容
2. 收集到真实的复杂 HD2IR 样本
3. 需要优化用户体验（UI 层）
4. 需要验证实际性能（真实大文件）

---

## 二、Phase 6+ 范围

根据执行文档第 2283-2287 节，Phase 6+ 包含以下内容：

### 2.1 复杂异常兼容

**目标：** 处理更多现实世界的异常打包格式

**潜在场景：**
1. **不规则命名**
   ```text
   VRchat_AIRI.zip
   └── 新建文件夹 (2).zip
       └── 备份.zip
           └── AIRI.hd2irpack.zip
   ```

2. **混合编码**
   ```text
   文件名同时包含 UTF-8, GBK, Shift-JIS
   ```

3. **损坏的 ZIP**
   ```text
   部分 Entry 损坏但其他 Entry 可用
   ```

4. **超大单文件**
   ```text
   单个 .model 文件 > 2GB
   ```

5. **特殊文件名**
   ```text
   包含特殊字符：空格、emoji、制表符
   ```

### 2.2 更多作者打包方式

**目标：** 扩展对不同作者打包习惯的支持

**潜在格式：**
1. **7z / RAR / TAR.GZ**
   ```text
   目前只支持 ZIP，可能需要支持其他格式
   ```

2. **分卷压缩**
   ```text
   AIRI.part1.zip
   AIRI.part2.zip
   AIRI.part3.zip
   ```

3. **加密 ZIP**
   ```text
   密码保护的 ZIP 文件
   ```

4. **软链接 / 硬链接**
   ```text
   ZIP 内部包含符号链接
   ```

### 2.3 更多组件类型

**目标：** 扩展组件检测类型

**当前支持：**
- ✅ HD2IR (hd2ir.json)
- ✅ Model (.model)
- ✅ Texture (DDS, PNG)
- ✅ Audio (.wem, .bnk)

**潜在扩展：**
1. **Script 组件**
   ```text
   .lua / .py / .js 脚本文件
   ```

2. **Config 组件**
   ```text
   配置文件、参数文件
   ```

3. **Animation 组件**
   ```text
   动画文件
   ```

4. **Shader 组件**
   ```text
   着色器文件
   ```

5. **Font 组件**
   ```text
   字体文件
   ```

### 2.4 实际大文件性能测试

**目标：** 验证 Phase 5 的理论性能预测

**测试矩阵：**

| 测试场景 | 文件大小 | 目标耗时 | 验证项 |
|---------|---------|---------|-------|
| 基准测试 | 100MB | < 5s | 分析、Hash、部署 |
| 中型文件 | 500MB | < 20s | 分析、Hash、部署 |
| 大型文件 | 1GB | < 60s | 分析、Hash、部署 |
| 嵌套深度 | 任意 | +20% | 5 层嵌套 |
| 大量小文件 | 任意 | - | 1000+ 文件 |
| 内存峰值 | 任意 | < 1GB | 内存监控 |
| CPU 峰值 | 任意 | < 80% | CPU 监控 |

**测试文件准备：**
1. 创建真实大小的测试 ZIP
2. 使用真实的 .model 和 .dds 文件
3. 构建多层嵌套结构
4. 生成大量小文件场景

**测试工具：**
- BenchmarkDotNet（已有）
- 内存分析器（dotMemory / PerfView）
- CPU 分析器（dotTrace / PerfView）

### 2.5 UI 层 LogicalMod 管理界面

**目标：** 在 UI 层集成 LogicalMod 管理功能

**功能需求：**
1. **LogicalMod 列表展示**
   - 显示所有 LogicalMod
   - 显示组件数量
   - 显示来源包数量

2. **LogicalMod 详情查看**
   - 查看组件列表
   - 查看来源包列表
   - 查看可部署文件列表

3. **LogicalMod 导入**
   - 导入 ZIP 文件
   - 分析进度展示
   - 组件检测结果展示

4. **LogicalMod 编辑**
   - 启用/禁用组件
   - 调整组件顺序
   - 组件配置覆盖

5. **LogicalMod 删除**
   - 删除确认对话框
   - 级联删除提示
   - 引用检查

6. **LogicalMod 在 Profile 中的集成**
   - Profile 中添加 LogicalMod
   - 调整 LogicalMod 顺序
   - 启用/禁用 LogicalMod

---

## 三、Phase 6+ 实施策略

### 3.1 分阶段实施

**建议分为多个子阶段：**

| 子阶段 | 名称 | 优先级 | 预估时间 |
|-------|------|--------|---------|
| Phase 6.1 | 实际性能测试 | 高 | 4-6 小时 |
| Phase 6.2 | UI 层集成 | 高 | 8-12 小时 |
| Phase 6.3 | 异常格式兼容 | 中 | 6-8 小时 |
| Phase 6.4 | 更多组件类型 | 低 | 4-6 小时 |
| Phase 6.5 | 更多归档格式 | 低 | 6-8 小时 |

### 3.2 优先级建议

**高优先级（建议优先实施）：**
1. **Phase 6.1 - 实际性能测试**
   - 验证 Phase 5 的理论预测
   - 识别实际性能瓶颈
   - 建立性能基线

2. **Phase 6.2 - UI 层集成**
   - 让用户可以使用 LogicalMod 功能
   - 提升用户体验
   - 完整的端到端功能

**中优先级（根据需求实施）：**
3. **Phase 6.3 - 异常格式兼容**
   - 当遇到实际用户反馈时实施
   - 根据真实样本逐步扩展

**低优先级（可延后）：**
4. **Phase 6.4 - 更多组件类型**
   - 当有明确需求时实施
   - 架构已支持扩展

5. **Phase 6.5 - 更多归档格式**
   - 当有明确需求时实施
   - 可作为 Plugin 实现

### 3.3 实施原则

根据执行文档第 2289-2294 节：

> **先建立可扩展检测框架**  
> **不要把未知规则硬编码成永久规范**

**原则 1：可扩展性优先**
- 使用 Plugin 机制扩展组件检测
- 使用 Strategy 模式扩展归档格式
- 使用 Registry 模式管理扩展

**原则 2：数据驱动**
- 检测规则使用配置文件
- 不要硬编码文件扩展名
- 不要硬编码路径匹配规则

**原则 3：渐进式增强**
- 每个子阶段独立可验证
- 不破坏现有功能
- 向后兼容

---

## 四、Phase 6.1 - 实际性能测试（详细计划）

### 4.1 测试文件准备

**任务 1：创建测试文件生成器**

```csharp
// TestFileGenerator.cs
public class TestFileGenerator
{
    // 生成指定大小的 ZIP 文件
    public static void GenerateTestZip(string outputPath, long sizeInMB);
    
    // 生成嵌套 ZIP 文件
    public static void GenerateNestedZip(string outputPath, int depth);
    
    // 生成大量小文件 ZIP
    public static void GenerateManySmallFilesZip(string outputPath, int fileCount);
    
    // 生成混合场景 ZIP
    public static void GenerateMixedZip(string outputPath, TestScenario scenario);
}
```

**任务 2：准备测试数据集**

| 测试文件 | 大小 | 结构 | 用途 |
|---------|------|------|------|
| test_100mb.zip | 100MB | 单层 | 基准测试 |
| test_500mb.zip | 500MB | 单层 | 中型文件 |
| test_1gb.zip | 1GB | 单层 | 大型文件 |
| test_nested_5.zip | 100MB | 5 层嵌套 | 嵌套深度 |
| test_1000_files.zip | 50MB | 1000 个小文件 | 大量文件 |
| test_realistic.zip | 200MB | 混合结构 | 真实场景 |

### 4.2 基准测试实施

**测试框架：** BenchmarkDotNet

**测试套件：**

```csharp
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ArchivePerformanceBenchmarks
{
    [Benchmark]
    [Arguments("test_100mb.zip")]
    [Arguments("test_500mb.zip")]
    [Arguments("test_1gb.zip")]
    public async Task ImportArchive(string filePath)
    {
        // 完整导入流程
        var result = await _archiveImportService.ImportAsync(filePath);
    }
    
    [Benchmark]
    public async Task AnalyzeArchive()
    {
        // 仅分析阶段
    }
    
    [Benchmark]
    public async Task DetectComponents()
    {
        // 仅检测阶段
    }
    
    [Benchmark]
    public async Task CalculateDiff()
    {
        // 仅 Diff 计算
    }
    
    [Benchmark]
    public async Task DeployFiles()
    {
        // 仅部署阶段
    }
}
```

### 4.3 性能指标记录

**记录指标：**
1. 总耗时
2. 分阶段耗时（分析、检测、构建、部署）
3. 内存峰值
4. CPU 使用率
5. I/O 吞吐量
6. 线程数量

**产出报告：**
- HD2IR_PHASE6.1_PERFORMANCE_RESULTS.md
- 性能对比图表
- 瓶颈分析
- 优化建议

### 4.4 验证标准

**成功标准：**
- 100MB ZIP < 5s ✅
- 500MB ZIP < 20s ✅
- 1GB ZIP < 60s ✅
- 内存峰值 < 1GB ✅
- CPU 峰值 < 80% ✅

**如果不满足：**
- 识别瓶颈
- 提出优化方案
- 实施优化
- 重新测试

---

## 五、Phase 6.2 - UI 层集成（详细计划）

### 5.1 UI 架构设计

**新增 ViewModel：**
1. **LogicalModLibraryViewModel**
   - 管理 LogicalMod 列表
   - 搜索和过滤
   - 排序

2. **LogicalModDetailViewModel**
   - 显示 LogicalMod 详情
   - 组件列表
   - 来源包列表
   - 可部署文件列表

3. **LogicalModImportViewModel**
   - 导入向导
   - 进度展示
   - 结果预览

**新增 View：**
1. **LogicalModLibraryView.axaml**
   - LogicalMod 列表界面

2. **LogicalModDetailView.axaml**
   - LogicalMod 详情界面

3. **LogicalModImportDialog.axaml**
   - 导入对话框

### 5.2 集成到 MainWindow

**导航结构：**
```
MainWindow
├── Library (传统 Mod)
├── LogicalMods (NEW)  ← 新增
├── Profiles
├── Tasks
└── Settings
```

**Profile 界面扩展：**
```
ProfileView
├── Traditional Mods 列表
└── Logical Mods 列表 (NEW)  ← 新增
```

### 5.3 用户体验设计

**导入流程：**
1. 用户点击 "Import LogicalMod"
2. 选择 ZIP 文件
3. 显示分析进度
4. 显示检测结果预览
5. 用户确认或调整
6. 完成导入

**管理流程：**
1. 在 LogicalMods 界面查看所有 LogicalMod
2. 点击查看详情
3. 启用/禁用组件
4. 删除 LogicalMod

**部署流程：**
1. 在 Profile 界面添加 LogicalMod
2. 调整顺序
3. 启用/禁用
4. 部署 Profile

### 5.4 实施任务清单

**Task 1：创建 ViewModel**
- [ ] LogicalModLibraryViewModel.cs
- [ ] LogicalModDetailViewModel.cs
- [ ] LogicalModImportViewModel.cs

**Task 2：创建 View**
- [ ] LogicalModLibraryView.axaml
- [ ] LogicalModDetailView.axaml
- [ ] LogicalModImportDialog.axaml

**Task 3：集成到 MainWindow**
- [ ] 添加导航项
- [ ] 路由配置

**Task 4：扩展 ProfileView**
- [ ] 添加 LogicalMods 列表
- [ ] 添加/删除/排序功能

**Task 5：测试**
- [ ] 单元测试
- [ ] 集成测试
- [ ] UI 测试

---

## 六、Phase 6.3-6.5 概要计划

### 6.1 Phase 6.3 - 异常格式兼容

**实施方式：** 按需实施，根据真实用户反馈

**扩展点：**
- ComponentDetector 规则扩展
- ArchiveAnalysisEngine 异常处理
- 编码检测和转换

### 6.2 Phase 6.4 - 更多组件类型

**实施方式：** Plugin 机制扩展

**扩展点：**
- IComponentDetectorPlugin 接口
- ComponentType 枚举扩展
- DeploymentRule 扩展

### 6.3 Phase 6.5 - 更多归档格式

**实施方式：** Strategy 模式扩展

**扩展点：**
- IArchiveReader 接口
- 7z / RAR / TAR.GZ 支持
- 分卷压缩支持

---

## 七、风险评估

### 7.1 技术风险

| 风险 | 影响 | 概率 | 缓解措施 |
|-----|------|------|---------|
| 性能不达标 | 高 | 中 | 提前识别瓶颈，逐步优化 |
| UI 复杂度高 | 中 | 中 | 分阶段实施，先核心后增强 |
| 兼容性问题 | 中 | 低 | 充分测试，向后兼容 |

### 7.2 资源风险

| 风险 | 影响 | 概率 | 缓解措施 |
|-----|------|------|---------|
| 时间不足 | 中 | 中 | 优先高价值功能 |
| 测试数据不足 | 低 | 中 | 生成合成数据 |

---

## 八、预期产出

### 8.1 Phase 6.1 产出
- HD2IR_PHASE6.1_PERFORMANCE_RESULTS.md
- 性能测试报告
- 优化建议

### 8.2 Phase 6.2 产出
- 完整的 UI 集成
- 用户文档
- 操作视频（可选）

### 8.3 Phase 6.3-6.5 产出
- 扩展功能文档
- 插件开发指南
- 兼容性矩阵

---

## 九、下一步行动

### 建议的启动顺序

**Option A：从性能测试开始（推荐）**
1. Phase 6.1 - 实际性能测试
2. Phase 6.2 - UI 层集成
3. Phase 6.3-6.5 - 按需实施

**Option B：从 UI 集成开始**
1. Phase 6.2 - UI 层集成
2. Phase 6.1 - 实际性能测试
3. Phase 6.3-6.5 - 按需实施

**Option C：并行推进**
1. Phase 6.1 和 6.2 同步进行
2. Phase 6.3-6.5 根据反馈实施

### 启动 Phase 6.1 的前置条件

- [x] Phase 1-5 完成
- [x] 所有验收标准通过
- [x] 文档体系完整
- [ ] 确认启动 Phase 6+
- [ ] 准备测试环境
- [ ] 准备测试数据

---

**计划人员：** Claude (Opus 5)  
**计划日期：** 2026-10-03  
**计划版本：** 1.0  
**状态：** 📋 待启动，等待用户确认是否继续
