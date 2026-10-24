# Phase 16: Platform Integration - 实现规划

## 概述

Phase 16 实现 Mod Platform 集成，允许用户从外部平台搜索、下载和管理 Mod。

根据开发计划（Section 108），Platform 系统采用 Adapter 模式，支持多个平台。

---

## 核心设计

### 1. IModPlatform 接口

```csharp
public interface IModPlatform
{
    string Name { get; }
    string Id { get; }
    PlatformCapabilities Capabilities { get; }
    
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default);
    Task<PlatformModInfo> GetModInfoAsync(string modId, CancellationToken cancellationToken = default);
    Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<List<PlatformModVersion>> GetVersionsAsync(string modId, CancellationToken cancellationToken = default);
    Task<List<PlatformModFile>> GetFilesAsync(string modId, string? version = null, CancellationToken cancellationToken = default);
}
```

### 2. Platform Capabilities

```csharp
[Flags]
public enum PlatformCapabilities
{
    None = 0,
    Search = 1 << 0,
    Download = 1 << 1,
    Metadata = 1 << 2,
    Versions = 1 << 3,
    Update = 1 << 4,
    Files = 1 << 5,
    Authentication = 1 << 6,
    RateLimit = 1 << 7
}
```

### 3. Platform Registry

```csharp
public interface IPlatformRegistry
{
    void RegisterPlatform(IModPlatform platform);
    IModPlatform? GetPlatform(string platformId);
    List<IModPlatform> GetAllPlatforms();
    List<IModPlatform> GetAvailablePlatforms();
}
```

---

## Phase 16.1: Core Platform Infrastructure

### 实现内容

#### 1. Core Entities (4个文件)

**PlatformSearchQuery.cs**
```csharp
public class PlatformSearchQuery
{
    public string Query { get; set; } = string.Empty;
    public int PageSize { get; set; } = 20;
    public int Page { get; set; } = 1;
    public PlatformSearchSort SortBy { get; set; } = PlatformSearchSort.Relevance;
    public Dictionary<string, string> Filters { get; set; } = new();
}
```

**PlatformSearchResult.cs**
```csharp
public class PlatformSearchResult
{
    public List<PlatformModInfo> Mods { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool HasNextPage { get; set; }
}
```

**PlatformModInfo.cs**
```csharp
public class PlatformModInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime? UpdatedAt { get; set; }
    public long DownloadCount { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}
```

**PlatformModVersion.cs**
```csharp
public class PlatformModVersion
{
    public string Version { get; set; } = string.Empty;
    public DateTime ReleasedAt { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string ChangeLog { get; set; } = string.Empty;
}
```

#### 2. Infrastructure (2个文件)

**PlatformRegistry.cs**
```csharp
public class PlatformRegistry : IPlatformRegistry
{
    private readonly Dictionary<string, IModPlatform> _platforms = new();
    
    public void RegisterPlatform(IModPlatform platform);
    public IModPlatform? GetPlatform(string platformId);
    public List<IModPlatform> GetAllPlatforms();
    public List<IModPlatform> GetAvailablePlatforms();
}
```

**BasePlatformAdapter.cs**
```csharp
public abstract class BasePlatformAdapter : IModPlatform
{
    public abstract string Name { get; }
    public abstract string Id { get; }
    public abstract PlatformCapabilities Capabilities { get; }
    
    protected HttpClient HttpClient { get; }
    protected ILogger Logger { get; }
    
    public virtual Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    
    // 子类实现具体平台逻辑
    public abstract Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default);
    // ... 其他抽象方法
}
```

#### 3. Application Service (1个文件)

**PlatformService.cs**
```csharp
public class PlatformService
{
    private readonly IPlatformRegistry _platformRegistry;
    private readonly IModRepository _modRepository;
    private readonly ILogger<PlatformService> _logger;
    
    public async Task<PlatformSearchResult> SearchAcrossPlatformsAsync(string query, List<string>? platformIds = null);
    public async Task<Mod> DownloadAndImportModAsync(string platformId, string modId, IProgress<ImportProgress>? progress = null);
    public async Task<bool> CheckForUpdatesAsync(Mod mod);
    public async Task<Mod?> UpdateModAsync(Mod mod);
}
```

#### 4. Tests (3个文件)

**PlatformRegistryTests.cs** - 10 测试
- RegisterPlatform_Should_Add_Platform
- GetPlatform_Should_Return_Registered_Platform
- GetPlatform_Should_Return_Null_For_Unknown
- GetAllPlatforms_Should_Return_All
- GetAvailablePlatforms_Should_Only_Return_Available
- RegisterPlatform_Should_Throw_On_Duplicate_Id
- RegisterPlatform_Should_Throw_On_Null
- GetAllPlatforms_Should_Return_Empty_Initially
- RegisterPlatform_Should_Support_Multiple
- GetAvailablePlatforms_Should_Check_Availability

**MockPlatformAdapterTests.cs** - 8 测试
- SearchAsync_Should_Return_Results
- GetModInfoAsync_Should_Return_Info
- DownloadModAsync_Should_Return_Stream
- GetVersionsAsync_Should_Return_Versions
- IsAvailableAsync_Should_Return_True
- SearchAsync_Should_Handle_Pagination
- SearchAsync_Should_Apply_Filters
- DownloadModAsync_Should_Report_Progress

**PlatformServiceTests.cs** - 12 测试
- SearchAcrossPlatforms_Should_Aggregate_Results
- SearchAcrossPlatforms_Should_Filter_By_Platform_Ids
- DownloadAndImportMod_Should_Import_To_Library
- CheckForUpdates_Should_Detect_New_Version
- UpdateMod_Should_Download_New_Version
- DownloadAndImportMod_Should_Report_Progress
- SearchAcrossPlatforms_Should_Handle_Platform_Failure
- DownloadAndImportMod_Should_Handle_Download_Error
- CheckForUpdates_Should_Return_False_For_Latest
- UpdateMod_Should_Return_Null_If_No_Update
- DownloadAndImportMod_Should_Set_Platform_Metadata
- SearchAcrossPlatforms_Should_Sort_Results

### 成功标准
- ✅ IPlatformRegistry 实现完成
- ✅ BasePlatformAdapter 基类完成
- ✅ PlatformService 实现完成
- ✅ 30 个单元测试全部通过
- ✅ 可以注册和管理多个平台

---

## Phase 16.2: NexusMods Platform Adapter

### 目标
实现 NexusMods 平台适配器（最常用的 Mod 平台）。

### 实现内容

#### NexusModsPlatform.cs
```csharp
public class NexusModsPlatform : BasePlatformAdapter
{
    public override string Name => "NexusMods";
    public override string Id => "nexusmods";
    public override PlatformCapabilities Capabilities => 
        PlatformCapabilities.Search | 
        PlatformCapabilities.Download | 
        PlatformCapabilities.Metadata | 
        PlatformCapabilities.Versions | 
        PlatformCapabilities.Files |
        PlatformCapabilities.Authentication |
        PlatformCapabilities.RateLimit;
    
    private const string ApiBaseUrl = "https://api.nexusmods.com/v1";
    private const string GameDomain = "helldivers2";
    
    public override async Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default);
    public override async Task<PlatformModInfo> GetModInfoAsync(string modId, CancellationToken cancellationToken = default);
    public override async Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    public override async Task<List<PlatformModVersion>> GetVersionsAsync(string modId, CancellationToken cancellationToken = default);
    public override async Task<List<PlatformModFile>> GetFilesAsync(string modId, string? version = null, CancellationToken cancellationToken = default);
}
```

#### API 限流处理
```csharp
public class RateLimiter
{
    private int _remainingRequests;
    private DateTime _resetTime;
    
    public async Task WaitIfNeededAsync(CancellationToken cancellationToken = default);
    public void UpdateFromHeaders(HttpResponseMessage response);
}
```

#### Authentication
```csharp
public class NexusModsAuth
{
    public string? ApiKey { get; set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(ApiKey);
    
    public void SetApiKey(string apiKey);
    public void ClearApiKey();
}
```

### 成功标准
- ✅ 实现所有 IModPlatform 方法
- ✅ 处理 API 限流
- ✅ 支持 API Key 认证
- ✅ 12 个单元测试（使用 Mock HTTP）

---

## Phase 16.3: GitHub Releases Platform Adapter

### 目标
支持从 GitHub Releases 下载 Mod。

### 实现内容

#### GitHubPlatform.cs
```csharp
public class GitHubPlatform : BasePlatformAdapter
{
    public override string Name => "GitHub";
    public override string Id => "github";
    public override PlatformCapabilities Capabilities => 
        PlatformCapabilities.Search | 
        PlatformCapabilities.Download | 
        PlatformCapabilities.Metadata | 
        PlatformCapabilities.Versions | 
        PlatformCapabilities.Files;
    
    private const string ApiBaseUrl = "https://api.github.com";
    
    // 搜索 Helldivers 2 相关仓库
    public override async Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default);
    
    // 获取 Release 信息
    public override async Task<PlatformModInfo> GetModInfoAsync(string modId, CancellationToken cancellationToken = default);
    
    // 下载 Release 资产
    public override async Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
```

### ModId 格式
```
github:owner/repo
github:owner/repo/releases/tag/v1.0.0
```

### 成功标准
- ✅ 支持 GitHub API
- ✅ 解析 Releases
- ✅ 下载 Release 资产
- ✅ 10 个单元测试

---

## Phase 16.4: UI Integration

### 目标
在 UI 中集成 Platform 功能。

### 实现内容

#### 1. Platform Browser View
- 平台选择器（NexusMods, GitHub, etc.）
- 搜索界面
- 搜索结果列表（带缩略图）
- Mod 详情页面

#### 2. Download Manager
- 下载队列
- 下载进度显示
- 自动导入到 Library
- 错误处理和重试

#### 3. Update Checker
- 检查已安装 Mod 的更新
- 批量更新
- 更新日志显示

#### ViewModel 示例
```csharp
public partial class PlatformBrowserViewModel : ViewModelBase
{
    [ObservableProperty]
    private IModPlatform? _selectedPlatform;
    
    [ObservableProperty]
    private string _searchQuery = string.Empty;
    
    [ObservableProperty]
    private ObservableCollection<PlatformModInfo> _searchResults = new();
    
    [RelayCommand]
    private async Task SearchAsync();
    
    [RelayCommand]
    private async Task DownloadModAsync(PlatformModInfo modInfo);
}
```

### 成功标准
- ✅ Platform Browser 页面完成
- ✅ 搜索功能正常
- ✅ 下载功能正常
- ✅ Update Checker 正常

---

## Phase 16.5: Platform Metadata Persistence

### 目标
保存 Mod 的平台来源信息，支持后续更新检查。

### 实现内容

#### Mod Entity 扩展
```csharp
public class Mod
{
    // ... 现有属性
    
    public string? PlatformId { get; set; }
    public string? PlatformModId { get; set; }
    public string? PlatformVersion { get; set; }
    public DateTime? LastUpdateCheck { get; set; }
}
```

#### Database Migration
```sql
ALTER TABLE Mods ADD COLUMN PlatformId TEXT;
ALTER TABLE Mods ADD COLUMN PlatformModId TEXT;
ALTER TABLE Mods ADD COLUMN PlatformVersion TEXT;
ALTER TABLE Mods ADD COLUMN LastUpdateCheck TEXT;

CREATE INDEX IF NOT EXISTS IX_Mods_Platform ON Mods(PlatformId, PlatformModId);
```

### 成功标准
- ✅ 数据库迁移完成
- ✅ Mod 导入时保存平台信息
- ✅ Update Checker 使用平台信息

---

## 测试策略

### 单元测试
```
PlatformRegistryTests: 10 测试
MockPlatformAdapterTests: 8 测试
PlatformServiceTests: 12 测试
NexusModsPlatformTests: 12 测试
GitHubPlatformTests: 10 测试
-----------------------------------
总计: 52 测试
```

### 集成测试
- 真实 API 调用测试（可选，需要 API Key）
- 下载完整性测试
- 导入流程测试

### 性能测试
- 搜索响应时间 < 2s
- 下载速度不受限制（除平台限流）
- 并发下载支持

---

## 错误处理

### 常见错误场景
1. **网络错误** - 重试机制
2. **API 限流** - 自动等待
3. **认证失败** - 提示用户
4. **下载中断** - 支持断点续传
5. **平台不可用** - 优雅降级

---

## 配置示例

### Platform Settings
```json
{
  "platforms": {
    "nexusmods": {
      "enabled": true,
      "apiKey": "YOUR_API_KEY",
      "respectRateLimit": true
    },
    "github": {
      "enabled": true,
      "token": null
    }
  },
  "download": {
    "maxConcurrent": 3,
    "retryCount": 3,
    "timeout": 300
  },
  "update": {
    "autoCheckInterval": 86400,
    "autoDownload": false
  }
}
```

---

## Phase 16 总结

完成 Phase 16 后，HD2Mimnt 将具备：

✅ 统一的平台适配器接口  
✅ 多平台支持（NexusMods, GitHub）  
✅ 平台搜索和浏览  
✅ 一键下载和导入  
✅ 自动更新检查  
✅ 52+ 单元测试  

---

## 当前状态

Phase 15 完成（181 测试通过）

下一步：实现 Phase 16.1 - Core Platform Infrastructure
