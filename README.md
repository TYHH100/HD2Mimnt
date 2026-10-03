# HD2Mimnt

**Helldivers 2 Mod Manager**

A comprehensive mod management solution for Helldivers 2, featuring advanced support for HD2IR (nested archive) mods, profile-based configurations, and multiple deployment methods.

---

## Project Status

**Version:** 0.2.0-alpha  
**Current Phase:** Phase 5 (Performance Optimization) - Ready to start  
**Last Update:** 2026-10-03

### Recent Achievements ✅

**Phase 1-4 (HD2IR Nested Archive Support) - Completed 2026-10-03**
- ✅ **Phase 1 (P0)**: Import & Detection - Nested ZIP parsing, component detection, LogicalMod building
- ✅ **Phase 2 (P2)**: Manifest & Ownership - Manifest standardization, file ownership tracking
- ✅ **Phase 3 (P3)**: Deployment Integration - Unified deployment engine, diff-driven deployment
- ✅ **Phase 4 (P4)**: Profile Integration - LogicalMod profile integration, diff-driven profile switching
- ✅ **24/24 Acceptance Criteria Passed** (100%)

See [HD2IR Documentation Index](docs/HD2IR_DOCUMENTATION_INDEX.md) for complete documentation.

---

## Features

### Core Features
- **Mod Library Management**: Import, organize, and manage your mod collection
- **Profile System**: Create multiple profiles for different gameplay scenarios
- **Multiple Deployment Methods**: Copy, Symbolic Link, Hard Link
- **Workflow System**: Support for different mod formats (Legacy, V1, Integrated Armor)
- **Full-text Search**: Fast mod search across name, description, and author
- **Resource Explorer**: Analyze mod dependencies and resource usage

### HD2IR Support (NEW - Phase 1-4)
- **Nested Archive Parsing**: Handle arbitrarily nested ZIP files
- **Smart Component Detection**: Automatically detect HD2IR, Model, Texture, and Audio components
- **LogicalMod Abstraction**: Unified user-facing mod objects containing multiple components
- **Diff-Driven Deployment**: Only deploy changed files, skip identical content
- **Profile Integration**: LogicalMods work seamlessly with the profile system
- **Component-Level Control**: Selectively enable/disable individual components

### Advanced Features
- **Plugin System**: Extensible architecture for custom functionality
- **External Tools Integration**: Launch external tools directly from the manager
- **Task System**: Background task execution with progress tracking
- **Deployment History**: Complete audit trail of all deployment operations
- **Thumbnail Cache**: Optimized image loading for mod icons

---

## Architecture

HD2Mimnt follows Clean Architecture principles with four main layers:

```
┌─────────────────────────────────────────┐
│              UI Layer                   │
│        (Avalonia MVVM)                  │
└─────────────────┬───────────────────────┘
                  │
┌─────────────────▼───────────────────────┐
│         Application Layer               │
│    (Business Logic Services)            │
└─────────────────┬───────────────────────┘
                  │
┌─────────────────▼───────────────────────┐
│       Infrastructure Layer              │
│   (Data Access, Deployment, etc.)       │
└─────────────────┬───────────────────────┘
                  │
┌─────────────────▼───────────────────────┐
│            Core Layer                   │
│      (Entities, Interfaces)             │
└─────────────────────────────────────────┘
```

### HD2IR Four-Layer Architecture (NEW)

```
Profile Layer (Configuration Management)
    ↓
Logical Mod Layer (User Perspective)
    ↓
Import Layer (Archive Parsing)
    ↓
Deployment Engine (File Operations)
```

---

## Technology Stack

- **Framework**: .NET 10
- **UI**: Avalonia 11.2.2 (Cross-platform MVVM)
- **Database**: SQLite with WAL mode
- **Dependency Injection**: Microsoft.Extensions.DependencyInjection
- **Archive Processing**: SharpCompress
- **Testing**: xUnit, Moq
- **Benchmarking**: BenchmarkDotNet

---

## Getting Started

### Prerequisites
- .NET 10 SDK
- Windows 10/11 (primary support)
- Helldivers 2 installed

### Build and Run

```bash
# Clone the repository
git clone https://github.com/yourusername/HD2Mimnt.git
cd HD2Mimnt

# Build the solution
dotnet build

# Run the application
cd src/HD2Mimnt.UI
dotnet run

# Run tests
dotnet test

# Run benchmarks
cd benchmarks/HD2Mimnt.Benchmarks
dotnet run -c Release
```

---

## Documentation

### Quick Start
- [CLAUDE.md](CLAUDE.md) - Project overview and development guide
- [HD2IR Quick Reference](docs/HD2IR_QUICKREF.md) - API and usage examples

### HD2IR Phase 1-4 Documentation
- [Documentation Index](docs/HD2IR_DOCUMENTATION_INDEX.md) - Complete documentation navigation
- [Final Summary](docs/HD2IR_PHASE1-4_FINAL_SUMMARY.md) - Phase 1-4 overview
- [Acceptance Report](docs/HD2IR_PHASE1-4_ACCEPTANCE_REPORT.md) - Verification and acceptance
- [Implementation Report](docs/HD2IR_PHASE1-4_IMPLEMENTATION_REPORT.md) - Technical details

### Phase-Specific Reports
- [Phase 1 Complete](docs/HD2IR_PHASE1_COMPLETE.md) - Import & Detection
- [Phase 2-3 Complete](docs/HD2IR_PHASE2-3_COMPLETE.md) - Deployment Integration
- [Phase 4 Complete](docs/HD2IR_PHASE4_COMPLETE.md) - Profile Integration

---

## Key Concepts

### Traditional Mods
Standard mod packages with predefined structure, managed as single units.

### LogicalMods (NEW)
User-facing unified mod objects that may contain multiple components:
- **HD2IR Component**: Game integration layer
- **Model Component**: 3D models and assets
- **Texture Component**: Textures and materials
- **Audio Component**: Sound effects and music

### Components
Individual parts of a LogicalMod, each with its own files and configuration.

### Profiles
User-defined configurations specifying which mods/LogicalMods to deploy and in what order.

### Deployment Methods
- **Copy**: Traditional file copying
- **Symbolic Link**: Windows symbolic links (requires admin)
- **Hard Link**: NTFS hard links (same volume only)

---

## Performance Optimizations

### HD2IR Optimizations (Phase 1-4)
- **Lazy Extraction**: Archives are analyzed but not extracted until deployment
- **Fast Fingerprinting**: First+last 1KB SHA256 for quick file comparison
- **Analysis Caching**: ZIP analysis results cached to avoid redundant processing
- **Diff-Driven Operations**: Only deploy/remove changed files
- **Reference Counting**: Shared resources tracked with reference counts for safe cleanup

### Target Performance (Phase 5)
- 100MB ZIP: Analysis < 5s
- 500MB ZIP: Analysis < 20s
- 1GB ZIP: Analysis < 60s
- 5-layer nesting: No timeout
- Memory peak: < 1GB

---

## Development

### Project Structure

```
HD2Mimnt/
├── src/
│   ├── HD2Mimnt.Core/              # Domain entities and interfaces
│   ├── HD2Mimnt.Infrastructure/    # Data access and implementations
│   ├── HD2Mimnt.Application/       # Business logic services
│   ├── HD2Mimnt.UI/                # Avalonia UI (MVVM)
│   ├── HD2Mimnt.PluginSDK/         # Plugin development SDK
│   ├── HD2Mimnt.PluginHost/        # Isolated plugin host
│   └── HD2Mimnt.Worker/            # Background worker process
├── tests/
│   ├── HD2Mimnt.Tests/             # Unit tests
│   └── HD2Mimnt.StabilityTests/    # Long-running stability tests
├── benchmarks/
│   └── HD2Mimnt.Benchmarks/        # Performance benchmarks
└── docs/                            # Documentation
```

### Coding Conventions
- Nullable reference types enabled
- Implicit usings enabled
- Async/await for all I/O operations
- Repository pattern for data access
- Service layer for business logic
- MVVM pattern for UI

### Testing
- Unit tests with xUnit and Moq
- InMemory database for repository tests
- Arrange-Act-Assert pattern
- Method naming: `MethodName_Scenario_ExpectedResult`

---

## Contributing

We welcome contributions! Please follow these guidelines:
1. Follow the existing code style and conventions
2. Add unit tests for new features
3. Update documentation as needed
4. Ensure `dotnet build` succeeds with no errors
5. Run `dotnet test` to verify all tests pass

---

## License

[Your License Here]

---

## Acknowledgments

- Helldivers 2 by Arrowhead Game Studios
- HD2IR format specification
- All contributors and testers

---

## Contact

[Your Contact Information]

---

**Last Updated:** 2026-10-03  
**Phase 1-4 Status:** ✅ Complete (24/24 acceptance criteria passed)  
**Next Phase:** Phase 5 - Performance Optimization