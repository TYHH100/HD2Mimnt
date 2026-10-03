```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12400F 2.50GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                        | Mean       | Error    | StdDev   | Rank | Gen0     | Gen1    | Allocated  |
|------------------------------ |-----------:|---------:|---------:|-----:|---------:|--------:|-----------:|
| SearchByName_SingleKeyword    |   235.9 μs |  2.20 μs |  2.06 μs |    1 |   5.1270 |       - |    49.3 KB |
| SearchByName_PartialMatch     | 1,743.6 μs | 19.34 μs | 16.15 μs |    3 |  35.1563 |  3.9063 |  339.15 KB |
| SearchByAuthor                |   231.5 μs |  4.59 μs |  4.91 μs |    1 |   5.1270 |       - |   47.52 KB |
| SearchByCategory              |   772.8 μs | 14.07 μs | 13.16 μs |    2 |  25.3906 |  8.7891 |  239.88 KB |
| SearchByTag                   |   230.2 μs |  3.20 μs |  3.00 μs |    1 |   5.1270 |       - |   47.44 KB |
| SearchByMultipleTags          |   235.6 μs |  2.51 μs |  2.35 μs |    1 |   5.1270 |       - |   47.46 KB |
| SearchComplex_NameAndCategory | 1,987.9 μs | 39.32 μs | 65.69 μs |    4 |  35.1563 |  3.9063 |  339.15 KB |
| EmptySearch_ReturnAll         | 3,366.4 μs | 27.69 μs | 24.55 μs |    5 | 128.9063 | 89.8438 | 1192.55 KB |
| SearchNoResults               |   234.3 μs |  2.90 μs |  2.71 μs |    1 |   5.1270 |       - |   47.62 KB |
| SearchWithPagination          | 1,695.8 μs | 16.05 μs | 14.23 μs |    3 |  35.1563 |  3.9063 |  339.15 KB |
