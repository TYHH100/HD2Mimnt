# HD2Mimnt UI - 阶段 2 服务接口审查报告

**审查时间**: 2026-10-03  
**审查范围**: ModService, SearchService, 相关实体

---

## 一、ModService 审查结果

### 1.1 可用接口 ✅

```csharp
// CRUD 操作
Task<Mod?> GetModByIdAsync(Guid id, CancellationToken cancellationToken = default)
Task<IEnumerable<Mod>> GetAllModsAsync(CancellationToken cancellationToken = default)
Task<bool> DeleteModAsync(Guid id, CancellationToken cancellationToken = default)

// 导入操作
Task<Guid> ImportModAsync(string sourcePath, CancellationToken cancellationToken = default)
Task<IEnumerable<Guid>> ImportModsAsync(IEnumerable<string> sourcePaths, CancellationToken cancellationToken = default)

// 搜索和筛选
Task<IEnumerable<Mod>> SearchModsAsync(string query, CancellationToken cancellationToken = default)
Task<IEnumerable<Mod>> GetModsByCategoryAsync(ModCategory category, CancellationToken cancellationToken = default)
Task<IEnumerable<Mod>> GetModsByStatusAsync(ModStatus status, CancellationToken cancellationToken = default)
Task<IEnumerable<Mod>> GetModsByTagAsync(string tag, CancellationToken cancellationToken = default)

// 元数据更新
Task UpdateModMetadataAsync(Guid id, string? name, string? description, string? author, string? version, ModCategory? category, CancellationToken cancellationToken)

// 标签操作
Task AddTagAsync(Guid modId, string tag, CancellationToken cancellationToken = default)
Task RemoveTagAsync(Guid modId, string tag, CancellationToken cancellationToken = default)
```

### 1.2 缺失接口 ⚠️

**需要扩展的方法**:
```csharp
// 批量操作（代码中未发现）
Task<int> BatchDeleteAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)

// 统计信息（LibraryViewModel 引用但未找到实现）
Task<ModStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)

// 标签列表（LibraryViewModel 引用但未找到实现）
Task<IEnumerable<string>> GetAllTagsAsync(CancellationToken cancellationToken = default)

// 启用/禁用状态（需要与 ProfileMod 关联）
// 注意：IsEnabled 和 IsDeployed 应该在 ProfileMod 中，不是 Mod 实体本身
```

### 1.3 建议 💡

1. **ModService 基础功能完整**，可以直接使用
2. **需要补充统计和批量操作方法**
3. **启用/禁用状态应该通过 ProfileService 管理**，不在 ModService 中

---

## 二、SearchService 审查结果 ✅

### 2.1 核心功能

```csharp
// 构建搜索索引
Task BuildIndexAsync(CancellationToken cancellationToken = default)

// 搜索
Task<IEnumerable<Mod>> SearchAsync(string query, CancellationToken cancellationToken = default)

// 索引更新
void UpdateModIndex(Mod mod)
void RemoveModFromIndex(Guid modId)

// 统计信息
SearchIndexStats GetIndexStats()
```

### 2.2 特性

- ✅ 内存索引（高性能）
- ✅ 支持精准搜索、前缀搜索、模糊搜索
- ✅ 支持标签搜索 (`tag:xxx`)
- ✅ 优先级排序（精准 > 前缀 > 标准化 > 模糊）
- ✅ 索引增量更新
- ✅ 线程安全（lock）

### 2.3 性能目标

- 目标：10,000 Mods 达到毫秒级响应
- 索引构建：异步执行
- 搜索响应：< 100ms

### 2.4 建议 💡

**SearchService 功能完整，无需修改**，可直接集成到 UI。

---

## 三、Mod 实体审查结果 ✅

### 3.1 核心字段

```csharp
public class Mod
{
    // 基础信息
    Guid Id
    string Name
    string Version
    string Author
    string Description
    string Path
    
    // 分类和标签
    ModCategory Category
    List<string> Tags
    
    // 工作流
    string WorkflowType
    ModManifest? Manifest
    
    // 时间戳
    DateTime ImportedAt
    DateTime ModifiedAt
    DateTime CreatedAt
    
    // 文件信息
    long Size
    string Hash
    string ThumbnailPath
    
    // 来源
    ModSource Source
    ModStatus Status
    
    // 安全
    bool ContainsScripts
    
    // 平台信息
    int? NexusModId
    string? PlatformId
    string? PlatformModId
    string? PlatformVersion
    DateTime? LastUpdateCheck
}
```

### 3.2 关键发现 ⚠️

**Mod 实体不包含启用/部署状态**：
- ❌ 没有 `IsEnabled` 字段
- ❌ 没有 `IsDeployed` 字段

这些状态应该在 **ProfileMod** 实体中管理，因为：
- 同一个 Mod 在不同配置文件中有不同的启用状态
- 部署状态也与当前激活的配置文件相关

### 3.3 建议 💡

需要扩展 ModViewModel 以包含：
- `bool IsEnabled` - 从当前 ProfileMod 获取
- `bool IsDeployed` - 从部署记录获取
- `int Order` - 在配置文件中的排序

---

## 四、ProfileService 审查需求

### 4.1 需要确认的接口

```csharp
// 获取当前激活的配置文件
Task<Profile?> GetActiveProfileAsync()

// 获取配置文件中的模组
Task<IEnumerable<ProfileMod>> GetProfileModsAsync(Guid profileId)

// 启用/禁用模组
Task SetModEnabledAsync(Guid profileId, Guid modId, bool enabled)

// 批量启用/禁用
Task BatchSetModEnabledAsync(Guid profileId, IEnumerable<Guid> modIds, bool enabled)
```

### 4.2 ProfileMod 实体结构

需要确认是否包含：
```csharp
public class ProfileMod
{
    Guid Id
    Guid ProfileId
    Guid ModId
    bool Enabled
    int Order
}
```

---

## 五、DeploymentService 审查需求

### 5.1 需要的接口

```csharp
// 检查模组部署状态
Task<bool> IsModDeployedAsync(Guid modId)

// 获取部署记录
Task<DeploymentRecord?> GetLatestDeploymentAsync()
```

---

## 六、缺失服务和扩展需求

### 6.1 需要扩展 ModService

**新增方法**:
```csharp
public async Task<ModStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
{
    var allMods = await GetAllModsAsync(cancellationToken);
    return new ModStatistics
    {
        TotalCount = allMods.Count(),
        // ... 其他统计
    };
}

public async Task<IEnumerable<string>> GetAllTagsAsync(CancellationToken cancellationToken = default)
{
    var allMods = await GetAllModsAsync(cancellationToken);
    return allMods.SelectMany(m => m.Tags).Distinct().OrderBy(t => t);
}
```

### 6.2 需要创建 ModStatistics 类

```csharp
public class ModStatistics
{
    public int TotalCount { get; set; }
    public int EnabledCount { get; set; }
    public int DisabledCount { get; set; }
    public int DeployedCount { get; set; }
    public long TotalSize { get; set; }
    public Dictionary<ModCategory, int> ByCategory { get; set; } = new();
}
```

---

## 七、执行建议

### 7.1 立即执行（优先级 P0）

1. ✅ **直接使用现有 ModService** - 基础功能完整
2. ✅ **直接集成 SearchService** - 无需修改
3. ⚠️ **扩展 ModService** - 添加 `GetStatisticsAsync` 和 `GetAllTagsAsync`
4. ⚠️ **扩展 ModViewModel** - 添加 `IsEnabled`, `IsDeployed`, `IsSelected` 属性
5. ⚠️ **重构 LibraryViewModel** - 实现完整的阶段 2 功能

### 7.2 后续任务（优先级 P1）

1. 确认 ProfileService 接口
2. 确认 DeploymentService 接口
3. 实现启用/禁用逻辑（需要 ProfileService 支持）
4. 实现部署状态查询（需要 DeploymentService 支持）

---

## 八、数据流设计

### 8.1 模组列表加载流程

```
LibraryViewModel.InitializeAsync()
  ↓
1. ModService.GetAllModsAsync() → List<Mod>
  ↓
2. ProfileService.GetActiveProfileAsync() → Profile
  ↓
3. ProfileService.GetProfileModsAsync(profileId) → List<ProfileMod>
  ↓
4. DeploymentService.GetLatestDeploymentAsync() → DeploymentRecord
  ↓
5. 合并数据 → List<ModItemViewModel>
     - Mod 基础信息
     - ProfileMod.Enabled → IsEnabled
     - DeploymentRecord.Files → IsDeployed
```

### 8.2 搜索流程

```
用户输入
  ↓
防抖 (300ms)
  ↓
SearchService.SearchAsync(query)
  ↓
过滤结果 (应用 Filter/Sort)
  ↓
更新 FilteredMods
```

### 8.3 启用/禁用流程

```
用户点击启用
  ↓
ProfileService.SetModEnabledAsync(profileId, modId, true)
  ↓
更新 ModItemViewModel.IsEnabled
  ↓
更新统计计数
```

---

## 九、总结

### 9.1 服务状态

| 服务 | 状态 | 可用性 |
|-----|------|--------|
| ModService | ✅ 基础完整 | 90% |
| SearchService | ✅ 功能完整 | 100% |
| ProfileService | ❓ 待确认 | - |
| DeploymentService | ❓ 待确认 | - |

### 9.2 下一步

1. ✅ **立即开始扩展 ModService**（添加缺失方法）
2. ✅ **立即开始重构 LibraryViewModel**（完整实现阶段 2）
3. ⏭️ **稍后确认 ProfileService 和 DeploymentService**

---

**状态**: ✅ 审查完成  
**结论**: 可以开始阶段 2 开发，部分功能需要后续补充
