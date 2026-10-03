# Phase 16.5 Plan: Platform Metadata Persistence

## 目标

实现平台元数据的持久化系统，缓存平台搜索结果和 Mod 元数据，减少 API 调用次数，支持离线浏览。

## 已完成

Phase 16.4 已经完成了 Mod Entity 的平台属性扩展：
- ✅ `PlatformId` - 平台标识符
- ✅ `PlatformModId` - 平台特定 Mod ID
- ✅ `PlatformVersion` - 平台版本字符串
- ✅ `LastUpdateCheck` - 上次更新检查时间

## 待实现内容

### 1. Platform Cache Entity

缓存平台 Mod 元数据，减少 API 调用。

```csharp
namespace HD2Mimnt.Core.Entities;

public class PlatformCache
{
    public Guid Id { get; set; }
    public string PlatformId { get; set; } = string.Empty;
    public string PlatformModId { get; set; } = string.Empty;
    public string CachedData { get; set; } = string.Empty; // JSON serialized PlatformModInfo
    public DateTime CachedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string Etag { get; set; } = string.Empty; // For HTTP cache validation
}
```

### 2. Platform Cache Repository

```csharp
namespace HD2Mimnt.Core.Interfaces;

public interface IPlatformCacheRepository
{
    Task<PlatformCache?> GetAsync(string platformId, string platformModId, CancellationToken cancellationToken = default);
    Task AddAsync(PlatformCache cache, CancellationToken cancellationToken = default);
    Task UpdateAsync(PlatformCache cache, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<PlatformCache>> GetExpiredAsync(DateTime now, CancellationToken cancellationToken = default);
    Task DeleteExpiredAsync(DateTime now, CancellationToken cancellationToken = default);
    Task<int> GetCacheSizeAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(string? platformId = null, CancellationToken cancellationToken = default);
}
```

### 3. Platform Cache Repository Implementation

```csharp
namespace HD2Mimnt.Infrastructure.Repositories;

public class PlatformCacheRepository : IPlatformCacheRepository
{
    private readonly HD2MimntDbContext _context;

    public PlatformCacheRepository(HD2MimntDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    // 实现所有接口方法
}
```

### 4. Database Migration

```sql
CREATE TABLE IF NOT EXISTS PlatformCaches (
    Id TEXT PRIMARY KEY,
    PlatformId TEXT NOT NULL,
    PlatformModId TEXT NOT NULL,
    CachedData TEXT NOT NULL,
    CachedAt TEXT NOT NULL,
    ExpiresAt TEXT NOT NULL,
    Etag TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS IX_PlatformCaches_Platform 
    ON PlatformCaches(PlatformId, PlatformModId);

CREATE INDEX IF NOT EXISTS IX_PlatformCaches_ExpiresAt 
    ON PlatformCaches(ExpiresAt);
```

Mod 表已在 Phase 16.4 扩展，无需额外迁移。

### 5. Caching Platform Service

包装现有平台适配器，添加缓存层。

```csharp
namespace HD2Mimnt.Application.Services;

public class CachingPlatformService
{
    private readonly IPlatformRegistry _registry;
    private readonly IPlatformCacheRepository _cacheRepository;
    private readonly TimeSpan _defaultCacheDuration;

    public CachingPlatformService(
        IPlatformRegistry registry,
        IPlatformCacheRepository cacheRepository,
        TimeSpan? cacheDuration = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _cacheRepository = cacheRepository ?? throw new ArgumentNullException(nameof(cacheRepository));
        _defaultCacheDuration = cacheDuration ?? TimeSpan.FromHours(6);
    }

    public async Task<PlatformModInfo?> GetModInfoAsync(
        string platformId, 
        string platformModId, 
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    public async Task ClearExpiredCachesAsync(CancellationToken cancellationToken = default);
    
    public async Task<int> GetCacheSizeAsync(CancellationToken cancellationToken = default);
    
    public async Task ClearCacheAsync(string? platformId = null, CancellationToken cancellationToken = default);
}
```

**关键逻辑**:
1. 检查缓存是否存在且未过期
2. 如果存中，反序列化并返回
3. 如果缓存未命中或 `forceRefresh=true`，调用平台适配器
4. 将结果序列化并存入缓存

## 测试计划

### PlatformCacheRepositoryTests (10 tests)
- `Constructor_Should_Throw_When_Context_Is_Null`
- `GetAsync_Should_Return_Null_When_Not_Found`
- `GetAsync_Should_Return_Cache_When_Found`
- `AddAsync_Should_Insert_Cache`
- `UpdateAsync_Should_Modify_Cache`
- `DeleteAsync_Should_Remove_Cache`
- `GetExpiredAsync_Should_Return_Only_Expired`
- `DeleteExpiredAsync_Should_Remove_Only_Expired`
- `GetCacheSizeAsync_Should_Return_Count`
- `ClearAsync_Should_Remove_All_Or_By_Platform`

### CachingPlatformServiceTests (12 tests)
- `Constructor_Should_Throw_When_Registry_Is_Null`
- `Constructor_Should_Throw_When_CacheRepository_Is_Null`
- `Constructor_Should_Use_Default_Cache_Duration`
- `GetModInfoAsync_Should_Return_From_Cache_When_Valid`
- `GetModInfoAsync_Should_Call_Platform_When_Cache_Miss`
- `GetModInfoAsync_Should_Call_Platform_When_Expired`
- `GetModInfoAsync_Should_Call_Platform_When_ForceRefresh`
- `GetModInfoAsync_Should_Store_Result_In_Cache`
- `GetModInfoAsync_Should_Return_Null_When_Platform_Not_Found`
- `ClearExpiredCachesAsync_Should_Delete_Expired`
- `GetCacheSizeAsync_Should_Return_Count`
- `ClearCacheAsync_Should_Clear_All_Or_By_Platform`

**预计新增测试**: 22 tests

## 实现步骤

1. ✅ 创建 PlatformCache 实体
2. ✅ 创建 IPlatformCacheRepository 接口
3. ✅ 实现 PlatformCacheRepository
4. ✅ 添加数据库迁移（DbContext 配置）
5. ✅ 实现 CachingPlatformService
6. ✅ 编写 PlatformCacheRepositoryTests（10 tests）
7. ✅ 编写 CachingPlatformServiceTests（12 tests）
8. ✅ 在 App.axaml.cs 中注册服务
9. ✅ 验证所有测试通过
10. ✅ 更新 PROGRESS.md

## 成功标准

- ✅ PlatformCache 实体定义完成
- ✅ IPlatformCacheRepository 接口和实现完成
- ✅ CachingPlatformService 实现完成
- ✅ 数据库迁移配置完成
- ✅ 22 个单元测试全部通过
- ✅ 缓存过期机制正常工作
- ✅ 强制刷新功能正常工作

## 架构集成

```
UI Layer
    ↓
CachingPlatformService (Application Layer)
    ↓
├─ PlatformCacheRepository (Infrastructure)
│   └─ HD2MimntDbContext
└─ PlatformRegistry → NexusModsPlatformAdapter / GitHubPlatformAdapter
    └─ HTTP API
```

缓存层透明地包装平台适配器，减少 API 调用，提升性能。
