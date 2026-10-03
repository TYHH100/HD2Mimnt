```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i5-12400F 2.50GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                        | count | Mean       | Error     | StdDev    | Rank | Allocated |
|------------------------------ |------ |-----------:|----------:|----------:|-----:|----------:|
| **GenerateThumbnail_SmallImage**  | **?**     |  **10.207 ms** | **0.1954 ms** | **0.2006 ms** |    **2** |     **800 B** |
| GenerateThumbnail_MediumImage | ?     |  19.715 ms | 0.3627 ms | 0.3393 ms |    3 |     800 B |
| GenerateThumbnail_LargeImage  | ?     | 115.897 ms | 1.1924 ms | 1.0570 ms |    4 |     800 B |
| LoadImageFromDisk             | ?     |   4.532 ms | 0.0515 ms | 0.0456 ms |    1 |    4693 B |
| **BatchLoadImages**               | **10**    | **126.907 ms** | **1.2093 ms** | **1.0720 ms** |    **5** |   **47284 B** |
| **BatchLoadImages**               | **50**    | **673.690 ms** | **7.6251 ms** | **6.7594 ms** |    **6** |  **235664 B** |
