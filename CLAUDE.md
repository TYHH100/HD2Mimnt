# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

HD2Mimnt is a Helldivers 2 Mod Manager built with .NET 10, Avalonia UI, and SQLite. It provides mod library management, profile-based configurations, multiple deployment methods (Copy, Symbolic Link, Hard Link), workflow system for different mod formats, and plugin architecture for extensibility.

**Architecture**: Clean Architecture with 4 main layers:
- **Core** (`HD2Mimnt.Core`): Domain entities, interfaces, enums
- **Infrastructure** (`HD2Mimnt.Infrastructure`): Data access (SQLite), deployment methods, workflows, platform adapters
- **Application** (`HD2Mimnt.Application`): Business logic services (ModService, ProfileService, DeploymentService, TaskService, etc.)
- **UI** (`HD2Mimnt.UI`): Avalonia-based MVVM presentation layer

**Additional Projects**:
- `HD2Mimnt.PluginSDK`: Plugin development SDK (interfaces: IPlugin, IModPlugin, IDeploymentPlugin, IAnalysisPlugin)
- `HD2Mimnt.PluginHost`: Isolated plugin execution host (separate process)
- `HD2Mimnt.Worker`: Background worker process for long-running tasks

## Build and Test Commands

```bash
# Build the entire solution
dotnet build

# Build specific project
dotnet build src/HD2Mimnt.UI/HD2Mimnt.UI.csproj

# Run the application
cd src/HD2Mimnt.UI
dotnet run

# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity normal

# Run specific test class
dotnet test --filter "FullyQualifiedName~ProfileServiceTests"

# Run single test method
dotnet test --filter "FullyQualifiedName~ProfileServiceTests.CreateProfile_ShouldSucceed"

# Run benchmarks
cd benchmarks/HD2Mimnt.Benchmarks
dotnet run -c Release

# Run stability tests
cd tests/HD2Mimnt.StabilityTests
dotnet test

# Publish for Windows x64
dotnet publish src/HD2Mimnt.UI/HD2Mimnt.UI.csproj -c Release -r win-x64 --self-contained
```

## Architecture Highlights

### Deployment System
The deployment system supports three methods with complete file tracking:
- **Copy**: Traditional file copying
- **SymbolicLink**: Windows symbolic links (requires admin)
- **HardLink**: NTFS hard links (same volume only)

Each deployment creates a `SessionId` for batch operations. The `DeploymentService` handles:
- Pre-deployment cleanup (removes previous session files)
- Profile deployment (deploys all enabled mods in order)
- File-level tracking via `DeploymentRecord` and `DeploymentFile` entities
- Cleanup operations using the appropriate method for each file

### Workflow System
Workflows define how different mod formats are parsed, validated, and deployed:
- **LegacyWorkflow**: Original format with dropdown options (manifest v0)
- **V1Workflow**: Modern format with independent toggles (manifest v1)
- **IntegratedArmorWorkflow**: Specialized armor mod format with variants

Workflows implement `IWorkflow` and are managed by `WorkflowRegistry`. The `ManifestParser` service handles JSON parsing.

### Plugin Architecture
Plugin system uses isolated processes for safety:
- Plugins implement `IPlugin` (base), `IModPlugin`, `IDeploymentPlugin`, or `IAnalysisPlugin`
- PluginHost runs in separate process, communicates via IPC
- Plugins have manifest-based permissions (filesystem, network, database)
- Sandbox isolation prevents plugins from affecting main application

### Task System
All long-running operations are tracked through the `TaskService`:
- Import, Scan, Validation, Deployment, Cleanup tasks
- Progress tracking with stages and percentages
- Cancellation support via CancellationToken
- Workers (`ScanWorker`, `ValidationWorker`, `ImportWorker`) execute background operations

### Platform Integration
External mod platforms (Nexus Mods, etc.) are abstracted via `IModPlatform`:
- Search, download, version checking
- Platform-specific metadata
- `PlatformRegistry` manages available adapters
- Currently includes `MockPlatformAdapter` for testing

## Key Design Patterns

### Repository Pattern
All data access goes through repository interfaces:
- `IModRepository`, `IProfileRepository`, `ITaskRepository`, `IDeploymentRepository`
- Concrete implementations in Infrastructure layer use SQLite
- Enables testing with in-memory implementations

### Service Layer
Business logic lives in Application services:
- `ModService`: Mod CRUD, import, search
- `ProfileService`: Profile management, mod assignment
- `DeploymentService`: Deployment orchestration and cleanup
- `TaskService`: Task lifecycle management
- `SearchService`: Full-text search across mods
- `ResourceExplorerService`: Mod resource dependency analysis

### Dependency Injection
The application uses Microsoft.Extensions.DependencyInjection. Services are registered in the UI project's Program.cs or App.axaml.cs.

### Image Handling
- `ImageLoaderService`: Loads images from disk/resources
- `ImageCacheService`: LRU cache for thumbnail optimization
- `ThumbnailService`: Generates thumbnails from mod icons

## Database Schema

SQLite database with core tables:
- `Mods`: Mod library (Id, Name, Path, Version, Author, etc.)
- `Profiles`: User configurations
- `ProfileMods`: Many-to-many relationship (ProfileId, ModId, Enabled, Order)
- `Tasks`: Task tracking
- `DeploymentRecords`: Deployment sessions (SessionId, ProfileId, ModId, Method, Status)
- `DeploymentFiles`: Individual file tracking (DeploymentId, SourcePath, TargetPath, Method)

## Testing

Test framework: xUnit with Moq for mocking
- Unit tests: `tests/HD2Mimnt.Tests/`
- Benchmarks: `benchmarks/HD2Mimnt.Benchmarks/` (BenchmarkDotNet)
- Stability tests: `tests/HD2Mimnt.StabilityTests/` (long-running scenarios)

When writing tests:
- Use InMemory database for repository tests
- Mock external dependencies (IModRepository, ILogger, etc.)
- Follow existing test patterns (arrange-act-assert)
- Test files use descriptive method names: `MethodName_Scenario_ExpectedResult`

## Code Conventions

- **Nullable reference types** enabled project-wide
- **ImplicitUsings** enabled
- Target framework: `net10.0`
- Use async/await consistently; append `Async` suffix to async methods
- CancellationToken parameters default to `default`
- Use `ILogger<T>?` with null-conditional for logging (logger is optional)
- Entity IDs are `Guid`, timestamps are `DateTime` (UTC)
- Enums are in `HD2Mimnt.Core.Enums` namespace

## Important Paths and Entry Points

- Main entry: `src/HD2Mimnt.UI/Program.cs`
- Main window: `src/HD2Mimnt.UI/MainWindow.axaml` + `.cs`
- ViewModels: `src/HD2Mimnt.UI/ViewModels/`
- Core entities: `src/HD2Mimnt.Core/Entities/`
- Service implementations: `src/HD2Mimnt.Application/Services/`

## Common Patterns

### Deployment Workflow
1. Call `PreDeploymentCleanupAsync()` to remove previous session
2. Call `DeployProfileAsync()` with method (Copy/SymbolicLink/HardLink) and order
3. Service creates new SessionId, deploys enabled mods in order
4. Each file deployment creates `DeploymentFile` record
5. Cleanup uses tracked records to reverse deployment

### Task Creation
1. Create `ModTask` entity with Name, Type, Status
2. Add to database via `TaskService.CreateTaskAsync()`
3. Update progress via `UpdateTaskProgressAsync()`
4. Update status via `UpdateTaskStatusAsync()` when complete

### Adding a New Workflow
1. Create class implementing `IWorkflow` in `Infrastructure/Workflows/`
2. Implement `CanHandleAsync()` detection logic
3. Implement `ParseManifestAsync()` for manifest parsing
4. Implement `ValidateAsync()` for structure validation
5. Implement `GetDeploymentFilesAsync()` for file mapping
6. Register in `WorkflowRegistry`

## Current Status

Version: 0.1.0-alpha
Recent phases completed: Phase 22 (Release preparation), Phase 21 (Stability testing), Phase 20 (Performance testing), Phase 19 (AI Integration), Phase 18 (External platforms), Phase 17 (Plugin system)

All core systems are functional. The application is in early alpha with ongoing UI and feature development.
