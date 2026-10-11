# Nexus Mods v3 接入

实现以仓库的 [openapi.yaml](openapi.yaml) 为契约，服务器为 `https://api.nexusmods.com/v3`。

## 模块边界与调用

N 网实现独立组织在 `src/HD2Mimnt.Infrastructure/Platforms/NexusMods/`，通过现有 `IModPlatform` 接口调用：

| 文件 | 职责 |
| --- | --- |
| `NexusModsPlatformAdapter.cs` | 公开模块入口，实现统一平台接口、转发认证设置和操作 |
| `NexusModsApiClient.cs` | v3 路由、认证、HTTP 请求、JSON 包装及错误处理 |
| `NexusModsFileService.cs` | 模组/文件/版本查询流程、下载及进度 |
| `NexusModsFileSelector.cs` | 下载与更新检查共用的活动主版本选择规则 |
| `NexusModsMapper.cs` | API 数据转换为平台无关的 Core 模型，热门列表筛选与分页 |
| `Models/` | 按模组、文件及通用响应分组的内部 API 模型 |

内部实现均为 `internal`，模块对外保留 `HD2Mimnt.Infrastructure.Platforms.NexusModsPlatformAdapter` 的类名、命名空间和构造方式。通用 `Retry-After` 等待逻辑单独位于 `Platforms/RateLimiter.cs`；各客户端持有自己的实例。

在应用的组合入口创建并注册模块，然后交给 Application 服务调用；`HttpClient` 由创建方管理生命周期：

```csharp
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms;

using var httpClient = new HttpClient();
var nexus = new NexusModsPlatformAdapter(httpClient);
nexus.SetApiKey(apiKey); // apiKey 由调用方提供。

var registry = new PlatformRegistry();
registry.RegisterPlatform(nexus);
IModPlatform platform = registry.GetPlatform("nexusmods")!;
var mod = await platform.GetModInfoAsync("123");
```

后续 A 网建立自己的平台目录、客户端及模型，实现 `IModPlatform` 后注册到同一 `PlatformRegistry`。Application 和 UI 按平台 ID 调用，不依赖 N 网内部 API 模型或路由；各网站认证与配置由各自模块管理。

## 现有功能的映射

| 功能 | v3 请求及行为 |
| --- | --- |
| 可用性 | GET `/games/helldivers2/trending-mods`，检查公开接口，不验证账号 |
| 搜索入口 | 在热门前 5 项中按名称/摘要筛选，支持名称、作者排序及本地分页；`ScopeDescription` 标明范围，非全站搜索 |
| 模组详情 | GET `/games/helldivers2/mods/{game_scoped_id}`，读取 `data` 内的 ID、游戏 ID 和可空名称 |
| 文件列表 | 先解析模组全局 ID，再 GET `/mods/{id}/files`，随后逐文件 GET `/mod-files/{id}/versions` |
| 版本列表 | 活动类别（main/update/optional/miscellaneous）按版本号分组；默认下载版本排首位，其余按上传日期降序 |
| 下载 | 优先活动 `is_primary`，其次 main，再按上传时间；POST `/mod-file-versions/{id}/download-repacked`，从未包装的 `download_url` 读取签名地址后 GET 存档 |
| 更新检查 | 详情缺少版本号时使用版本列表首项；没有可用版本时返回未知，不报告更新 |

保留现有 `IModPlatform` 接口；调用方传入网站上的游戏范围模组 ID（例如 `123`）。全局 ID 存在 `PlatformModInfo.Metadata["nexus_global_mod_id"]` 中，不能与网站 ID 混用。`PlatformModFile.Id` 是文件**版本**的全局 ID。

详情模型不提供作者、摘要、版本号、下载次数或日期，文件版本模型不提供存档大小及描述：这些字段保留空值/零值，零大小表示未知。签名地址只在下载时获取，不缓存为永久地址。没有把上传、集合编辑等写入接口扩展为管理器功能。

## 认证与错误

`SetApiKey(key)` 和 `SetBearerToken(jwt)` 互斥切换；JWT 由外部取得，此适配器不负责登录或签发。`ClearApiKey()` 仅清除 Key，`ClearAuthentication()` 清除两种认证。

凭据只放在需要认证的 API 请求中，不修改 `HttpClient.DefaultRequestHeaders`，公开热门接口和签名存档请求不携带适配器凭据。建议使用不预设认证头的专用 `HttpClient`。

API 错误读取 `application/problem+json` 的 `title`、`detail`，并保留 HTTP 状态码。429 后遵循 `Retry-After` 限制后续 API 请求；当前请求向调用方报告失败，不自动重试下载 POST。等待与网络读取支持取消。v3 契约没有 v1 配额头或 `/users/validate.json`，不继续依赖它们。

所用模组读取、热门列表和下载接口在文档中标为 Experimental，后续更新应重新对照契约。

测试认证入口位于“更多 → 开发工具”：粘贴 API 密钥后点击“应用密钥”，即可供注册的 N 网平台实例使用。输入默认遮罩，应用后清空输入框；关闭开发工具不会清除认证，点击“清除密钥”可移除 API Key。密钥仅保存在本次运行的内存中，重启后需重新填写，不写入设置或日志。“已配置”表示已应用认证，不表示服务器已验证密钥有效性。

## 验证与来源

契约回归使用模拟 HTTP 响应，检查实际 URL、方法、认证头、响应包装、ID 转换、主文件选择、错误及取消。运行：

```powershell
dotnet build HD2Mimnt.slnx --no-restore
dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --filter "FullyQualifiedName~Platforms|FullyQualifiedName~UpdateCheckerServiceTests|FullyQualifiedName~CachingPlatformServiceTests|FullyQualifiedName~DownloadManagerServiceTests"
```

外部参考：[Nexus Mods API 文档](https://api-docs.nexusmods.com/)、[API Key 管理](https://www.nexusmods.com/settings/api-keys)、[RFC 9457 Problem Details](https://www.rfc-editor.org/rfc/rfc9457)、[Retry-After](https://www.rfc-editor.org/rfc/rfc9110.html#name-retry-after)。接口字段以本仓库提供的 OpenAPI 快照为准。
