# Phase 17 Implementation Plan: Plugin System

## Overview

Phase 17 implements the Plugin System as specified in the development plan. This enables third-party developers to extend HD2Mimnt functionality while maintaining system stability and security.

---

## Core Requirements (from plan)

### 1. Plugin不能污染Core
- Plugin 不能访问内部 DB Schema
- Plugin 不能访问 Core 私有类
- Plugin 不能访问私有 UI
- Plugin 不能访问内部文件结构
- Plugin 只能使用 Public Plugin SDK

### 2. Plugin Host 进程隔离
```
HD2Mimnt.exe
    ↕ IPC
HD2PluginHost.exe
    ↓
Plugin.dll
```

目的: **Plugin 崩了，主程序别跟着一起去世**

### 3. 两种运行模式
- **Sandbox** (默认): 受限权限
- **Full Access**: 用户明确允许后才能使用

### 4. Plugin Manifest
至少包含:
```
Id
Name
Version
Author
ApiVersion
Runtime
PermissionMode
RequestedPermissions
```

不做数字签名和在线认证。

---

## Architecture Design

### Layer Structure

```
HD2Mimnt.Core
    ↓ (references)
HD2Mimnt.PluginSDK (Public API)
    ↑ (references)
ThirdPartyPlugin.dll

HD2Mimnt.Application
    ↓ (uses)
HD2Mimnt.PluginHost (Process Manager)
    ↓ (spawns)
HD2Mimnt.PluginHost.exe
    ↓ (loads)
ThirdPartyPlugin.dll
```

### Communication Flow

```
UI Command
    ↓
Application/PluginService
    ↓
PluginHost Manager
    ↓ Named Pipes / IPC
PluginHost.exe Process
    ↓
Plugin.dll
    ↓ SDK Calls
Response
```

---

## Implementation Phases

### Phase 17.1: Plugin SDK (Public API)

**Project**: `HD2Mimnt.PluginSDK`

**Entities to expose**:
```csharp
// Safe DTOs (no internal implementation details)
public class PluginModInfo { ... }
public class PluginProfileInfo { ... }
public class PluginTaskInfo { ... }

// Interfaces plugins implement
public interface IPlugin
{
    PluginManifest Manifest { get; }
    Task InitializeAsync();
    Task ShutdownAsync();
}

public interface IModPlugin : IPlugin
{
    Task<PluginResult> OnModImported(PluginModInfo mod);
    Task<PluginResult> OnModDeleted(PluginModInfo mod);
}

public interface IDeploymentPlugin : IPlugin
{
    Task<PluginResult> OnPreDeploy(PluginDeploymentContext context);
    Task<PluginResult> OnPostDeploy(PluginDeploymentContext context);
}

public interface IAnalysisPlugin : IPlugin
{
    Task<PluginAnalysisResult> AnalyzeMod(PluginModInfo mod);
}
```

**SDK Services** (Host provides to plugins):
```csharp
public interface IPluginContext
{
    IPluginLogger Logger { get; }
    IPluginStorage Storage { get; }
    IPluginModAccess ModAccess { get; }
    IPluginConfiguration Configuration { get; }
}
```

**Sandbox Restrictions**:
- Cannot access file system outside plugin directory
- Cannot start processes
- Cannot access network (unless Full Access)
- Cannot access database directly

**Full Access Capabilities**:
- File system access
- Process spawning
- Network access
- Limited database read access (via safe DTOs)

**Tests**: 15-20 tests
- Manifest validation
- Permission checks
- SDK API contracts

---

### Phase 17.2: Plugin Host Process

**Project**: `HD2Mimnt.PluginHost`

**Executable**: `HD2Mimnt.PluginHost.exe`

**Responsibilities**:
- Load plugin assemblies in isolated AppDomain/AssemblyLoadContext
- Enforce sandbox restrictions
- Manage plugin lifecycle
- IPC communication with main process
- Crash isolation (plugin crash doesn't kill host)

**IPC Protocol**:
```
Named Pipes: \\.\pipe\HD2Mimnt.PluginHost.{PluginId}

Message Format:
{
    "Type": "Command" | "Response" | "Event",
    "PluginId": "guid",
    "Payload": { ... }
}
```

**Lifecycle**:
```
1. Main app spawns PluginHost.exe --plugin <path>
2. Host loads plugin DLL
3. Host validates manifest
4. Host initializes plugin
5. IPC connection established
6. Main app sends commands
7. Plugin responds via IPC
8. On shutdown/crash, host process exits
9. Main app detects and logs
```

**Tests**: 20-25 tests
- Process spawning
- IPC communication
- Crash isolation
- Sandbox enforcement
- Plugin loading/unloading

---

### Phase 17.3: Plugin Management Service

**Project**: `HD2Mimnt.Application`

**New Services**:

```csharp
public interface IPluginRegistry
{
    Task<IReadOnlyList<PluginManifest>> GetInstalledPluginsAsync();
    Task<PluginManifest> InstallPluginAsync(string path);
    Task UninstallPluginAsync(string pluginId);
    Task<PluginInfo> GetPluginInfoAsync(string pluginId);
}

public interface IPluginHostManager
{
    Task<IPluginHost> StartPluginAsync(string pluginId);
    Task StopPluginAsync(string pluginId);
    Task<PluginExecutionResult> ExecutePluginCommandAsync(
        string pluginId, 
        string command, 
        object? args = null);
    IReadOnlyList<IPluginHost> GetRunningPlugins();
}

public class PluginService
{
    // Coordinates plugin operations
    // Integrates with Task Center
    // Manages plugin permissions
}
```

**Database Tables**:
```sql
CREATE TABLE Plugins (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Version TEXT NOT NULL,
    Author TEXT,
    Description TEXT,
    ManifestPath TEXT NOT NULL,
    InstalledAt TEXT NOT NULL,
    Enabled INTEGER NOT NULL DEFAULT 1,
    PermissionMode TEXT NOT NULL -- 'Sandbox' | 'FullAccess'
);

CREATE TABLE PluginPermissions (
    Id TEXT PRIMARY KEY,
    PluginId TEXT NOT NULL,
    Permission TEXT NOT NULL,
    Granted INTEGER NOT NULL DEFAULT 0,
    GrantedAt TEXT,
    FOREIGN KEY (PluginId) REFERENCES Plugins(Id)
);

CREATE TABLE PluginExecutionHistory (
    Id TEXT PRIMARY KEY,
    PluginId TEXT NOT NULL,
    Command TEXT NOT NULL,
    StartTime TEXT NOT NULL,
    EndTime TEXT,
    Status TEXT NOT NULL,
    Result TEXT,
    Error TEXT,
    FOREIGN KEY (PluginId) REFERENCES Plugins(Id)
);
```

**Tests**: 15-20 tests
- Plugin installation
- Plugin uninstallation
- Permission management
- Execution tracking

---

### Phase 17.4: Plugin Discovery & UI

**UI Components**:

```
Settings → Plugins

┌────────────────────────────────────────────┐
│ Installed Plugins                          │
├────────────────────────────────────────────┤
│                                            │
│ ┌────────────────────────────────────┐    │
│ │ 📦 Example Analysis Plugin         │    │
│ │ Version: 1.0.0                     │    │
│ │ Author: Third Party                │    │
│ │ Status: ✅ Enabled                 │    │
│ │ Mode: 🔒 Sandbox                   │    │
│ │                                    │    │
│ │ [Disable] [Configure] [Remove]    │    │
│ └────────────────────────────────────┘    │
│                                            │
│ [Install New Plugin...]                    │
└────────────────────────────────────────────┘
```

**Permission Dialog**:
```
Plugin "XYZ" requests Full Access

Requested Permissions:
  ✓ Read mod files
  ✓ Access network
  ⚠️ Execute external programs
  ⚠️ Write to disk

[Allow Once] [Allow Always] [Deny]
```

**Tests**: 10-15 tests (ViewModel tests)
- Plugin list display
- Permission requests
- Enable/Disable
- Installation workflow

---

### Phase 17.5: Plugin Events & Hooks

**Event System**:

```csharp
public interface IPluginEventBus
{
    void Subscribe<TEvent>(string pluginId, Action<TEvent> handler);
    void Unsubscribe<TEvent>(string pluginId);
    Task PublishAsync<TEvent>(TEvent @event);
}

// Events plugins can listen to
public class ModImportedEvent { ... }
public class ModDeletedEvent { ... }
public class DeploymentStartedEvent { ... }
public class DeploymentCompletedEvent { ... }
public class ProfileChangedEvent { ... }
```

**Hook Points** (plugins can intercept):
```
OnModImport
OnModValidation
OnModAnalysis
OnPreDeployment
OnPostDeployment
OnCleanup
OnProfileSwitch
```

**Tests**: 10-15 tests
- Event subscription
- Event publishing
- Hook execution order
- Error isolation

---

## Implementation Order

```
Week 1: Phase 17.1 (Plugin SDK)
  - Define public interfaces
  - Implement DTOs
  - Permission model
  - SDK documentation

Week 2: Phase 17.2 (Plugin Host Process)
  - Process spawning
  - IPC implementation
  - Plugin loading
  - Crash isolation

Week 3: Phase 17.3 (Plugin Management Service)
  - Database schema
  - PluginRegistry
  - PluginHostManager
  - Permission enforcement

Week 4: Phase 17.4 (Plugin UI)
  - Settings page
  - Installation workflow
  - Permission dialogs
  - Configuration UI

Week 5: Phase 17.5 (Events & Hooks)
  - Event bus
  - Hook points
  - Integration testing

Week 6: Testing & Documentation
  - Integration tests
  - Example plugins
  - Plugin developer guide
```

---

## Example Plugin

```csharp
// ExampleAnalysisPlugin.cs
using HD2Mimnt.PluginSDK;

public class ExampleAnalysisPlugin : IAnalysisPlugin
{
    public PluginManifest Manifest => new()
    {
        Id = "com.example.analysis",
        Name = "Example Analysis Plugin",
        Version = "1.0.0",
        Author = "Example Author",
        ApiVersion = "1.0",
        PermissionMode = PluginPermissionMode.Sandbox,
        RequestedPermissions = new[] { "ReadModFiles" }
    };

    private IPluginContext _context;

    public Task InitializeAsync(IPluginContext context)
    {
        _context = context;
        _context.Logger.Info("Plugin initialized");
        return Task.CompletedTask;
    }

    public async Task<PluginAnalysisResult> AnalyzeMod(PluginModInfo mod)
    {
        _context.Logger.Info($"Analyzing mod: {mod.Name}");
        
        // Custom analysis logic
        var issues = new List<string>();
        
        if (mod.FileCount > 10000)
        {
            issues.Add("Large file count detected");
        }
        
        return new PluginAnalysisResult
        {
            Success = true,
            Issues = issues,
            Metadata = new Dictionary<string, string>
            {
                ["AnalyzedBy"] = Manifest.Id,
                ["IssueCount"] = issues.Count.ToString()
            }
        };
    }

    public Task ShutdownAsync()
    {
        _context.Logger.Info("Plugin shutting down");
        return Task.CompletedTask;
    }
}

// Plugin.manifest.json
{
    "id": "com.example.analysis",
    "name": "Example Analysis Plugin",
    "version": "1.0.0",
    "author": "Example Author",
    "description": "Demonstrates plugin capabilities",
    "apiVersion": "1.0",
    "runtime": "net10.0",
    "permissionMode": "Sandbox",
    "requestedPermissions": [
        "ReadModFiles"
    ],
    "assembly": "ExampleAnalysisPlugin.dll",
    "entryType": "ExampleAnalysisPlugin.ExampleAnalysisPlugin"
}
```

---

## Security Considerations

### Sandbox Mode (Default)
- Plugin runs in separate process
- Limited file system access (plugin directory only)
- No network access
- No process spawning
- No registry access
- Read-only mod metadata access

### Full Access Mode (User-approved)
- File system access (with user consent)
- Network access (with user consent)
- Process spawning (with user consent)
- Still runs in separate process
- Still crash-isolated

### Permission Validation
```csharp
public enum PluginPermission
{
    ReadModFiles,
    WriteModFiles,
    AccessNetwork,
    ExecuteProcesses,
    ReadDatabase,
    WriteConfiguration
}
```

---

## Error Handling

### Plugin Crash
```
1. Plugin throws unhandled exception
2. PluginHost.exe catches exception
3. PluginHost.exe sends error via IPC
4. Main app logs error
5. Main app marks plugin as "Crashed"
6. UI shows error notification
7. User can restart plugin or disable it
```

### PluginHost Crash
```
1. PluginHost.exe process crashes
2. Main app detects process exit
3. Main app logs crash
4. Main app marks plugin as "Terminated"
5. UI shows error notification
6. Plugin can be restarted
```

### Communication Timeout
```
1. Main app sends command via IPC
2. Timeout after 30 seconds
3. Main app logs timeout
4. Main app can force-terminate plugin
```

---

## Testing Strategy

### Unit Tests (SDK)
- Manifest validation
- DTO serialization
- Permission checks
- Interface contracts

### Integration Tests (Host)
- Plugin loading
- IPC communication
- Sandbox enforcement
- Crash isolation

### End-to-End Tests
- Install plugin
- Execute plugin command
- Plugin crash recovery
- Permission dialog flow

### Performance Tests
- Plugin startup time
- IPC latency
- Multiple plugins running
- Memory usage

---

## Documentation Deliverables

1. **Plugin Developer Guide**
   - Getting started
   - SDK API reference
   - Permission model
   - Best practices
   - Example plugins

2. **Plugin User Guide**
   - Installing plugins
   - Managing permissions
   - Troubleshooting

3. **Architecture Document**
   - System design
   - IPC protocol
   - Security model

---

## Success Criteria

Phase 17 is complete when:

1. ✅ Plugin SDK is published as NuGet package (or available)
2. ✅ Example plugin successfully loads and executes
3. ✅ Plugin crashes do not affect main application
4. ✅ Sandbox mode prevents unauthorized access
5. ✅ Full Access mode requires user approval
6. ✅ All 80+ tests pass
7. ✅ Plugin developer guide is published
8. ✅ At least 2 example plugins are working

---

## Estimated Effort

- **Phase 17.1**: 1 week (40 hours)
- **Phase 17.2**: 1 week (40 hours)
- **Phase 17.3**: 1 week (40 hours)
- **Phase 17.4**: 1 week (40 hours)
- **Phase 17.5**: 1 week (40 hours)
- **Testing & Docs**: 1 week (40 hours)

**Total**: 6 weeks (240 hours)

---

## Dependencies

- Phase 6 (Task Center) ✅
- Phase 1-2 (Core architecture) ✅
- IPC library (System.IO.Pipes or gRPC)
- JSON serialization (System.Text.Json)

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Plugin SDK design changes | High | Freeze SDK API early, version carefully |
| IPC performance issues | Medium | Benchmark early, use efficient serialization |
| Sandbox bypass | High | Security review, penetration testing |
| Plugin compatibility | Medium | Clear versioning, compatibility checks |
| Documentation gaps | Low | Write docs alongside code |

---

## Notes

- This is a **major** feature that enables ecosystem growth
- Follows plan's requirement: "Plugin 崩了,主程序别跟着一起去世"
- Maintains clean separation between public SDK and internal implementation
- Allows community contributions without compromising stability
