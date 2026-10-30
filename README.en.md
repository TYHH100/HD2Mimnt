# HD2Mimnt

[中文](README.md) | **English**

A Helldivers 2 mod manager built on .NET 10 and Avalonia, primarily targeting Windows 10/11.

## Key Features

- **Mod library management**: import, organize and search mods, with support for the nested-archive HD2IR format
- **Configuration system**: multiple profiles for the mods enabled in different scenarios, switched with one click
- **Several deployment methods**: copy, symbolic links, hard links
- **Background tasks**: scanning, importing and deployment run asynchronously with progress reporting and cancellation
- **Plugin system**: an SDK plus a separate host process, so plugins run isolated

## Tech Stack

.NET 10 · Avalonia · SQLite · CommunityToolkit.MVVM · SharpCompress

## Build and Run

Requires the .NET 10 SDK.

```powershell
dotnet build HD2Mimnt.slnx
dotnet run --project src/HD2Mimnt.UI/HD2Mimnt.UI.csproj

# unit tests and UI tests
dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build
dotnet test tests/HD2Mimnt.UITests/HD2Mimnt.UITests.csproj --no-build
```

## Project Structure

| Project | Responsibility |
| --- | --- |
| `HD2Mimnt.Core` | Domain entities and interfaces |
| `HD2Mimnt.Infrastructure` | Data access, deployment implementations, workflows |
| `HD2Mimnt.Application` | Business services and orchestration |
| `HD2Mimnt.UI` | Avalonia UI (MVVM) |
| `HD2Mimnt.PluginSDK` / `PluginHost` | Plugin SDK and isolated host process |
| `HD2Mimnt.Worker` | Background worker process |

Under `tests/` there are four test projects: unit tests (Tests), UI tests (UITests), stability tests (StabilityTests) and performance tests (PerformanceTests).

## Documentation

- [Documentation navigation](docs/README.md)
- [Release process](docs/RELEASE_PROCESS.md)
- [Contribution rules](AGENTS.md)

## License

[MIT](LICENSE)
