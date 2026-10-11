# 后端多线程下载模块

模块采用异步并发 HTTP Range 请求，不为每个分段创建专用线程。
Core 的 `IHttpDownloadService` 定义接口，Infrastructure 的
`Downloads/HttpDownloadService` 实现传输。应用 DI 已注册接口，
Nexus Mods 与 GitHub 下载入口默认复用此实现，保留原有 `Task<Stream>` 契约。

设置页的独立“下载”Tab 可以调整：并发连接数（1–16）、最小分段大小（1–256 MiB）、
分段重试次数（0–10）、重试间隔（0–60000 毫秒）、断点续传开关（默认开启）。
保存后，新下载和重新尝试的下载读取最新设置，正在进行的传输保持启动时的快照。
此 Tab 可以独立保存，不要求先配置有效游戏路径；“恢复默认”只重置下载设置。

## 使用

```csharp
// 从 DI 获取 IHttpDownloadService，或使用由调用方维护生命周期的 HttpClient。
IHttpDownloadService downloader = new HttpDownloadService(httpClient);
await downloader.DownloadToFileAsync(
    new Uri("https://example.com/mod.zip"),
    targetPath,
    new DownloadOptions
    {
        MaxParallelSegments = 4,
        MinimumSegmentSize = 4 * 1024 * 1024,
        MaxRetries = 2,
        RetryDelay = TimeSpan.FromMilliseconds(500),
        EnableResume = true
    },
    progress,
    cancellationToken);
```

`DownloadAsync` 返回位置为 0 的可定位临时文件流，调用方必须 `using` / `await using`，
释放后自动删除完成文件；`DownloadToFileAsync` 在目标目录创建临时文件，
全部完成后重命名替换目标，失败或取消保留原目标。
开启续传且服务器支持时，保留中断文件及进度；关闭续传或无法安全续传时清理中断文件。
现有 `DownloadManagerService` 也按此方式发布平台返回的流。
取消任务后若需要等待流释放及进度保存/清理，调用 `WaitForDownloadAsync(taskId)`；
`Cancelled` 状态表示取消已请求，不表示后台已经退出。再次提交同一平台文件会使用已保存的断点。

## 断点续传

- 保存每个分段的起止位置及已写入偏移，重试从剩余字节开始。
  完成的分段不再请求；即使重启进程，只要断点文件仍在就可重新尝试。
- 数据先落盘，再原子替换 JSON 进度文件。每约 1 MiB 写入保存一次，正常取消或异常退出执行体时
  再保存最后进度。进程被强制结束时，可能重新下载最后一个未保存区间。
  预分配文件的长度不能代表已完成进度。
- 开始时重新读取远端长度与强 ETag / Last-Modified；文件版本、长度、下载身份或进度格式
  不一致、数据文件缺失时从头下载。不支持 Range 或缺少可靠版本标识时完整下载。
- `DownloadToFileAsync` 的断点为目标路径旁的 `.hd2download.part` 和 `.hd2download.part.json`；
  `DownloadAsync` 的断点放在应用数据目录的 `Downloads` 下。
  默认构造器使用 `%LOCALAPPDATA%/HD2Mimnt/Downloads`，应用 DI 遵循 `HD2MIMNT_DATA_PATH`。
- 直接 URL 下载默认按完整 URL 的 SHA-256 关联；URL 中的签名/凭据不会明文写入进度文件。
  如果下载链接会变，调用时提供相同的 `resumeKey`，它必须标识同一个文件版本。
  Nexus Mods 使用文件版本 ID，GitHub 使用仓库及 release asset ID；每次重新尝试会重新获取地址，
  过期链接失败保留原断点，但不会在本次传输中自动刷新认证。
- 同一断点同一时刻只允许一个下载者，防止互相覆盖。恢复旧分段布局时仍遵守新的并发上限。
  续传速度只统计本次实际写入字节，不把之前已下载的数据算成当前速度。
- 下载成功删除进度文件；中断时保留的文件是续传数据，尚未实现自动过期回收或清理按钮。

## 行为与边界

- 默认每个文件最多 4 个分段，每段至少 4 MiB；并发数允许 1–16。
  实际分段数受文件大小约束。例如默认 8 MiB 文件使用 2 个分段。
- 首次 GET 读取响应头；文件长度已知、声明 `Accept-Ranges: bytes`，
  且提供强 ETag 或 Last-Modified 时才分段，否则直接使用该响应单连接下载。
  首次响应在分段开始前释放。
- 分段携带 `Range`、`If-Range`、`Accept-Encoding: identity`，
  必须返回匹配的 HTTP 206、Content-Range、长度及未编码内容。
  返回 HTTP 200 或 416 时，停止并等待全部分段退出后重新完整下载。
  其它不合法分段拒绝发布。
- 分段网络错误、短读、HTTP 408 / 429 / 5xx 最多额外重试 2 次；
  认证失败、范围错误不重试。取消会传入 HTTP 请求、流读写和重试等待。
  首次 GET 与单连接回退失败直接向调用方报告。
- 进度按已写入分段的最高偏移累计，重试不会重复计数；提供总字节数、
  已下载字节数及平均写入速度。完整回退会重置进度；未知长度期间百分比为 0，完成时为 100。
- HttpClient 由注入方管理，服务不释放它。不要将平台 API 凭据加入下载客户端默认头；
  平台认证只附加在 API 请求上。DI 使用专用的下载客户端。
- 当前提供持久化断点续传、自动回退与分段重试；还没有独立暂停按钮、队列历史持久化或全局带宽限制。
  并发上限按文件计算，现有下载队列仍可同时运行多个任务。

## 验证

```powershell
dotnet build HD2Mimnt.slnx --no-restore
dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~HttpDownloadServiceTests|FullyQualifiedName~DownloadManagerServiceTests|FullyQualifiedName~NexusModsPlatformAdapterTests|FullyQualifiedName~GitHubPlatformAdapterTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~SettingsServiceTests"
dotnet test tests/HD2Mimnt.UITests/HD2Mimnt.UITests.csproj --no-build --no-restore --filter "FullyQualifiedName~DownloadSettingsTab_Should_Save_Through_Controls_And_Configure_Runtime_Downloader"
```

测试覆盖请求并发重叠、完整字节拼接、范围/版本校验、重试、单连接回退、
取消后等待全部写入者退出、原目标保留、临时文件清理及完成后延迟进度保护。
另覆盖新服务续传、签名 URL 更新、长度/版本变化、损坏断点、并发访问防护及设置驱动实际请求。
设置页用 Headless 在实际 DI 下点击分类、输入控件、切换续传开关并点击保存。
HTTP 契约测试使用本地模拟处理器，不代表真实 CDN 的吞吐量或加速倍数。

参考：
- [HTTP Range 与 If-Range 规范（RFC 9110）](https://www.rfc-editor.org/rfc/rfc9110.html#section-14)
- [.NET RandomAccess.WriteAsync](https://learn.microsoft.com/dotnet/api/system.io.randomaccess.writeasync)
- [.NET 任务取消](https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation)
