# 平台、认证与下载

涉及 IModPlatform、Nexus Mods、凭据、NXM、在线更新或 HTTP 下载时先读本文件。实现说明见 [Nexus Mods](../features/NEXUS_MODS.md) 与 [分段下载](../features/MULTITHREADED_DOWNLOADS.md)。

## 易错点与正确做法

- A/N 网的等待授权、文件下载、导入和在线更新通过 `PlatformDownloadTaskRunner` 登记为 `TaskService` 的 `PlatformDownload` 任务。切页、视图卸载和关闭更新面板只收起界面，不取消下载；页面上的取消、任务中心取消和确认退出才取消并等待清理。普通查询、账号登录和图片预览仍随页面生命周期停止。任务中心只保存名称、文件和进度消息，运行委托及 NXM/签名链接不入库；进度限频且在后台串行写库，不能把 UI 状态快照的读取移到后台线程。多个待授权任务按游戏/模组/文件准确分发，后台回调不强行跳页。新增构造依赖要覆盖 `App.ConfigureServices` 与 Library 的手动构造链。依据：[.NET 协作取消](https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation)、[Avalonia UI 线程](https://docs.avaloniaui.net/docs/guides/development-guides/accessing-the-ui-thread)。

- HD2IAS 更新优先直连官方 Raw，连接失败才顺序尝试用户指定的公益源；不能并发竞速或后台高频探测。版本列表缓存 15 分钟，已校验 ZIP 复用，下载单连接且不自动重试。同主机公益源请求至少间隔 2 秒，失败冷却 5 分钟；429 立即停止，不能轮换源规避限流。缓存包重新验证后才能复用；缓存损坏时先保留旧文件，下载校验成功后再替换。见 [HD2IAS 管理](../features/HD2IAS_MANAGEMENT.md#备用下载与请求频率)，来源为用户指定的 [gitproxy](https://api.gitproxy.dev/)、[gh-proxy](https://gh-proxy.com/)、[fastgit](https://fastgit.cc/) 和[官方 Raw](https://raw.githubusercontent.com/Estecsky/HD2-Integrated-Armor-System/refs/heads/main/UPDATE_HISTORY.txt)。

- 平台来源元数据存入模组库外层 `.hd2mimnt-metadata/{ModId:N}.json`，保留数据库兼容镜像，不写进模组内容目录或重写 manifest；版本/作者读取优先使用来源记录，安装文件版本与远端默认版本分开。下载任务开始时固定来源上下文，覆盖浏览器/API、首次导入/更新和手动 DI 链。元数据变化使用 `MetadataChanged`，不得复用会失效冲突缓存的 `ModsChanged`。2026-10-15 只读响应确认 A 网详情字段为 `version`，版本列表为 `version_string`，作者主页为 `User.view_url`；N 网 v3 详情无作者，需 v1 补充，上传者与作者不一致时不冒认主页。详见[来源元数据与响应核对](../features/MOD_SOURCE_METADATA.md)。来源：[A 网接口](https://docs.ayakamods.com/endpoints/)、[N 网 API](https://api-docs.nexusmods.com/)。

- A 网 UI 默认通过系统浏览器下载，无 NXM 回调；开发工具可对浏览页单独开启本次运行的 API 下载测试，任务开始时固定模式、执行中禁用切换、失败不自动回退。只在用户明确开始“下载并导入/更新”后由 Application `BrowserDownloadHandoffService` 等待系统下载目录中新出现的附件，匹配名称/已知大小并等待稳定、排除临时文件和写锁。旧文件、多个候选或未知元数据交给用户手选，自定义保存目录也走手选。显式取消或确认退出需等待清理，切页和收起面板继续等待；源文件属于用户，任何结果均不得删除。更新仍复用原 Mod 替换并重新校验绑定和附件；测试使用隔离下载目录，并从 App 的真实 DI 工厂验证浏览页和 Library 的手动构造链，避免可选参数漏传导致其中一处显示服务不可用。官方“只读 Guest key”仍描述下载能力，不能把只读等同于无下载权限。参考：[A 网指南](https://docs.ayakamods.com/mod-manager-guide/)、[浏览器接管参考项目](https://github.com/katsyk/DemocracyDefenderModManager)、[系统下载目录](https://learn.microsoft.com/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)。

- A/N 网响应日志共用 `Platforms/PlatformResponseLog` 的脱敏逻辑，分别使用各自适配器分类，不引用另一平台内部实现。仅密钥和机密信息脱敏，保留完整请求 URL、普通查询参数（尤其下载 `file`、列表排序/分页）、公开账号字段、响应下载 URL 及重定向路径。签名 URL 只隐藏凭据/签名值，不隐藏整条链接；编码过的凭据参数也要处理。根级 HTTP code 和 A 网 `errors[].code` 是诊断字段，不应误当授权码删掉；OAuth code/state/verifier、工具 key、Bearer、Cookie、密码仍须隐藏。A 网元数据及下载错误在 Debug 下记录正文；压缩包和重定向只记响应信息。新增日志须同时验证普通参数可见、错误页/解析/下载字节不受影响、所有日志参数无明文凭据。操作入口见 [A 网 Debug 排障](../features/AYAKA_MODS.md#debug-排障)。来源：[A 网下载端点](https://docs.ayakamods.com/endpoints/#versions--downloads)、[.NET 日志](https://learn.microsoft.com/dotnet/core/extensions/logging)、[HttpContent 缓冲](https://learn.microsoft.com/dotnet/api/system.net.http.httpcontent.readasstringasync)。

- A 网工具 key 与用户 OAuth 分开：列表/下载继续 `XF-Api-Key`，Public Client 用户身份采用 PKCE S256 和数字 loopback 随机端口，不套用 N 网 JWT 契约、不使用 `XF-Api-User`。OAuth 凭据独立 DPAPI 保存，退出仅清本机令牌；无注册 Client ID 不冒用示例。来源：[A 网认证](https://docs.ayakamods.com/authentication/)。

- A 网列表限定 `mod-games/119/mods/`，`mods/?game_id=` 无效；不假造关键词搜索。保留的 API 下载必须重新校验所选版本/附件并从 API 下载端点开始，不能保存/复用原始存储 URL；重定向不转发 key。列表/详情合理缓存，单分段下载；批量检查用 `mod_ids`（最多 100），不逐模组激进并发。文档未完整定义的版本字段需真实只读响应确认，模拟 fixture 不能视作线上验收。实现与边界见 [A 网接入](../features/AYAKA_MODS.md)。来源：[端点](https://docs.ayakamods.com/endpoints/)、[管理器指南](https://docs.ayakamods.com/mod-manager-guide/)、[使用规范](https://docs.ayakamods.com/acceptable-use/)。

- Nexus API 排障先查看实际响应：`NexusModsPlatformAdapter` 分类的 Debug 日志记录 v1/v2/v3 API 的操作、完整请求 URL（保留普通查询参数）、状态、响应头和完整响应体（包含非 JSON 错误页）。记录级别由“设置 → 日志”统一配置，所有构建默认警告；排查时手动保存为 Debug 后立即生效。所有日志参数必须先隐藏凭据字段、Cookie、签名参数及正文中的凭据回显；不记录请求认证头、NXM 临时凭据或压缩包内容。日志响应体缓冲后仍用于正常解析，不能消费后丢失。来源：[.NET 日志筛选](https://learn.microsoft.com/dotnet/core/extensions/logging#configure-logging)、[ReadAsStringAsync 缓冲行为](https://learn.microsoft.com/dotnet/api/system.net.http.httpcontent.readasstringasync)。

- Nexus HTTP 403 先结合 API 返回原因解释：`Mod not available` 只说明模组目前不可用，界面不推测未发布或审核状态，也不能直接归因为密钥、Premium 或网页授权问题；`Not published` 则提示尚未发布。保留服务端原始说明，建议查看网页状态、稍后由用户重试；无明确原因时保留通用访问提示。来源：[HTTP 403 语义](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.5.4)、[Nexus API](https://api-docs.nexusmods.com/)。

- Nexus 请求额度读取最近一次认证 API 响应的 `X-RL-Hourly-*` / `X-RL-Daily-*`，无字段显示未知，不硬编码为已获取额度、不后台轮询。退出/换账号清空内存额度，旧请求回传不能复活；手动刷新使用 v1 `users/validate.json`，429 同样先读额度再报告错误。OAuth 指南静态公钥和 Discovery JWKS 可能不同，使用固定官方地址按 `kid` 验签，不采信 JWT 的外来密钥 URL。来源：[v1 限流](https://app.swaggerhub.com/apis-docs/NexusMods/nexus-mods_public_api_params_in_form_data/1.0#rate-limiting)、[Discovery](https://users.nexusmods.com/.well-known/openid-configuration)。

- Nexus 正式认证使用 Public Application 的 Authorization Code + PKCE S256：Client ID/回调地址为公开配置，桌面应用不得保存 Client Secret。启动只恢复加密状态，用户操作前按需串行刷新；回调 state/verifier 只驻内存，Access/Refresh Token 仅存 DPAPI 凭据表。无注册 Client ID 不冒用示例应用；个人 Key 保留为开发测试入口，不能替代正式发行认证。429 保留登录并遵守 Retry-After，不能当成授权撤销。来源：[OAuth 指南](https://modding.wiki/en/api/oauth2-guide)、[API 使用规范](https://help.nexusmods.com/article/114-api-acceptable-use-policy)、[RFC 8252](https://www.rfc-editor.org/rfc/rfc8252)。

- “工具 → 直链下载”通过 `DirectDownloadService` 复用 `HttpDownloadService`，按每次任务创建独立代理客户端；解析、HTTP 重定向、分段和回退请求使用同一客户端与 Cookie。最多跟随 20 次 HTTP 跳转；登录、验证码和 JavaScript 页面不当作模组压缩包导入。最终响应的 `filename*` / `filename` 优先于最终 URL，文件名去除路径并处理 Windows 保留名；仅下载遇同名文件自动编号，发布时仍禁止覆盖，防止并发创建目标。导入必须等待现有导入浮窗完成并检查结果，再清理独立源目录；取消后等待下载释放流，支持续传的断点由缓存管理。代理地址可保存，代理账号、密码和完整下载链接不得写入设置或任务历史。来源：[.NET 自动重定向](https://learn.microsoft.com/dotnet/api/system.net.http.httpclienthandler.allowautoredirect)、[SOCKS 代理](https://devblogs.microsoft.com/dotnet/dotnet-6-networking-improvements/#socks-proxy-support)、[Content-Disposition](https://www.rfc-editor.org/rfc/rfc6266)。

- HTTP 200 不代表返回了文件：下载接口可能用 JSON 报告登录、权限或过期错误，例如 `ec=40100`。`DirectDownloadResponseGuard` 在解析、分段和回退时检查响应，最多探测 16 KiB，按常见错误字段识别而不绑定单一网站；探测过的真实文件字节必须完整重放，保留正常 JSON 文件和明确的附件。不要把错误 JSON 保存成无扩展名的 `redirect` 文件再交给解压器，也不把原始响应、授权地址或凭据带入异常历史。来源：[HTTP 状态语义](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.3.1)、[Problem Details](https://www.rfc-editor.org/rfc/rfc9457.html)。

- 需要网站账号验证时使用系统浏览器，不提供 Cookie/Referer 粘贴入口、不读取浏览器登录凭据。浏览器完成验证后可以使用已授权的文件直链；无法取得独立可用直链时，提供浏览器下载文件的导入入口。导入用户选择的本地文件不得删除源文件；这与管理器自己创建、完成后清理的临时下载目录不同。来源：[RFC 6265 会话 Cookie](https://www.rfc-editor.org/rfc/rfc6265)、[Avalonia 文件选择器](https://docs.avaloniaui.net/docs/concepts/services/storage-provider)。

- 浏览页下载并导入使用独立临时目录，复用现有导入浮窗并等待完成后清理压缩包；不能将导入浮窗吞掉的错误误报成成功，下载进度进入导入阶段后也不能覆盖导入状态。封面预览完整解码 `picture_url` 源图，关闭时只释放预览自己拥有的图片，不能释放其它页面共享的 Bitmap。v1/v3 公开 REST 契约没有列出模组画廊列表接口，其他图片提供网页 Images 入口。来源：https://api.nexusmods.com/openapi.yaml 、https://api.swaggerhub.com/apis/NexusMods/nexus-mods_public_api_params_in_form_data/1.0

- “浏览模组”通过 `IModBrowsePlatform` → `ModBrowseService` 调用独立 Nexus 模块。v3 热门仅 5 项且无需认证；v1 最新发布/最近更新各 10 项并需要已有密钥，名称或作者搜索用官方 v2 GraphQL `mods`，AND 限定本游戏、OR 匹配名称/作者；每页 20 项，按服务器总数翻页，`WILDCARD` 传原始关键词，不添加星号。旧 `IModPlatform.SearchAsync` 仍是有限热门兼容接口。首次下载不必先建本地 Mod，选文件后检查 v1 `is_premium`：Premium 使用同一网站文件编号直接获取链接，免费账户复用网页授权和 DownloadManager；Supporter 不等于 Premium，状态读取失败不能猜测权限；显式取消和确认退出必须等待清理，切页继续后台下载，回传仅匹配当前等待的文件。预览客户端不带 API 凭据且加载不阻塞列表操作。来源：https://app.swaggerhub.com/apis-docs/NexusMods/nexus-mods_public_api_params_in_form_data/1.0 、https://api-docs.nexusmods.com/ 、https://github.com/Nexus-Mods/node-nexus-api/blob/master/src/parameters.ts 、https://api.nexusmods.com/v2/graphql

- 断点续传不能把预分配文件长度当成已下载进度，按分段偏移持久化；先刷新数据到磁盘，再原子替换断点 JSON。恢复必须校验下载身份、远端长度与强 ETag / Last-Modified，签名 URL 更新按稳定文件版本键关联，不能明文保存 URL 凭据。同一断点禁止并发写入，回退前等待全部分段退出；下载设置按每次启动读取快照，独立下载 Tab 保存不依赖游戏路径校验。开启续传时保留中断数据，测试使用隔离目录并结束后清理。来源：https://www.rfc-editor.org/rfc/rfc9110.html#section-14

- HTTP 分段下载使用独立 `Downloads/HttpDownloadService`：先确认已知长度、Range 支持及强 ETag / Last-Modified，再带 If-Range 请求并严格校验 206 范围和长度；HTTP 200 回退前必须等待所有分段写入者退出。重试进度不能重复计数，取消/失败不能替换已有目标，临时文件流必须释放；下载客户端默认头禁止携带平台 API 凭据。测试未知长度时不能使用可定位 MemoryStream 的 StreamContent（.NET 会推导 Content-Length），应使用 TryComputeLength 返回 false 的 HttpContent。来源：https://www.rfc-editor.org/rfc/rfc9110.html#section-14

- 外部平台按独立模块目录组织，例如 `Infrastructure/Platforms/NexusMods/`；公开适配器实现 `IModPlatform` 并通过 `PlatformRegistry` 调用，HTTP 客户端、平台模型、映射和文件流程按职责拆分，内部类型保持 `internal`。新增 A 网等平台使用自己的目录、认证状态和 API 模型，不向 N 网文件追加其它网站逻辑，也不把平台 DTO 泄漏到 Application/UI。来源：https://learn.microsoft.com/dotnet/csharp/fundamentals/types/interfaces

- Nexus Mods v3 以 `docs/references/nexus_openapi.yaml` 为契约：网站 `game_scoped_id`、模组全局 `id`、文件 `id` 和文件版本 `id` 不可混用。读取接口用 `data` 包装，但 `download-repacked` 的 POST 响应直接返回 `download_url`；下载优先活动主版本，API 凭据不能放入共享默认头并随签名下载地址发送。热门接口仅前 5 项，不能称为全站搜索；详情不含版本号，更新检查须查询文件版本，空版本不能制造更新提示。来源：https://api-docs.nexusmods.com/

- 模组平台字段新增或用于关联时，须同时核对 Core 实体、SQLite 补列、Repository 的 INSERT/UPDATE/读取与参数绑定；实体存在 `PlatformId` 等属性不代表已持久化。N 网页面编号用于更新/下载调用，全局 ID 仅用于其 API 内部路由；规范页面与 API 信息保存到独立来源 JSON 和 Mod 行兼容镜像，删模组时一起删除，不保存签名下载地址或密钥。JSON 读取使用 `FileShare.Delete`，让原子替换及后台清理不被读流锁住；失败回滚同时清理临时文件和空目录。回归须覆盖数据库重开、旧库补列、删除及更新/下载读取。来源：[Nexus API](https://api-docs.nexusmods.com/)、[FileShare](https://learn.microsoft.com/dotnet/api/system.io.fileshare)。

- N 网个人 API Key 仅用于开发测试或个人使用；计划面向普通用户公开提供 API 功能时，有可演示的测试构建后联系官方注册应用，不能把个人 Key 输入入口当作正式应用认证。API 请求同时携带稳定的 `Application-Name` 和从当前 UI 程序集读取的 `Application-Version`，不能漏版本头、硬编码旧版本或冒充其它应用；用户 Key 不上传开发者服务器，使用须由用户操作发起。来源：https://help.nexusmods.com/article/114-api-acceptable-use-policy

- 在线更新保留原 Mod ID 和 ProfileMods，不能用“删除后重新导入”替换。先在独立目录准备、校验并计算 Hash，清理追踪的旧部署后提交原行的新路径，提交成功才删除旧库内文件；失败清理未提交副本。下载必须使用用户所选的文件版本全局 ID 并再次校验可下载状态，不能回退成当时的默认主文件；已安装文件版本与远端默认版本分开保存。取消/关闭等待执行体释放临时文件，排队进度验证当前操作和阶段，避免覆盖解析或最终结果。来源：https://api-docs.nexusmods.com/

- 免费账户 nxm 授权使用 v1 网站模组/文件编号和 GET download_link.json 的 key/expires，不能发给 v3 download-repacked 或当成全局文件版本 ID。先校验链接属于当前模组且未过期，再用 users/validate.json 比对 user_id；下载时重新验证所选文件。临时凭据仅用于当前操作，不入数据库、任务历史或错误文本；网站安装文件 ID 与全局版本 ID 分开保存。SSO 是账户登录方式，不等于 Premium 下载权限。来源：https://app.swaggerhub.com/apis-docs/NexusMods/nexus-mods_public_api_params_in_form_data/1.0?view=elementsDocs

- NXM 网页授权回传必须进入原运行实例。已有下载/更新等待时，仅匹配当前游戏、模组及网站文件编号；没有等待任务时，有效链接可独立下载并导入，无需先选库中模组、检查 Premium 或再次打开网页授权，不盲目替换已有模组。NXM 的文件下载许可与 API 账号认证分开：复用当前登录，缺少认证引导“设置 → 账号”，不能把“没有等待任务”误报成“必须先用开发工具填 Key”。显式取消后的同目标迟到回调不能复活旧任务，重新在应用发起下载才重置该等待状态；临时授权不入库或日志。Windows 按当前用户注册 ProgID/URLAssociations，不能覆盖其它管理器的已有默认关联；测试使用隔离管道与注册表子键并清理。来源：[Nexus v1 API](https://app.swaggerhub.com/apis-docs/NexusMods/nexus-mods_public_api_params_in_form_data/1.0?view=elementsDocs)、[Windows 默认应用](https://learn.microsoft.com/en-us/windows/win32/shell/default-programs)。

- NXM 在主实例启动和打开下载网页时先只读检查注册状态；ProgID、应用选择器候选、URLAssociations 与当前 EXE/DLL 路径一致时直接跳过，不重写注册表、不发送 SHChangeNotify。仅首次注册、条目缺失或路径变化时更新并通知系统刷新；二次实例转发不注册，注册失败必须呈现通知。不要仅凭持久化布尔标记判断已注册，也不能覆盖其它管理器的默认关联。来源：https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shchangenotify

- 平台 API 密钥通过 IPlatformCredentialStore 写入独立 PlatformCredentials 表，仅保存 DPAPI CurrentUser 密文，不使用会记录值的 SettingsService，也不能放入模组绑定或日志。保存成功后才切换内存密钥，删除持久化成功后才清除内存；启动与备份恢复通过 Application 服务加载，解密失败提示重填但不中断启动。临时 NXM 授权仍不持久化。来源：https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata
