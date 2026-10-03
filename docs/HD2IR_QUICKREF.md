# HD2IR 嵌套压缩包兼容 - 快速参考

## 核心概念

### Logical Mod（逻辑模组）
用户管理的统一模组对象，可包含多个组件（HD2IR、模型、材质等）。

**示例：**
```
VRChat AIRI 原皮
├── HD2IR 组件     (来自 AIRI.hd2irpack.zip)
├── 模型组件       (来自 AIRI_Model.zip)
└── 材质组件       (来自 AIRI_Texture.zip)
```

### 四层架构

```
用户视角                                内部实现
─────────────────────────────────────────────────────
逻辑模组层      →  LogicalMod        (用户管理什么)
  ↓
组件层          →  LogicalModComponent (包含哪些组件)
  ↓
包源层          →  PackageSource      (组件来自哪个包)
  ↓
文件层          →  DeployableFile     (实际部署什么文件)
```

---

## 导入流程

### 1. 归档检查 (Archive Inspection)
```csharp
var tree = await archiveInspector.InspectAsync("VRChat_AIRI.zip", maxDepth: 5);
```
- 只读取 ZIP 中央目录，不完整解压
- 递归识别嵌套归档
- 深度限制：默认 5 层

### 2. 组件检测 (Component Detection)
```csharp
var candidates = await componentDetector.ExtractCandidatesAsync(tree);
```
检测到的组件类型：
- **HD2IR**: `.hd2irpack.zip`, `HD2IndependentRig/`, `.hd2phys`, `.rigbin`
- **Model**: `model/`, `.fbx`, `.obj`, `.dae`
- **Texture**: `texture/`, `.dds`, `.png`, `.tga`
- **Material**: `material/`, `.mat`, `.mtl`
- **Lua**: `.lua`, `script/`
- **Addon**: `addon/`
- **Appearance**: `appearance/`, `幻化`

### 3. 组件合并 (Component Merging)
```csharp
var mergeResult = await modMerger.MergeAsync(candidates);
```

**合并条件（按优先级）：**
1. 来自同一父容器
2. 名称具有公共前缀（>3 字符）
3. HD2IR + Model 组合模式
4. 所有组件检测置信度高

**合并决策：**
- `AutomaticMerge`: 高置信度，自动合并
- `ManualConfirmation`: 中低置信度，需要用户确认
- `SeparateImport`: 无法合并，分别导入

### 4. 保存逻辑模组
```csharp
await logicalModRepository.AddAsync(logicalMod);
```

---

## 使用示例

### 导入归档
```csharp
var importService = new ArchiveImportService(
    archiveInspector,
    componentDetector,
    modMerger,
    logicalModRepository,
    cacheRepository,
    logger);

var result = await importService.ImportArchiveAsync("path/to/archive.zip");

if (result.Success)
{
    Console.WriteLine($"成功导入 {result.ImportedLogicalMods.Count} 个逻辑模组");
}
else if (result.RequiresUserConfirmation)
{
    // 需要用户确认
    var confirmed = await importService.ConfirmMergeAsync(result, autoMerge: true);
}
```

### 查询逻辑模组
```csharp
// 获取所有逻辑模组
var allMods = await logicalModRepository.GetAllAsync();

// 按名称搜索
var mods = await logicalModRepository.SearchByNameAsync("AIRI");

// 获取详情
var mod = await logicalModRepository.GetByIdAsync(modId);
Console.WriteLine($"模组: {mod.Name}");
foreach (var component in mod.Components)
{
    Console.WriteLine($"  - {component.Type}: {component.Name}");
}
```

### 文件所有权跟踪
```csharp
// 记录文件所有权
var ownership = new FileOwnership
{
    LogicalModId = mod.Id,
    ComponentId = component.Id,
    PackageId = package.Id,
    DeployPath = "bin/HD2IndependentRig/character.rigbin",
    ContentHash = hash,
    TransactionId = transactionId,
    IsShared = false
};
await ownershipRepository.AddAsync(ownership);

// 查询文件所有者
var owner = await ownershipRepository.GetByDeployPathAsync("bin/HD2IndependentRig/character.rigbin");

// 删除模组时清理所有文件
var ownedFiles = await ownershipRepository.GetByLogicalModIdAsync(modId);
foreach (var file in ownedFiles)
{
    if (file.IsShared && file.ReferenceCount > 1)
    {
        // 共享文件，减少引用计数
        await ownershipRepository.UpdateReferenceCountAsync(file.DeployPath, -1);
    }
    else
    {
        // 删除文件
        File.Delete(file.DeployPath);
    }
}
```

---

## HD2IR 检测规则

### 检测优先级

| 优先级 | 特征 | 置信度 |
|--------|------|--------|
| **P1** | 文件扩展名 `.hd2irpack.zip` | Absolute |
| **P2** | 目录结构 `HD2IndependentRig/` 或 `bin/HD2IndependentRig/` | High |
| **P3** | 特征扩展名 `.hd2phys`, `.rigbin`, `.hd2pose`, `.hd2preview`, `.hd2shared` | High |
| **P4** | 配置文件 `difference.ini`, `isolation.ini` | Medium |

### 检测示例

**示例 1: 标准 HD2IR 包**
```
AIRI.hd2irpack.zip
└── bin/HD2IndependentRig/
    ├── character.hd2phys
    ├── character.rigbin
    └── character.hd2pose
```
**检测结果:** HD2IR, Confidence: Absolute

**示例 2: 嵌套 HD2IR**
```
VRChat_AIRI.zip
└── Resources.zip
    └── HD2IR.zip
        └── AIRI.hd2irpack.zip
```
**检测结果:** 在第三层检测到 HD2IR

---

## 配置选项

### ArchiveImportConfiguration
```csharp
var config = new ArchiveImportConfiguration
{
    // 最大嵌套深度（防止无限递归）
    MaxArchiveInspectionDepth = 5,
    
    // 启用分析缓存（避免重复扫描）
    EnableAnalysisCache = true,
    
    // 缓存过期时间（天）
    CacheExpirationDays = 30,
    
    // 高置信度自动合并
    AutoMergeHighConfidence = true,
    
    // 低置信度提示用户
    PromptForLowConfidenceMerge = true,
    
    // 快速指纹（仅首尾哈希）
    UseFastFingerprint = true,
    
    // 临时解压目录
    TempExtractionPath = Path.Combine(Path.GetTempPath(), "HD2Mimnt", "ArchiveTemp")
};
```

---

## 数据库架构

### 核心表

**LogicalMods** - 逻辑模组
```sql
CREATE TABLE LogicalMods (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Version TEXT,
    Author TEXT,
    Description TEXT,
    ManifestVersion INTEGER,
    CreatedAt TEXT,
    ModifiedAt TEXT,
    TotalSize INTEGER,
    ThumbnailPath TEXT,
    Tags TEXT,  -- JSON array
    Metadata TEXT  -- JSON object
);
```

**LogicalModComponents** - 组件
```sql
CREATE TABLE LogicalModComponents (
    Id TEXT PRIMARY KEY,
    LogicalModId TEXT NOT NULL,
    ComponentId TEXT NOT NULL,
    Type INTEGER NOT NULL,  -- ComponentType enum
    Name TEXT NOT NULL,
    SourcePackageId TEXT NOT NULL,
    Size INTEGER,
    Enabled INTEGER,
    Dependencies TEXT,  -- JSON array of Guid
    Metadata TEXT,
    FOREIGN KEY (LogicalModId) REFERENCES LogicalMods(Id) ON DELETE CASCADE
);
```

**PackageSources** - 包源
```sql
CREATE TABLE PackageSources (
    Id TEXT PRIMARY KEY,
    LogicalModId TEXT NOT NULL,
    Name TEXT NOT NULL,
    PhysicalPath TEXT NOT NULL,
    ArchiveType INTEGER NOT NULL,
    Size INTEGER,
    Hash TEXT,
    LastModified TEXT,
    ParentPackageId TEXT,  -- 嵌套关系
    Depth INTEGER,
    PathInParent TEXT,
    FOREIGN KEY (LogicalModId) REFERENCES LogicalMods(Id)
);
```

**FileOwnerships** - 文件所有权
```sql
CREATE TABLE FileOwnerships (
    Id TEXT PRIMARY KEY,
    LogicalModId TEXT NOT NULL,
    ComponentId TEXT NOT NULL,
    PackageId TEXT NOT NULL,
    DeployPath TEXT NOT NULL,
    ContentHash TEXT,
    TransactionId TEXT,
    DeployedAt TEXT,
    IsShared INTEGER,
    ReferenceCount INTEGER,
    FOREIGN KEY (LogicalModId) REFERENCES LogicalMods(Id)
);
```

---

## 测试场景

### 测试矩阵

**Case A: 标准双包**
```
Outer.zip
├── HD2IR.zip
└── Model.zip
```
预期: 1 Logical Mod, 2 Components ✅

**Case B: 三组件**
```
Outer.zip
├── HD2IR.zip
├── Model.zip
└── Texture.zip
```
预期: 1 Logical Mod, 3 Components ✅

**Case C: 嵌套**
```
Outer.zip
└── Resource.zip
    ├── HD2IR.zip
    └── Model.zip
```
预期: 1 Logical Mod, 2 Components ✅

**Case D: 深层嵌套（最大深度测试）**
```
A.zip → B.zip → C.zip → D.zip → HD2IR.zip
```
预期: 正常分析直到最大深度 ✅

**Case E: 超出深度**
```
A.zip → B.zip → C.zip → D.zip → E.zip → F.zip → HD2IR.zip (7层)
```
预期: 停止递归，记录 Warning，程序不崩溃 ⏸️ (需集成测试)

**Case F: 同名不同模组**
```
AIRI.zip
AIRI_Summer.zip
```
预期: 不静默合并 ✅

---

## 关键 API

### IArchiveInspector
```csharp
Task<ArchiveTree> InspectAsync(string archivePath, int maxDepth, CancellationToken ct);
bool IsArchive(string filePath);
Task<ArchiveType> GetArchiveTypeAsync(string filePath, CancellationToken ct);
Task<List<string>> ListEntriesAsync(string archivePath, CancellationToken ct);
```

### IHd2IrDetector
```csharp
Task<DetectionResult> DetectAsync(ArchiveNode node, CancellationToken ct);
Task<DetectionResult> DetectFromPathsAsync(IEnumerable<string> filePaths, CancellationToken ct);
IEnumerable<string> GetHd2IrPatterns();
```

### IComponentDetector
```csharp
Task<DetectionResult> DetectComponentAsync(ArchiveNode node, CancellationToken ct);
Task<List<ComponentCandidate>> ExtractCandidatesAsync(ArchiveTree tree, CancellationToken ct);
IEnumerable<string> GetPatternsForType(ComponentType type);
```

### IModMerger
```csharp
Task<MergeResult> MergeAsync(List<ComponentCandidate> candidates, CancellationToken ct);
bool ShouldMerge(List<ComponentCandidate> candidates);
DetectionConfidence CalculateMergeConfidence(List<ComponentCandidate> candidates);
```

---

## 性能优化

### 1. 延迟解压
- ❌ 不做: 发现 ZIP 就递归解压
- ✅ 实际: 只读取中央目录，按需提取

### 2. 分析缓存
```csharp
// 缓存键 = FilePath + FileSize + LastModified
var cache = await cacheRepository.GetAsync(path, size, modified);
if (cache != null)
{
    // 使用缓存的 ArchiveTree
    return JsonSerializer.Deserialize<ArchiveTree>(cache.ArchiveTreeJson);
}
```

### 3. 快速指纹
```csharp
// 不计算整个文件的 SHA256
// 只哈希首尾各 1KB
using var stream = File.OpenRead(filePath);
var buffer = new byte[1024];
stream.Read(buffer, 0, 1024);
if (stream.Length > 1024)
{
    stream.Seek(-1024, SeekOrigin.End);
    stream.Read(buffer, 0, 1024);
}
return SHA256.HashData(buffer);
```

### 4. 异步 I/O
- 所有 I/O 操作使用 `async/await`
- 支持 `CancellationToken`

---

## 错误处理

### 常见错误场景

1. **ZIP 损坏**
   - 捕获异常，记录错误
   - 不影响其他归档的分析

2. **密码 ZIP**
   - 检测到加密时跳过
   - 记录 Warning

3. **深度超限**
   - 停止递归
   - 设置 `DepthLimitExceeded = true`
   - 记录到 `Warnings` 列表

4. **磁盘空间不足**
   - 捕获 IOException
   - 清理临时文件
   - 返回错误信息

---

## 日志格式

### 导入日志示例
```
[20:10:01.002] Import     开始导入：VRchat_AIRI.zip
[20:10:01.021] Analyze    发现 3 个嵌套压缩包
[20:10:01.030] Detect     检测到 HD2IR Package：AIRI.hd2irpack.zip
[20:10:01.032] Detect     检测到 Model Package：AIRI_Model.zip
[20:10:01.034] Detect     检测到 Texture Package：AIRI_Texture.zip
[20:10:01.041] Merge      3 个组件候选属于同一 Logical Mod
[20:10:01.050] Merge      Logical Mod：VRChat AIRI
[20:10:01.055] Import     导入完成
```

---

## 扩展点

### 添加新的归档格式
```csharp
// 实现 IArchiveInspector
public class SevenZipInspector : IArchiveInspector
{
    public async Task<ArchiveTree> InspectAsync(string archivePath, int maxDepth, CancellationToken ct)
    {
        // 使用 SevenZipSharp 或其他库
    }
}
```

### 添加新的组件类型
```csharp
// 1. 在 ComponentType 枚举添加新类型
public enum ComponentType
{
    // ...
    CustomType  // 新增
}

// 2. 在 ComponentDetector 添加检测模式
private static readonly Dictionary<ComponentType, string[]> ComponentPatterns = new()
{
    // ...
    [ComponentType.CustomType] = new[] { "custom", ".custom", "customdir/" }
};
```

### 添加新的检测规则
```csharp
public class CustomDetector : IHd2IrDetector
{
    public async Task<DetectionResult> DetectAsync(ArchiveNode node, CancellationToken ct)
    {
        // 自定义检测逻辑
    }
}
```

---

## 下一步工作

### Phase 3 (P2) - Manifest & Ownership
- [ ] 标准化 Manifest JSON 格式
- [ ] Dependency Graph 实现
- [ ] 共享资源引用计数验证

### Phase 4 (P3) - Deployment Integration
- [ ] 与 DeploymentService 集成
- [ ] Diff Engine: 计算 Desired vs Current State
- [ ] Cleanup Engine: 使用 FileOwnership 精确清理
- [ ] Transaction Engine: 记录所有变更

### Phase 5 (P4) - Profile Integration
- [ ] Profile 选择 LogicalMod
- [ ] 配置切换只部署差异文件
- [ ] 批量部署/清理

---

## 参考资源

- **执行文档**: `docs/HD2Mimnt — HD2IR - 嵌套压缩包兼容专项执行文档.md`
- **Phase 1 完成报告**: `docs/PHASE_P0_COMPLETE.md`
- **项目总览**: `CLAUDE.md`
- **测试**: `tests/HD2Mimnt.Tests/Archive/`
