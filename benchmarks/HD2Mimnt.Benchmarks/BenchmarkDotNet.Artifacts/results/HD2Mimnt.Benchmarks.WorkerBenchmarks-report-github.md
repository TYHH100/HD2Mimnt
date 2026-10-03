```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12400F 2.50GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                 | taskCount | Mean         | Error        | StdDev        | Rank | Gen0      | Gen1      | Gen2      | Allocated   |
|----------------------- |---------- |-------------:|-------------:|--------------:|-----:|----------:|----------:|----------:|------------:|
| **CreateTask**             | **?**         |     **563.5 μs** |      **8.32 μs** |       **7.78 μs** |    **1** |         **-** |         **-** |         **-** |     **3.68 KB** |
| QueryTaskStatus        | ?         |     583.1 μs |      8.60 μs |       7.62 μs |    1 |         - |         - |         - |     5.48 KB |
| UpdateTaskProgress     | ?         |   5,719.6 μs |    113.98 μs |     111.94 μs |    3 |         - |         - |         - |     38.6 KB |
| GetAllTasks            | ?         | 278,558.2 μs | 20,003.59 μs |  58,981.04 μs |    4 | 5071.4286 | 3000.0000 | 1000.0000 | 39983.21 KB |
| CancelTask             | ?         |   1,119.1 μs |     21.82 μs |      25.12 μs |    2 |         - |         - |         - |     7.25 KB |
| GetTaskHistory         | ?         | 364,235.7 μs | 73,561.58 μs | 216,898.00 μs |    5 | 5250.0000 | 2250.0000 |  875.0000 | 44828.42 KB |
| **ConcurrentTaskCreation** | **10**        |           **NA** |           **NA** |            **NA** |    **?** |        **NA** |        **NA** |        **NA** |          **NA** |
| **ConcurrentTaskCreation** | **50**        |           **NA** |           **NA** |            **NA** |    **?** |        **NA** |        **NA** |        **NA** |          **NA** |

Benchmarks with issues:
  WorkerBenchmarks.ConcurrentTaskCreation: DefaultJob [taskCount=10]
  WorkerBenchmarks.ConcurrentTaskCreation: DefaultJob [taskCount=50]
