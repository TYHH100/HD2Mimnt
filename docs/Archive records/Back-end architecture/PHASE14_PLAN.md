# Phase 14: Analysis / Resource Explorer - 实现计划

## 日期
2026-10-02

## 概述

Phase 14 将实现 Mod 文件分析和资源浏览功能，允许用户深入查看 Mod 的内部结构、文件信息和依赖关系。

## 目标

根据开发计划 (Section 106)，Resource Explorer 提供：

```
Mod
 ↓
Resource Explorer
```

功能：
- 文件树浏览
- 文件信息和元数据
- Resource Metadata
- Hash 计算
- 引用关系分析
- 依赖关系检测
- 文件预览（可选）

**重要**: Resource Explorer 不负责专业编辑，只提供分析和浏览能力。

## 架构设计

### 核心组件

```
Core Layer
├── IResourceAnalyzer (接口)
├── IResourceExplorer (接口)
├── ResourceNode (实体)
├── ResourceMetadata (实体)
├── ResourceDependency (实体)
└── ResourceReference (实体)

Infrastructure Layer
├── ResourceAnalyzer (实现)
├── ResourceScanner (文件扫描)
├── HashCalculator (Hash 计算)
└── DependencyDetector (依赖检测)

Application Layer
├── ResourceExplorerService (主服务)
├── ResourceTreeBuilder (树构建)
└── ResourceMetadataExtractor (元数据提取)

UI Layer
├── ResourceExplorerViewModel
└── ResourceTreeView (文件树 UI)
```

### 数据模型

#### ResourceNode (文件树节点)
```csharp
public class ResourceNode
{
    public string Name { get; set; }
    public string Path { get; set; }
    public ResourceNodeType Type { get; set; } // File, Directory
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string Extension { get; set; }
    public List<ResourceNode> Children { get; set; }
    public ResourceMetadata? Metadata { get; set; }
}
```

#### ResourceMetadata (文件元数据)
```csharp
public class ResourceMetadata
{
    public string FilePath { get; set; }
    public string Hash { get; set; } // SHA256
    public string FileType { get; set; }
    public long Size { get; set; }
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
    public Dictionary<string, object> Properties { get; set; }
    public List<ResourceReference> References { get; set; }
}
```

#### ResourceReference (资源引用)
```csharp
public class ResourceReference
{
    public string SourceFile { get; set; }
    public string TargetFile { get; set; }
    public ReferenceType Type { get; set; } // Direct, Indirect
    public string Context { get; set; } // 引用上下文（如 JSON 字段名）
}
```

#### ResourceDependency (依赖关系)
```csharp
public class ResourceDependency
{
    public string ModId { get; set; }
    public string ResourcePath { get; set; }
    public List<string> DependsOn { get; set; }
    public List<string> RequiredBy { get; set; }
}
```

## 核心接口

### IResourceAnalyzer
```csharp
public interface IResourceAnalyzer
{
    /// <summary>
    /// 分析 Mod 目录，生成资源树
    /// </summary>
    Task<ResourceNode> AnalyzeAsync(string modPath, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 获取文件元数据
    /// </summary>
    Task<ResourceMetadata> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 计算文件 Hash
    /// </summary>
    Task<string> CalculateHashAsync(string filePath, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 检测文件引用关系
    /// </summary>
    Task<List<ResourceReference>> DetectReferencesAsync(string filePath, CancellationToken cancellationToken = default);
}
```

### IResourceExplorer
```csharp
public interface IResourceExplorer
{
    /// <summary>
    /// 获取 Mod 的资源树
    /// </summary>
    Task<ResourceNode> GetResourceTreeAsync(string modId, bool forceRefresh = false, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 搜索资源
    /// </summary>
    Task<List<ResourceNode>> SearchResourcesAsync(string modId, string query, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 获取文件依赖关系
    /// </summary>
    Task<ResourceDependency> GetDependenciesAsync(string modId, string resourcePath, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 验证资源完整性
    /// </summary>
    Task<List<string>> ValidateIntegrityAsync(string modId, CancellationToken cancellationToken = default);
}
```

## 实现步骤

### Step 1: Core 实体定义
- [ ] 创建 `ResourceNode.cs`
- [ ] 创建 `ResourceMetadata.cs`
- [ ] 创建 `ResourceReference.cs`
- [ ] 创建 `ResourceDependency.cs`
- [ ] 创建枚举 `ResourceNodeType`, `ReferenceType`

### Step 2: Core 接口定义
- [ ] 创建 `IResourceAnalyzer.cs`
- [ ] 创建 `IResourceExplorer.cs`

### Step 3: Infrastructure 实现
- [ ] 实现 `ResourceAnalyzer.cs`
  - 文件树扫描
  - Hash 计算（SHA256）
  - 元数据提取
- [ ] 实现 `DependencyDetector.cs`
  - JSON 文件引用检测
  - manifest.json 依赖分析
  - V1 特有文件关联检测

### Step 4: Application 服务层
- [ ] 实现 `ResourceExplorerService.cs`
  - 资源树缓存（避免重复扫描）
  - 搜索资源
  - 完整性验证
  - 依赖关系图

### Step 5: 测试
- [ ] `ResourceAnalyzerTests.cs`
  - 测试文件树构建
  - 测试 Hash 计算
  - 测试元数据提取
- [ ] `ResourceExplorerServiceTests.cs`
  - 测试资源树获取
  - 测试搜索功能
  - 测试依赖检测
  - 测试完整性验证

### Step 6: UI 集成（可选，暂不实现）
- 文件树视图
- 元数据面板
- 依赖关系图

## 文件类型识别

基于扩展名识别常见类型：

```csharp
".json" -> "JSON Configuration"
".patch_0" -> "V1 Patch File"
".gpu_resources" -> "V1 GPU Resources"
".stream" -> "V1 Stream Data"
".png", ".jpg", ".jpeg" -> "Image"
".txt", ".md" -> "Text Document"
".dll" -> "Library"
".exe" -> "Executable"
".lua" -> "Lua Script"
".xml" -> "XML Configuration"
```

## Hash 策略

- **小文件** (< 10MB): 直接计算完整 Hash
- **大文件** (≥ 10MB): 分块计算，支持进度报告
- 使用 SHA256 算法
- Hash 结果缓存到 `ResourceMetadata`

## 依赖检测策略

### JSON 文件
- 解析 JSON，查找可能的文件路径引用
- 检测 manifest.json 中的依赖字段
- 检测 options 中的文件引用

### V1 特有文件
- `.patch_0` 文件通常依赖对应的 `.stream` 文件
- `.gpu_resources` 文件可能依赖纹理文件
- 基于命名约定检测关联

### 示例
```json
{
  "texture": "textures/armor.png",
  "model": "models/character.mesh"
}
```
→ 检测到两个依赖：`textures/armor.png`, `models/character.mesh`

## 性能考虑

### 缓存策略
- 资源树缓存到内存（LRU）
- Hash 结果持久化（可选，未来可存储到数据库）
- 仅在文件修改时重新分析

### 异步操作
- 所有 I/O 操作异步
- 支持 CancellationToken
- 大型 Mod 分析使用 Task

### 进度报告
- 文件扫描进度
- Hash 计算进度
- 依赖检测进度

## 完整性验证

检查项：
1. **文件存在性**: manifest 中引用的文件是否存在
2. **Hash 一致性**: 如果有记录的 Hash，验证是否匹配
3. **依赖完整性**: 被引用的文件是否存在
4. **V1 文件配对**: `.patch_0` 是否有对应的 `.stream`

返回缺失或损坏文件的列表。

## 与现有系统集成

### Mod 实体扩展
不需要修改 Mod 实体，ResourceExplorer 独立运作。

### Workflow 集成
- Workflow 可以使用 ResourceAnalyzer 验证文件
- V1Workflow 可以查询特有文件依赖

### UI 集成点（未来）
- LibraryViewModel 添加 "Explore Resources" 按钮
- 点击后打开 ResourceExplorerView
- 显示文件树和元数据面板

## 不做的事情

根据开发计划：
- ❌ 不做专业文件编辑
- ❌ 不做 3D 模型预览（复杂度高）
- ❌ 不做音频播放
- ❌ 不做纹理编辑
- ❌ 不做脚本执行

## 测试场景

### 场景 1: 简单 Mod
```
TestMod/
├── manifest.json
├── README.md
└── data.txt
```
- 生成 3 节点树
- 计算所有文件 Hash
- 无依赖关系

### 场景 2: V1 Mod
```
V1Mod/
├── manifest.json
├── armor.patch_0
├── armor.stream
└── armor.gpu_resources
```
- 生成 4 节点树
- 识别 V1 特有文件
- 检测 `.patch_0` ↔ `.stream` 依赖

### 场景 3: 复杂嵌套
```
ComplexMod/
├── manifest.json
├── textures/
│   ├── armor.png
│   └── helmet.png
└── models/
    └── character.mesh
```
- 生成嵌套树
- 检测 manifest 中的文件引用
- 验证所有引用文件存在

## 成功标准

- ✅ 能够分析任意 Mod 目录，生成完整资源树
- ✅ 准确计算文件 Hash（SHA256）
- ✅ 检测 JSON 文件中的引用关系
- ✅ 识别 V1 特有文件及其关联
- ✅ 验证资源完整性，返回缺失文件列表
- ✅ 所有测试通过（至少 15 个测试）
- ✅ 性能：1000 文件的 Mod 分析 < 5 秒

## 预计工作量

- **实现**: 4-5 小时
- **测试**: 1-2 小时
- **文档**: 0.5 小时
- **总计**: 5.5-7.5 小时

## 下一步

完成 Phase 14 后，进入 **Phase 15: Integrated Armor Workflow**，这是最复杂的高级 Workflow。

---

**创建日期**: 2026-10-02  
**Phase**: 14  
**状态**: 📋 计划中  
**依赖**: Phase 12-13 (Workflow System)
