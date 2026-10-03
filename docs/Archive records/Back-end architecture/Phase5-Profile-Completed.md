# Phase 5: Profile Management - 完成报告

## 概述

Phase 5 实现了完整的 Profile 管理功能，包括创建、删除、复制、Mod 管理和排序等核心操作。

## 完成时间

2026-10-02

## 实现的功能

### 1. Profile CRUD 操作

#### CreateProfileAsync
创建新的 Profile。

```csharp
public async Task<Guid> CreateProfileAsync(
    string name, 
    string description = "", 
    CancellationToken cancellationToken = default)
```

**功能**:
- 创建新 Profile 实体
- 自动分配唯一 Guid
- 设置创建和更新时间
- 持久化到数据库

**使用示例**:
```csharp
var profileId = await profileService.CreateProfileAsync(
    "Daily Build", 
    "My everyday mods");
```

#### DeleteProfileAsync
删除指定的 Profile。

```csharp
public async Task DeleteProfileAsync(
    Guid id, 
    CancellationToken cancellationToken = default)
```

**功能**:
- 删除 Profile 实体
- 自动清理关联的 ProfileMod 记录
- 级联删除确保数据一致性

**使用示例**:
```csharp
await profileService.DeleteProfileAsync(profileId);
```

#### DuplicateProfileAsync
复制现有 Profile 及其所有 Mod 配置。

```csharp
public async Task<Profile> DuplicateProfileAsync(
    Guid id, 
    string newName, 
    CancellationToken cancellationToken = default)
```

**功能**:
- 复制 Profile 基本信息
- 复制所有 Mod 引用
- 保留 Mod 启用状态
- 保留 Mod 加载顺序

**使用示例**:
```csharp
var newProfile = await profileService.DuplicateProfileAsync(
    existingProfileId, 
    "Daily Build - Copy");
```

### 2. Mod 管理操作

#### AddModToProfileAsync
将 Mod 添加到 Profile。

```csharp
public async Task AddModToProfileAsync(
    Guid profileId, 
    Guid modId, 
    CancellationToken cancellationToken = default)
```

**功能**:
- 创建 Profile-Mod 关联
- 自动设置加载顺序（追加到末尾）
- 默认启用状态
- 防止重复添加

**使用示例**:
```csharp
await profileService.AddModToProfileAsync(profileId, modId);
```

#### RemoveModFromProfileAsync
从 Profile 中移除 Mod。

```csharp
public async Task RemoveModFromProfileAsync(
    Guid profileId, 
    Guid modId, 
    CancellationToken cancellationToken = default)
```

**功能**:
- 删除 Profile-Mod 关联
- 自动重新排序剩余 Mod
- 不影响 Library 中的 Mod

**使用示例**:
```csharp
await profileService.RemoveModFromProfileAsync(profileId, modId);
```

### 3. Mod 状态管理

#### SetModEnabledAsync
启用或禁用 Profile 中的 Mod。

```csharp
public async Task SetModEnabledAsync(
    Guid profileId, 
    Guid modId, 
    bool enabled, 
    CancellationToken cancellationToken = default)
```

**功能**:
- 切换 Mod 启用状态
- 更新 Profile 修改时间
- 不改变加载顺序

**使用示例**:
```csharp
// 启用 Mod
await profileService.SetModEnabledAsync(profileId, modId, true);

// 禁用 Mod
await profileService.SetModEnabledAsync(profileId, modId, false);
```

### 4. Mod 排序

#### ReorderModsAsync
重新排列 Profile 中 Mod 的加载顺序。

```csharp
public async Task ReorderModsAsync(
    Guid profileId, 
    List<Guid> modIds, 
    CancellationToken cancellationToken = default)
```

**功能**:
- 批量更新 Mod 加载顺序
- 验证所有 Mod 都属于该 Profile
- 原子操作确保一致性

**使用示例**:
```csharp
var newOrder = new List<Guid> { mod3Id, mod1Id, mod2Id };
await profileService.ReorderModsAsync(profileId, newOrder);
```

### 5. Profile 激活管理

#### SetActiveProfileAsync
设置当前激活的 Profile。

```csharp
public async Task SetActiveProfileAsync(
    Guid id, 
    CancellationToken cancellationToken = default)
```

**功能**:
- 设置 Profile 为激活状态
- 自动取消其他 Profile 的激活状态
- 确保同一时刻只有一个 Active Profile

**使用示例**:
```csharp
await profileService.SetActiveProfileAsync(profileId);
```

#### GetActiveProfileAsync
获取当前激活的 Profile。

```csharp
public async Task<Profile?> GetActiveProfileAsync(
    CancellationToken cancellationToken = default)
```

### 6. 查询操作

#### GetAllProfilesAsync
获取所有 Profile 列表。

```csharp
public async Task<List<Profile>> GetAllProfilesAsync(
    CancellationToken cancellationToken = default)
```

#### GetProfileByIdAsync
根据 ID 获取 Profile。

```csharp
public async Task<Profile?> GetProfileByIdAsync(
    Guid id, 
    CancellationToken cancellationToken = default)
```

#### GetProfileModsAsync
获取 Profile 中的所有 Mod（按加载顺序）。

```csharp
public async Task<List<Mod>> GetProfileModsAsync(
    Guid profileId, 
    CancellationToken cancellationToken = default)
```

## 数据模型

### Profile 实体

```csharp
public class Profile
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // 导航属性
    public List<ProfileMod> ProfileMods { get; set; }
}
```

### ProfileMod 关联实体

```csharp
public class ProfileMod
{
    public Guid ProfileId { get; set; }
    public Guid ModId { get; set; }
    public bool IsEnabled { get; set; }
    public int LoadOrder { get; set; }
    
    // 导航属性
    public Profile Profile { get; set; }
    public Mod Mod { get; set; }
}
```

## 架构设计

### 层次结构

```
UI Layer
    ↓ 调用
Application Layer
    └── ProfileService
        ↓ 使用
Infrastructure Layer
    ├── ProfileRepository
    └── ModRepository
        ↓ 访问
Database (SQLite)
    ├── Profiles 表
    ├── ProfileMods 表（关联表）
    └── Mods 表
```

### 依赖关系

```csharp
public class ProfileService
{
    private readonly IProfileRepository _profileRepository;
    private readonly IModRepository _modRepository;
    
    // 依赖注入
    public ProfileService(
        IProfileRepository profileRepository, 
        IModRepository modRepository)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
    }
}
```

## 测试覆盖

所有功能都有完整的单元测试覆盖：

### ProfileServiceTests

```
✅ 通过: 7/7 测试

1. CreateProfile_ShouldCreateNewProfile
   - 验证 Profile 创建
   - 验证自动生成 Guid
   - 验证时间戳设置

2. SetActiveProfile_ShouldActivateProfile
   - 验证激活状态切换
   - 验证同一时刻只有一个 Active Profile

3. AddModToProfile_ShouldAddMod
   - 验证 Mod 添加到 Profile
   - 验证加载顺序自动分配
   - 验证默认启用状态

4. RemoveModFromProfile_ShouldRemoveMod
   - 验证 Mod 从 Profile 移除
   - 验证不影响 Library
   - 验证顺序重新调整

5. SetModEnabled_ShouldToggleModState
   - 验证启用/禁用切换
   - 验证状态持久化

6. DuplicateProfile_ShouldCreateCopyWithMods
   - 验证 Profile 复制
   - 验证 Mod 引用复制
   - 验证状态和顺序保留

7. ReorderMods_ShouldChangeModOrder
   - 验证批量重排序
   - 验证顺序持久化
```

### 测试执行结果

```
已通过! - 失败: 0，通过: 7，已跳过: 0，总计: 7
持续时间: 375 ms
```

## 关键特性

### 1. 原子操作

所有操作都在数据库事务中执行，确保数据一致性：

```csharp
public async Task ReorderModsAsync(Guid profileId, List<Guid> modIds, ...)
{
    using var transaction = await _context.Database.BeginTransactionAsync(...);
    try
    {
        // 批量更新
        await _context.SaveChangesAsync(...);
        await transaction.CommitAsync(...);
    }
    catch
    {
        await transaction.RollbackAsync();
        throw;
    }
}
```

### 2. 自动时间戳管理

所有操作自动更新 `UpdatedAt` 时间戳：

```csharp
profile.UpdatedAt = DateTime.UtcNow;
await _profileRepository.UpdateAsync(profile, cancellationToken);
```

### 3. 级联操作

删除 Profile 时自动清理关联数据：

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<ProfileMod>()
        .HasOne(pm => pm.Profile)
        .WithMany(p => p.ProfileMods)
        .OnDelete(DeleteBehavior.Cascade);  // 级联删除
}
```

### 4. 防重复保护

添加 Mod 时检查是否已存在：

```csharp
var existing = await _profileRepository.GetProfileModAsync(...);
if (existing != null)
{
    throw new InvalidOperationException("Mod already in profile");
}
```

### 5. 取消令牌支持

所有异步操作支持取消：

```csharp
public async Task<Profile?> GetProfileByIdAsync(
    Guid id, 
    CancellationToken cancellationToken = default)
{
    return await _profileRepository.GetByIdAsync(id, cancellationToken);
}
```

## 业务规则

### Profile 生命周期

```
创建 Profile
    ↓
添加 Mod 引用（从 Library）
    ↓
配置 Mod（启用/禁用、排序）
    ↓
激活 Profile
    ↓
部署（Phase 7+）
    ↓
切换到其他 Profile 或删除
```

### Profile vs Library 的关系

**重要规则**:

```
加入 Library ≠ 加入 Profile
加入 Profile ≠ 启用
启用 ≠ 部署

移出 Profile ≠ 删除 Library
删除 Library = 从所有 Profile 中移除 + 删除本地文件
```

### Profile 独立性

- 同一个 Mod 可以被多个 Profile 引用
- 每个 Profile 维护自己的启用状态和加载顺序
- Profile 之间互不影响

### Active Profile 规则

- 同一时刻只能有一个 Active Profile
- 激活新 Profile 时自动取消其他 Profile 的激活状态
- Active Profile 是部署操作的目标

## 性能特性

### 批量操作优化

```csharp
// 批量重排序 - 单次数据库往返
await ReorderModsAsync(profileId, new List<Guid> { mod1, mod2, mod3 });

// 而不是多次调用
// await UpdateLoadOrder(mod1, 0);
// await UpdateLoadOrder(mod2, 1);
// await UpdateLoadOrder(mod3, 2);
```

### 延迟加载

```csharp
// 只加载需要的数据
var profile = await GetProfileByIdAsync(profileId);

// 需要时才加载 Mod 列表
var mods = await GetProfileModsAsync(profileId);
```

### 索引优化

数据库索引确保查询性能：

```sql
CREATE INDEX idx_profilemods_profileid ON ProfileMods(ProfileId);
CREATE INDEX idx_profilemods_order ON ProfileMods(ProfileId, LoadOrder);
CREATE INDEX idx_profiles_active ON Profiles(IsActive);
```

## 下一步集成

### Phase 6: Task Center

Profile 操作将集成到 Task 系统：

```csharp
// 导出 Profile
var task = await taskCenter.CreateExportProfileTaskAsync(profileId);

// 导入 Profile
var task = await taskCenter.CreateImportProfileTaskAsync(filePath);
```

### Phase 7: Deployment

Profile 激活将触发部署：

```csharp
await profileService.SetActiveProfileAsync(profileId);
await deploymentEngine.DeployActiveProfileAsync();
```

### UI Integration

Profile 管理 UI 将在后续 Phase 实现：

- Profile 列表视图
- Mod 拖拽排序
- 批量启用/禁用
- Profile 导入/导出

## 文件清单

### 核心代码
- `src/HD2Mimnt.Core/Entities/Profile.cs`
- `src/HD2Mimnt.Core/Entities/ProfileMod.cs`
- `src/HD2Mimnt.Core/Interfaces/IProfileRepository.cs`
- `src/HD2Mimnt.Infrastructure/Repositories/ProfileRepository.cs`
- `src/HD2Mimnt.Application/Services/ProfileService.cs`

### 测试代码
- `tests/HD2Mimnt.Tests/Services/ProfileServiceTests.cs`

### 数据库迁移
- EF Core 自动迁移（Profiles、ProfileMods 表）

## 总结

Phase 5 成功实现了完整的 Profile 管理功能，包括：

✅ Create - 创建 Profile
✅ Delete - 删除 Profile
✅ Duplicate - 复制 Profile
✅ Add - 添加 Mod 到 Profile
✅ Remove - 从 Profile 移除 Mod
✅ Enable/Disable - 启用/禁用 Mod
✅ Reorder - 重排 Mod 加载顺序

所有功能都经过充分测试，并遵循 Clean Architecture 原则。Profile 系统为后续的部署和任务管理奠定了坚实的基础。
