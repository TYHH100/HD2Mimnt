# 平台、认证与下载

涉及 IModPlatform、Nexus Mods、凭据、NXM、在线更新或 HTTP 下载时先读本文件。实现说明见 [Nexus Mods](../features/NEXUS_MODS.md) 与 [分段下载](../features/MULTITHREADED_DOWNLOADS.md)。

## 易错点与正确做法

- 断点续传不能把预分配文件长度当成已下载进度，按分段偏移持久化；先刷新数据到磁盘，再原子替换断点 JSON。恢复必须校验下载身份、远端长度与强 ETag / Last-Modified，签名 URL 更新按稳定文件版本键关联，不能明文保存 URL 凭据。同一断点禁止并发写入，回退前等待全部分段退出；下载设置按每次启动读取快照，独立下载 Tab 保存不依赖游戏路径校验。开启续传时保留中断数据，测试使用隔离目录并结束后清理。来源：https://www.rfc-editor.org/rfc/rfc9110.html#section-14

- HTTP 分段下载使用独立 `Downloads/HttpDownloadService`：先确认已知长度、Range 支持及强 ETag / Last-Modified，再带 If-Range 请求并严格校验 206 范围和长度；HTTP 200 回退前必须等待所有分段写入者退出。重试进度不能重复计数，取消/失败不能替换已有目标，临时文件流必须释放；下载客户端默认头禁止携带平台 API 凭据。测试未知长度时不能使用可定位 MemoryStream 的 StreamContent（.NET 会推导 Content-Length），应使用 TryComputeLength 返回 false 的 HttpContent。来源：https://www.rfc-editor.org/rfc/rfc9110.html#section-14

- 外部平台按独立模块目录组织，例如 `Infrastructure/Platforms/NexusMods/`；公开适配器实现 `IModPlatform` 并通过 `PlatformRegistry` 调用，HTTP 客户端、平台模型、映射和文件流程按职责拆分，内部类型保持 `internal`。新增 A 网等平台使用自己的目录、认证状态和 API 模型，不向 N 网文件追加其它网站逻辑，也不把平台 DTO 泄漏到 Application/UI。来源：https://learn.microsoft.com/dotnet/csharp/fundamentals/types/interfaces

- Nexus Mods v3 以 `docs/references/nexus_openapi.yaml` 为契约：网站 `game_scoped_id`、模组全局 `id`、文件 `id` 和文件版本 `id` 不可混用。读取接口用 `data` 包装，但 `download-repacked` 的 POST 响应直接返回 `download_url`；下载优先活动主版本，API 凭据不能放入共享默认头并随签名下载地址发送。热门接口仅前 5 项，不能称为全站搜索；详情不含版本号，更新检查须查询文件版本，空版本不能制造更新提示。来源：https://api-docs.nexusmods.com/

- 模组平台字段新增或用于关联时，须同时核对 Core 实体、SQLite 补列、Repository 的 INSERT/UPDATE/读取与参数绑定；实体存在 `PlatformId` 等属性不代表已持久化。N 网页面编号用于更新/下载调用，全局 ID 仅用于其 API 内部路由；规范页面与 API 信息保存在 Mod 行内，删模组时一起删除，不保存签名下载地址或密钥。回归须覆盖数据库重开、旧库补列、删除及更新/下载读取。来源：https://api-docs.nexusmods.com/

- N 网个人 API Key 仅用于开发测试或个人使用；计划面向普通用户公开提供 API 功能时，有可演示的测试构建后联系官方注册应用，不能把个人 Key 输入入口当作正式应用认证。API 请求同时携带稳定的 `Application-Name` 和从当前 UI 程序集读取的 `Application-Version`，不能漏版本头、硬编码旧版本或冒充其它应用；用户 Key 不上传开发者服务器，使用须由用户操作发起。来源：https://help.nexusmods.com/article/114-api-acceptable-use-policy

- 在线更新保留原 Mod ID 和 ProfileMods，不能用“删除后重新导入”替换。先在独立目录准备、校验并计算 Hash，清理追踪的旧部署后提交原行的新路径，提交成功才删除旧库内文件；失败清理未提交副本。下载必须使用用户所选的文件版本全局 ID 并再次校验可下载状态，不能回退成当时的默认主文件；已安装文件版本与远端默认版本分开保存。取消/关闭等待执行体释放临时文件，排队进度验证当前操作和阶段，避免覆盖解析或最终结果。来源：https://api-docs.nexusmods.com/

- 免费账户 nxm 授权使用 v1 网站模组/文件编号和 GET download_link.json 的 key/expires，不能发给 v3 download-repacked 或当成全局文件版本 ID。先校验链接属于当前模组且未过期，再用 users/validate.json 比对 user_id；下载时重新验证所选文件。临时凭据仅用于当前操作，不入数据库、任务历史或错误文本；网站安装文件 ID 与全局版本 ID 分开保存。SSO 是账户登录方式，不等于 Premium 下载权限。来源：https://app.swaggerhub.com/apis-docs/NexusMods/nexus-mods_public_api_params_in_form_data/1.0?view=elementsDocs

- NXM 网页授权回传必须进入原运行实例，才能保留会话 API 密钥和用户发起的下载/更新动作；仅匹配当前等待的游戏、模组及网站文件编号，不接收回传后盲目替换。取消/关闭后回传不能复活旧任务，临时授权不入库或日志。Windows 按当前用户注册 ProgID/URLAssociations，不能覆盖其它管理器的已有默认关联；测试使用隔离管道与注册表子键并清理。来源：https://learn.microsoft.com/en-us/windows/win32/shell/default-programs

- NXM 在主实例启动和打开下载网页时先只读检查注册状态；ProgID、应用选择器候选、URLAssociations 与当前 EXE/DLL 路径一致时直接跳过，不重写注册表、不发送 SHChangeNotify。仅首次注册、条目缺失或路径变化时更新并通知系统刷新；二次实例转发不注册，注册失败必须呈现通知。不要仅凭持久化布尔标记判断已注册，也不能覆盖其它管理器的默认关联。来源：https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shchangenotify

- 平台 API 密钥通过 IPlatformCredentialStore 写入独立 PlatformCredentials 表，仅保存 DPAPI CurrentUser 密文，不使用会记录值的 SettingsService，也不能放入模组绑定或日志。保存成功后才切换内存密钥，删除持久化成功后才清除内存；启动与备份恢复通过 Application 服务加载，解密失败提示重填但不中断启动。临时 NXM 授权仍不持久化。来源：https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata
