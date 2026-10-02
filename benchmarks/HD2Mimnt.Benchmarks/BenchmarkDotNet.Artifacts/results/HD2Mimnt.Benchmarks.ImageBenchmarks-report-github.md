```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12400F 2.50GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                        | count | Mean       | Error      | StdDev    | Rank | Allocated |
|------------------------------ |------ |-----------:|-----------:|----------:|-----:|----------:|
| **GenerateThumbnail_SmallImage**  | **?**     |  **11.854 ms** |   **9.612 ms** | **0.5269 ms** |    **2** |     **800 B** |
| GenerateThumbnail_MediumImage | ?     |  21.104 ms |   3.291 ms | 0.1804 ms |    3 |     800 B |
| GenerateThumbnail_LargeImage  | ?     | 123.377 ms |   9.186 ms | 0.5035 ms |    4 |     832 B |
| LoadImageFromDisk             | ?     |   4.846 ms |   1.918 ms | 0.1051 ms |    1 |    4693 B |
| **BatchLoadImages**               | **10**    | **132.077 ms** |  **13.041 ms** | **0.7148 ms** |    **4** |   **47332 B** |
| **BatchLoadImages**               | **50**    | **714.192 ms** | **115.265 ms** | **6.3181 ms** |    **5** |  **235664 B** |
