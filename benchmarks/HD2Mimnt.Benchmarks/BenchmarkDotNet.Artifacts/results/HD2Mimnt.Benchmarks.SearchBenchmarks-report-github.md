```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12400F 2.50GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                        | Mean       | Error    | StdDev   | Median     | Rank | Gen0     | Gen1    | Allocated  |
|------------------------------ |-----------:|---------:|---------:|-----------:|-----:|---------:|--------:|-----------:|
| SearchByName_SingleKeyword    |   240.2 μs |  4.26 μs |  4.73 μs |   239.7 μs |    1 |   4.8828 |       - |    49.3 KB |
| SearchByName_PartialMatch     | 1,900.8 μs | 37.69 μs | 77.84 μs | 1,869.6 μs |    3 |  35.1563 |  3.9063 |  339.15 KB |
| SearchByAuthor                |   253.4 μs |  4.87 μs |  5.80 μs |   254.0 μs |    1 |   4.8828 |       - |   47.52 KB |
| SearchByCategory              |   826.4 μs | 15.24 μs | 22.82 μs |   820.7 μs |    2 |  23.4375 |  7.8125 |  239.88 KB |
| SearchByTag                   |   249.4 μs |  4.80 μs |  4.01 μs |   251.1 μs |    1 |   4.8828 |       - |   47.44 KB |
| SearchByMultipleTags          |   251.3 μs |  3.28 μs |  2.91 μs |   251.8 μs |    1 |   4.8828 |       - |   47.46 KB |
| SearchComplex_NameAndCategory | 1,923.9 μs | 29.47 μs | 27.57 μs | 1,925.8 μs |    3 |  35.1563 |  3.9063 |  339.15 KB |
| EmptySearch_ReturnAll         | 3,674.3 μs | 68.11 μs | 63.71 μs | 3,670.3 μs |    4 | 128.9063 | 89.8438 | 1192.55 KB |
| SearchNoResults               |   261.6 μs |  5.18 μs |  8.37 μs |   263.2 μs |    1 |   4.8828 |       - |   47.62 KB |
| SearchWithPagination          | 1,893.5 μs | 35.39 μs | 33.10 μs | 1,888.9 μs |    3 |  35.1563 |  3.9063 |  339.15 KB |
