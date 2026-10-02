```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12400F 2.50GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                           | Mean        | Error     | StdDev    | Rank | Gen0     | Gen1    | Allocated |
|--------------------------------- |------------:|----------:|----------:|-----:|---------:|--------:|----------:|
| QueryById                        |    16.57 μs |  0.208 μs |  0.174 μs |    1 |   0.5493 |       - |   5.15 KB |
| QueryAll_1000Records             | 3,431.31 μs | 67.384 μs | 92.236 μs |    6 | 101.5625 | 74.2188 | 937.19 KB |
| QueryByCategory                  |   732.31 μs |  7.274 μs |  5.679 μs |    5 |  20.5078 |  5.8594 | 191.77 KB |
| InsertSingleMod                  |   555.79 μs |  8.560 μs |  8.007 μs |    4 |        - |       - |   5.45 KB |
| UpdateModMetadata                |   501.70 μs |  4.285 μs |  3.799 μs |    3 |   0.9766 |       - |   10.2 KB |
| ComplexJoinQuery_ProfileWithMods |    89.70 μs |  0.712 μs |  0.631 μs |    2 |   3.1738 |       - |  30.13 KB |
| SearchByName                     |   539.90 μs |  6.721 μs |  6.287 μs |    4 |  11.7188 |  1.9531 | 108.26 KB |
