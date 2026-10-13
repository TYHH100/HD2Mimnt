# Nexus Mods 接入

实现以仓库的 [nexus_openapi.yaml](../references/nexus_openapi.yaml) 为契约，服务器为 `https://api.nexusmods.com/v3`。

网页 `nxm://` 授权下载单独依据 [Nexus Mods v1 Swagger 文档](https://app.swaggerhub.com/apis-docs/NexusMods/nexus-mods_public_api_params_in_form_data/1.0?view=elementsDocs) 实现，不向 v3 repacked 接口拼接 v1 授权参数。

## 模块边界与调用

N 网实现独立组织在 `src/HD2Mimnt.Infrastructure/Platforms/NexusMods/`，通过现有 `IModPlatform` 接口调用：

| 文件 | 职责 |
| --- | --- |
| `NexusModsPlatformAdapter.cs` | 公开模块入口，实现统一平台接口、转发认证设置和操作 |
| `NexusModsApiClient.cs` | v1/v3 路由、v2 GraphQL、认证、HTTP 请求及错误处理 |
| `NexusModsFileService.cs` | 模组/文件/版本查询流程、下载及进度 |
| `NexusModsFileSelector.cs` | 下载与更新检查共用的活动主版本选择规则 |
| `NexusModsMapper.cs` | API 数据转换为平台无关的 Core 模型，热门列表筛选与分页 |
| `NexusModsNxmLink.cs` / `NexusModsNxmService.cs` | v1 网站文件列表、nxm 链接校验、同账户验证、临时授权及 Premium 直接下载 |
| `NexusModsBrowseService.cs` | v1 最新发布、最近更新和完整详情，v3 公开热门列表、v2 GraphQL 模组名或作者搜索 |
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
| 旧 `IModPlatform.SearchAsync` | 在热门前 5 项中按名称/摘要筛选，支持名称、作者排序及本地分页；`ScopeDescription` 标明范围，非全站搜索 |
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

链接窗口与更新/下载窗口统一显示失败步骤（例如“获取模组文件列表”或“获取下载链接”）、HTTP 状态、处理建议及服务端原始说明。API 401 引导重新应用密钥；403 只说明访问被拒绝，不能直接推断密钥无效或需要会员。信息读取失败与下载链接被拒绝使用不同建议；存档服务器认证失败则引导获取新下载链接。服务端未提供具体原因时明确注明，认证值回显会隐藏，不把签名下载地址作为错误详情展示。

所用模组读取、热门列表和下载接口在文档中标为 Experimental，后续更新应重新对照契约。

测试认证入口位于“更多 → 开发工具”：粘贴 API 密钥后点击“保存并应用密钥”，加密保存成功后供注册的 N 网平台实例使用。输入默认遮罩，保存成功后清空输入框；关闭开发工具不会清除认证，点击“清除密钥”会先删除数据库中的密文，再清除内存认证，避免下次启动恢复已清除的密钥。保存或删除失败保留原凭据并提示重试。“已配置”表示已应用认证，不表示服务器已验证密钥有效性。

`PlatformCredentials` 表按平台 ID 保存 `ProtectedApiKey` BLOB，通过 Windows DPAPI `CurrentUser` 保护，并用应用/平台标识作为附加熵，不另存解密密码或明文密钥。不通过 `SettingsService` 保存，不写入 settings.json、模组绑定或日志。主窗口初始化时自动解密并应用，备份恢复后重新加载认证；没有保存记录时保持未配置。密文损坏或在无法解密的 Windows 用户环境恢复时提示重新填写，启动继续进行，不自动删除密文。数据库备份包含密文；原有普通模组数据保留。依据：[Windows ProtectedData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata)、[DataProtectionScope.CurrentUser](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.dataprotectionscope)。

依据 [API Acceptable Use Policy](https://help.nexusmods.com/article/114-api-acceptable-use-policy)，个人 API Key 可用于开发测试或个人使用。若计划面向普通用户公开提供 API 功能，在有可演示 API 用途的测试构建后，应联系 `support@nexusmods.com` 注册应用；注册后可选择接入 SSO。不得以个人 Key 替代正式应用认证、将用户 Key 存在开发者服务器上或在没有用户发起操作时使用，也不得批量抓取数据用于重新托管。所有 API 请求携带稳定的 `Application-Name: HD2Mimnt` 和来自 UI 程序集当前版本的 `Application-Version`；签名存档下载不附带这些 API 专用头或密钥。

## 应用内浏览模组

顶部“浏览模组”位于模组库右侧，使用 FluentAvalonia 列表和详情面板；`Ctrl+6` 打开，原有快捷键保留。首次进入读取公开热门列表，后续切换保留搜索结果和详情，点击“加载列表”刷新当前来源。三个来源的范围明确显示：热门为 v3 `trending-mods` 前 5 项；最新发布和最近更新为 v1 `latest_added.json` / `latest_updated.json` 各 10 项。输入模组名或作者后点击“搜索”或按 Enter，调用官方 v2 GraphQL `mods` 搜索；限定 `gameDomainName=helldivers2`，名称与作者以 OR 匹配。每页 20 项，按服务器 `totalCount` 提供上一页/下一页；编辑输入不会本地过滤列表，也不改变已提交的翻页关键词。“加载列表”回到所选来源。公开搜索允许未配置密钥时使用；旧 `IModPlatform.SearchAsync` 的有限热门兼容行为保留。

最新发布、最近更新、v1 `{id}.json` 完整详情及文件列表共用“工具 → 开发工具”已配置的 N 网密钥。可以从卡片查看详情，也可以直接输入 Helldivers 2 模组链接或正整数编号。API 模型仍留在 Nexus 模块，通过 `IModBrowsePlatform` 和 Application 的 `ModBrowseService` 返回通用模型；图片加载使用独立无认证 HTTP 客户端，不阻塞列表交互。

“下载压缩包…”先选择保存位置，两种下载按钮均通过 v1 `/users/validate.json` 检查 `is_premium`。Premium 会员调用所选网站文件的 v1 `download_link.json`，不传 `key/expires`，直接 API 下载；Supporter 或免费账户打开所选文件的网页等待 NXM 回传。会员状态读取失败会显示错误，不猜测权限；真正下载前再次校验会员和所选文件的编号及状态。复用已有账户校验及 `DownloadManagerService`。首次下载不要求先创建本地 Mod；保存后拖入窗口进入现有导入流程。错误游戏、模组和文件的回传不能继续下载；取消、离开页面和退出等待下载清理，后来的回传不能复活已取消任务。临时 NXM 链接及签名下载地址不写入数据库或历史。该链路的回归使用模拟 API/存档响应，真实账户授权仍依赖用户 N 网账户和本机浏览器。

来源：[Nexus Mods v1 官方契约](https://app.swaggerhub.com/apis-docs/NexusMods/nexus-mods_public_api_params_in_form_data/1.0)、[仓库 v3 契约](../references/nexus_openapi.yaml)、[官方客户端 GraphQL 地址](https://github.com/Nexus-Mods/node-nexus-api/blob/master/src/parameters.ts)、[官方 GraphQL 接口](https://api.nexusmods.com/v2/graphql)。2026-10-13 通过接口 schema 与匿名请求验证：按作者 CowboyBingus 返回本游戏 4 项；`WILDCARD` 传原始关键词，不额外添加星号。

## 浏览页导入与图片

浏览页也提供“下载并导入”：复用同一会员/网页授权判断与所选文件校验，下载到配置的临时目录，再进入现有导入浮窗，完成后刷新模组库。导入失败或取消不显示成功；临时压缩包在导入流程退出后清理，已有成功导入项保留。不会自动部署或切换到模组库。

列表封面可点击预览，详情中也有“预览封面”；按 API 的 `picture_url` 源地址完整解码，独立于 240px 列表缩略图。覆盖层关闭、刷新和离页释放源图，加载期间可按 Escape 取消，图片请求不携带 API 凭据。单张图片目前最多 4 MiB。

2026-10-13 核对 [v1 Swagger JSON](https://api.swaggerhub.com/apis/NexusMods/nexus-mods_public_api_params_in_form_data/1.0) 与 [当前 v3 OpenAPI](https://api.nexusmods.com/openapi.yaml)：v3 将 `picture_url` 定义为模组主图地址，两份公开 REST 契约都未列出模组画廊列表接口。其他图片通过“网页查看其他图片”打开模组的 Images 页；不将封面接口冒充画廊，也不猜测未公开接口。

## 模组库页面关联

模组卡片右键选择“模组链接…”，输入 `https://www.nexusmods.com/helldivers2/mods/16633` 形式的地址，点击“获取信息”后预览并“保存关联”。只接受 Nexus Mods 官方主域名或 www 主机、Helldivers 2 页面和正整数页面编号；移除查询参数和片段，保存规范 HTTPS 页面地址。A 网等待文档恢复后单独接入，当前不猜测其路由或 API。

API 查询由用户点击发起，复用开发工具保存或启动时恢复的密钥。详情提供远端名称及全局模组 ID，版本由活动文件版本接口取得；API 未提供的名称或版本显示未知，不猜测作者、摘要等字段，也不修改本地名称、作者和已安装版本。查询失败或取消、修改输入、关闭弹窗都不会覆盖已有绑定。

数据库在 `Mods` 同一行保存 `PlatformId`、网站编号 `PlatformModId`、远端版本 `PlatformVersion`、`LastUpdateCheck` 和 `PlatformLinkJson`（规范页面、游戏、全局 ID、远端名称、版本及获取时间）。旧库仅补列；更新检查和下载继续使用已保存的平台 ID 与网站编号，不能把全局 ID 当成网站编号。删除 Mod 行时关联同时消失，不另存孤立关联，模组关联不保存 API 密钥或签名下载地址；API 密钥由独立凭据表加密保存。

## 模组更新与下载

保存关联后，模组库右键提供统一的“检查和更新模组…”入口，进入文件选择窗口。打开时由本次用户操作检查活动文件，默认选择主文件，也可选择可选文件。版本号不同只表示版本不同，不推断一定更新；API 未提供版本时明确显示未知，允许下载压缩包但禁止直接更新。检查成功保存远端版本及检查时间，失败会显示错误，不当作“已最新”。

- “下载压缩包…”选择本地保存位置，只保存所选文件，不改变已安装版本。
- “下载并更新”下载所选文件版本、解压并准备独立副本，检查存在合法主补丁，计算完整 Hash；成功后更新原 Mod 行的文件路径与本地版本，不删除并重新导入 Mod。
- 原模组 ID、名称、作者、用户标签、导入时间及所有配置关联（启用、顺序、选项）保留。新版本选项可能变化，更新后需检查配置选项。
- 已部署时先按追踪记录清理旧游戏文件；清理失败停止替换并保留记录供重试。成功更新后需重新部署。清理旧部署后发生取消或写库失败时，原库内文件仍保留，但已清理的部署需要重新部署。
- 数据库保存成功后再清理旧库内副本，失败或取消清理未提交副本。窗口提供进度和取消，关闭窗口、离开页面及退出应用等待当前操作结束和临时文件清理；数据库提交阶段完成后再响应关闭。

直接 API 下载使用不可变的 v3 文件版本全局 ID，下载前再次检查该版本仍可下载，不因为默认主文件变化而换成其它版本。成功安装后将 `InstalledFileVersionId` 保存进原 `PlatformLinkJson`，远端默认版本与实际安装版本分别保存；删除模组时绑定一起删除。签名地址仅在下载阶段获取，不持久化。

## 免费账户下载与更新

N 网更新窗口默认开启“网页授权下载（免费账户）”。检查更新使用 v1 `/games/helldivers2/mods/{网站模组编号}/files.json`，以活动主文件优先并按上传时间排序，不需要临时下载授权。已安装网页文件编号与所选编号相同时显示已安装；不同编号或版本只表示所选文件不同，不凭版本字符串推断高低。

1. 首次使用时在开发工具保存并应用 API 测试密钥，后续启动自动恢复，网页登录同一个 N 网账户。
2. 选择远端文件，点击“下载压缩包…”（先选择保存位置）或“下载并更新”。应用按 API 的网站文件编号打开 `https://www.nexusmods.com/helldivers2/mods/{mod_id}?tab=files&file_id={file_id}&nmm=1`，等待网页授权。
3. 在网页完成下载确认，浏览器提示打开 `nxm` 链接时选择 **HD2Mimnt**。无需复制粘贴链接。
4. 应用接收回传，检查游戏、模组、所选文件及有效期，再调用 v1 `/users/validate.json` 比较 API 密钥所属账户与 `user_id`。校验通过后自动继续原来的下载或替换动作；下载前再次验证并兑换 v1 `/games/helldivers2/mods/{mod_id}/files/{file_id}/download_link.json` 返回的 `URI`。

Windows 在主实例启动及发起网页下载时先检查当前用户的 `HD2Mimnt.nxm` ProgID、RegisteredApplications 和 URLAssociations，无需管理员权限。已有其它管理器的默认 `nxm` 处理器不被覆盖；无已有处理器时添加协议入口。若浏览器仍打开其它管理器，可在 Windows 默认应用中将 NXM 链接的处理应用改为 HD2Mimnt。注册完整且路径一致时直接跳过，不重复写入或通知 Windows 刷新；首次注册、条目缺失或构建目录移动时才更新 ProgID、应用选择器、图标和属于本应用的协议打开命令，并刷新关联缓存；注册命令引用当前可执行文件；通过 `dotnet` 启动时同时引用应用 DLL，路径和协议参数均加引号。

启动参数与运行中回传使用同一接收入口。同一用户、数据目录只运行一个窗口，第二个进程通过仅当前用户可连接的命名管道转发参数，保留原窗口中的开发密钥和等待动作。启动期间的链接先在内存排队，界面初始化完成后再处理。未发起操作的链接只提示从库中发起下载，不自行替换任何模组；冷启动自动恢复已保存的密钥，但仍需从库中发起操作；未配置或解密失败时再填写密钥。错误游戏/模组/文件、过期链接保留等待状态并提示重新从所选网页授权；取消、关闭或切换文件后回传不会继续旧动作，重复回传不会创建第二次下载。
400 表示授权与账户或文件不匹配，410 表示授权过期，均提示从网页重新取得链接。签名地址下载不附带 API 凭据。原压缩包可能是 ZIP、7z、RAR 等，保存时保留平台提供的文件名；更新解压仍按内容识别格式。

网站文件编号不与 v3 全局文件版本 ID 混用：免费模式使用 `nexus-v1-file:{编号}` 作为操作期间的平台文件标识，成功替换后保存 `InstalledWebsiteFileId`，清空原 `InstalledFileVersionId`；v3 更新则反向保存。临时 `key`、`expires`、`user_id` 和完整 nxm 链接均不写入数据库或下载历史，操作结束或关闭窗口释放，服务端回显的密钥会隐藏。

当前通过浏览器回传 nxm 链接接入，尚未实现 SSO。SSO 后续负责账户登录；免费账户的网页授权要求仍由 Nexus Mods 决定，SSO 登录本身不授予 Premium 下载权限。关闭“网页授权下载”后仍可使用已有的 v3 直接 API 下载流程。

## 验证与来源

契约回归使用模拟 HTTP 响应，检查实际 URL、方法、认证头、响应包装、ID 转换、主文件选择、错误及取消。运行：

```powershell
dotnet build HD2Mimnt.slnx --no-restore
dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --filter "FullyQualifiedName~Platforms|FullyQualifiedName~ModOnlineServiceTests|FullyQualifiedName~ModLinkServiceTests|FullyQualifiedName~UpdateCheckerServiceTests|FullyQualifiedName~CachingPlatformServiceTests|FullyQualifiedName~DownloadManagerServiceTests"
```

外部参考：[Nexus Mods API 文档](https://api-docs.nexusmods.com/)、[API Key 管理](https://www.nexusmods.com/settings/api-keys)、[RFC 9457 Problem Details](https://www.rfc-editor.org/rfc/rfc9457)、[Retry-After](https://www.rfc-editor.org/rfc/rfc9110.html#name-retry-after)。接口字段以本仓库提供的 OpenAPI 快照为准。

更新回归使用真实 ZIP、隔离 SQLite 和模拟 HTTP，覆盖原 ID/配置状态保留、指定文件版本、无有效补丁、取消、写库失败、旧部署清理成功/失败和错误状态。UI 测试通过实际 DI、右键菜单、ComboBox 绑定和鼠标点击“下载并更新”验证替换与列表刷新；Headless 用例按独立进程顺序运行。未使用真实账户密钥或实际游戏验证。

免费模式另覆盖网站文件编号、同账户授权、非法/过期/其它模组链接、400/410 错误、临时凭据隐藏及实际窗口更新替换，断言该流程未请求 v3 repacked 接口。下载授权规则亦对照 [Nexus Mods 官方客户端 getDownloadURLs](https://github.com/Nexus-Mods/node-nexus-api/blob/master/src/Nexus.ts)。

浏览器回传回归使用隔离的命名管道和注册表子键，覆盖启动排队、单实例转发、默认关联保留、实际下载按钮到协议回传的自动替换、错文件/过期/错账户、取消与重复回传、只下载压缩包。未用真实浏览器或真实账户完成端到端验证。Windows 注册依据：[默认应用注册](https://learn.microsoft.com/en-us/windows/win32/shell/default-programs)、[应用注册](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration)。

凭据回归使用真实 DPAPI 与隔离 SQLite，覆盖密文入库、更新覆盖、新数据库上下文恢复、清除后不再恢复、损坏密文拒绝解密、读写失败保持已有凭据，以及开发工具实际按钮和主窗口启动恢复。

浏览页搜索、会员判断、保存与导入、封面预览、分页、进度和错误提示均接入简中/繁中/英语及运行时切换。HTTP 错误保存异常与操作信息，展示时分别翻译步骤、建议与标签；服务器诊断、模组名、作者、关键词和文件名保持原文。回归覆盖匿名搜索请求体、服务器分页、GraphQL 错误、Premium/Supporter 权限、所选网站文件、无凭据 CDN 下载、真实 ZIP 导入及取消清理；UI 用例分进程顺序执行。真实账户会员下载尚未端到端验证。
