```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12400F 2.50GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                 | taskCount | Mean       | Error     | StdDev    | Rank | Gen0     | Gen1     | Allocated  |
|----------------------- |---------- |-----------:|----------:|----------:|-----:|---------:|---------:|-----------:|
| **CreateTask**             | **?**         |   **1.043 ms** |  **1.686 ms** | **0.0924 ms** |    **1** |        **-** |        **-** |    **3.65 KB** |
| QueryTaskStatus        | ?         |   1.026 ms |  1.276 ms | 0.0699 ms |    1 |        - |        - |    5.45 KB |
| UpdateTaskProgress     | ?         |  10.197 ms | 55.032 ms | 3.0165 ms |    3 |        - |        - |   38.57 KB |
| GetAllTasks            | ?         |  56.732 ms | 21.623 ms | 1.1852 ms |    4 | 200.0000 | 100.0000 | 2665.28 KB |
| CancelTask             | ?         |   2.208 ms | 13.095 ms | 0.7178 ms |    2 |        - |        - |    7.22 KB |
| GetTaskHistory         | ?         | 105.700 ms | 42.447 ms | 2.3267 ms |    5 | 333.3333 | 166.6667 | 3337.93 KB |
| **ConcurrentTaskCreation** | **10**        |         **NA** |        **NA** |        **NA** |    **?** |       **NA** |       **NA** |         **NA** |
| **ConcurrentTaskCreation** | **50**        |         **NA** |        **NA** |        **NA** |    **?** |       **NA** |       **NA** |         **NA** |

Benchmarks with issues:
  WorkerBenchmarks.ConcurrentTaskCreation: ShortRun(IterationCount=3, LaunchCount=1, WarmupCount=3) [taskCount=10]
  WorkerBenchmarks.ConcurrentTaskCreation: ShortRun(IterationCount=3, LaunchCount=1, WarmupCount=3) [taskCount=50]
