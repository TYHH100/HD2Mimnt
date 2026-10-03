```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12400F 2.50GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                           | Mean        | Error     | StdDev    | Rank | Gen0     | Gen1    | Allocated |
|--------------------------------- |------------:|----------:|----------:|-----:|---------:|--------:|----------:|
| QueryById                        |    15.98 μs |  0.157 μs |  0.147 μs |    1 |   0.5493 |       - |   5.15 KB |
| QueryAll_1000Records             | 3,317.84 μs | 62.660 μs | 52.324 μs |    7 | 101.5625 | 74.2188 | 937.19 KB |
| QueryByCategory                  |   762.48 μs |  9.163 μs |  7.652 μs |    6 |  20.5078 |  5.8594 | 191.77 KB |
| InsertSingleMod                  |   722.25 μs | 13.340 μs | 11.825 μs |    5 |        - |       - |   5.45 KB |
| UpdateModMetadata                |   620.26 μs |  9.308 μs |  8.706 μs |    4 |   0.9766 |       - |   10.2 KB |
| ComplexJoinQuery_ProfileWithMods |    88.65 μs |  0.626 μs |  0.522 μs |    2 |   3.1738 |       - |  30.13 KB |
| SearchByName                     |   432.20 μs |  8.595 μs |  8.826 μs |    3 |   9.7656 |  0.9766 |  97.73 KB |
