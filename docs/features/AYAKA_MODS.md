# Ayaka Mods 接入

核对日期：2026-10-15。模块位于 `Infrastructure/Platforms/AyakaMods/`，通过 `PlatformRegistry` 和既有浏览、链接、下载及更新服务调用。

## 开发使用

1. 在“工具 → 开发工具”的 Ayaka Mods 卡片填写管理员发放的只读工具 API key，点击“保存并应用密钥”。与 N 网一致，保存到本机 DPAPI CurrentUser 加密凭据表，启动和备份恢复后加载；不写入普通设置或源码。
2. 在“浏览模组”选择 Ayaka Mods，点击加载列表，可浏览热门、最新发布和最近更新，并按服务器分页。页面限定 [Helldivers 2（119）](https://ayakamods.com/games/helldivers-2.119/)。公开文档未提供关键词搜索参数，因此 A 网不启用关键词搜索。
3. 可以输入模组网页链接打开详情，也可在本地模组右键“模组链接…”保存关联，使用现有检查更新、下载与替换流程。替换保留原 Mod ID、名称、标签和配置关联。
4. A 网下载入口默认使用系统浏览器，直接打开模组网页的 `/download` 路径以触发网站下载跳转；“下载并导入”或“下载并更新”先开始等待，再打开下载入口，匹配到完成的文件后自动继续。保留手动选取文件入口，不伪造 NXM 或自定义协议回调。`/download` 也可粘贴到模组链接输入框，保存时仍归一为模组主页。网站默认下载的文件仍须符合所选附件匹配条件；不假设入口支持未核实的版本/附件查询参数。参考：[网站下载入口示例](https://ayakamods.com/mods/weakpoint-lock-on-v2.3981/download)。2026-10-16 程序侧无凭据 HEAD 请求返回 HTTP 403，未据此声称完成真实浏览器下载验收。
5. 等待期间仅监视系统“下载”文件夹的顶层目录，自动匹配本次操作后新出现、名称及大小与 API 附件一致的压缩包（兼容浏览器 ` (1)` 等重名后缀）；排除临时文件、符号链接和仍被写入的文件，连续稳定后再导入。开始等待前已有的文件、多个候选、未知名称/大小不自动采用。文件名和大小只是匹配条件，并不能证明文件来源。
6. 浏览器保存到自定义目录或已有同名文件时，点击“选择已下载的文件…”手动选取；检查压缩包扩展名、非空及已知附件大小，沿用现有压缩包校验。更新前重新确认远端所选版本/附件及本地绑定未改变，成功后保留原 Mod ID、名称、标签和配置关联。
7. 同一时间只等待一项浏览器文件。取消、离开页面、切换平台/附件、关闭或等待十五分钟超时后停止接管。导入期间保持源文件只读，成功、失败、取消均不删除浏览器下载的原文件。API key 已保存不代表已通过服务器验证，访问失败按实际状态提示。

### API 直接下载测试开关

“工具 → 开发工具 → Ayaka Mods”提供“通过 API 直接下载 A 网文件（测试）”。默认关闭，仅本次运行有效；关闭开发工具窗口不重置，重启应用恢复关闭。仅影响“浏览模组”中的 A 网下载，模组库在线更新仍使用浏览器。

开启后浏览页文案同步显示 API 模式：“下载压缩包…”选择保存位置后调用原 API 下载，“下载并导入”通过 API 下载并复用现有导入浮窗。失败显示原接口错误，不自动转浏览器；Debug 日志继续按原有规则记录。下载/导入执行期间禁用开关，每项操作固定使用开始时的模式；取消勾选恢复默认浏览器方案。N 网行为不变。

## OAuth2 准备

工具 key 用于 API 列表、详情和版本；UI 默认通过浏览器下载，浏览页可通过开发工具开关测试 API 下载。OAuth 用于用户身份，二者互不替代。目前提供 OAuth 登录、按需刷新和退出的基础设施；评论、收藏、用户库等功能尚未接入。

向管理员注册 **Public Client**，提供应用名称和以下精确回调 URI，取得 `client_id`：

```text
http://127.0.0.1/oauth/ayaka/callback
```

在开发工具填写并保存 Client ID，然后到“设置 → 账号 → Ayaka Mods”登录。也可通过 `HD2MIMNT_AYAKA_CLIENT_ID` 提供默认公开配置；已保存的 Client ID 优先，清空保存值恢复默认。已登录时先退出再更换 Client ID。

登录使用系统浏览器、Authorization Code + PKCE S256；监听随机本机端口并验证 state、唯一 code 及回调路径。取消、离开账号页、关闭应用或五分钟超时会关闭监听并等待清理。公开客户端无需 Client Secret；程序也不提供 Secret 输入。`127.0.0.1` 动态端口与注册 URI 的路径保持一致，不使用自定义协议或 `localhost`。

Access/Refresh Token 保存在独立的加密凭据项 `ayakamods-oauth`，与工具 key `ayakamods`、N 网凭据分别管理。启动仅本地恢复；未来用户身份请求通过 `AyakaModsOAuthService.GetAccessTokenAsync` 获取有效令牌，临近过期时串行刷新。刷新后的轮换令牌先成功保存再应用；保存失败后重试保存，不能继续使用旧 refresh token。429/服务器故障保留登录，明确失效才要求重新登录。退出删除本机令牌，Public Client 不调用需 Secret 的撤销接口。

## 请求与下载约束

- 列表请求 `mod-games/119/mods/`，不向 `mods/` 传不存在的 `game_id` 过滤条件。详情和文件额外校验游戏与模组身份。
- 工具凭据仅通过每请求 `XF-Api-Key` 发送到固定官方 API，不使用 `XF-Api-User`，不放到下载客户端默认头。
- 列表和详情缓存映射后的公开信息两分钟，最多 128 项；更换/清除 key 失效。不缓存版本原始 DTO、存储地址或签名 URL。
- API 下载测试路径重新读取文件列表，确认所选版本/附件仍可见，再访问 `mod-versions/{version_id}/download?file={attachment_id}`。重定向不向存储主机转发 key、Authorization 或 Cookie；续传仍从 API 入口开始。
- 保留的 A 网 API 下载使用单分段；API 元数据请求串行并遵守 Retry-After。当前按用户操作单项检查，无后台轮询；后续批量更新应按官方 `mod_ids` 每批最多 100 项接入。
- 界面标明 Ayaka Mods 来源。HTTP 200 的错误 JSON/HTML 不作为模组压缩包导入；异常和任务历史不保留可能回显凭据的原始正文。

## Debug 排障

在“设置 → 日志”将**日志记录级别**设为 `Debug` 并保存，重新执行出错操作；日志页仅改变显示筛选不会启用记录。查找分类 `HD2Mimnt.Infrastructure.Platforms.AyakaMods.AyakaModsPlatformAdapter` 或正文 `Ayaka API response`，可看到操作、HTTP 方法、完整请求地址（含普通查询参数）、状态码、响应头及脱敏后的完整 JSON/HTML/文本正文。下载日志保留 `GET https://ayakamods.com/api/mod-versions/8348/download?file=39440` 这样的版本和附件信息，列表保留排序、分页参数。

列表、详情、版本和下载入口都使用同一分类。下载重定向记录响应头，`Location`、正文下载 URL 和外部文件请求均保留主机、路径及普通参数；只隐藏工具密钥、Bearer、Cookie、密码、OAuth 授权数据及 URL 中的凭据/签名值，不因字段名为 `download_url` 就隐藏整个地址，也不隐藏普通用户名、用户编号等元数据。签名链接例如 `https://cdn.example.test/armor.zip?signature=[REDACTED]&file=39440`。文件流和明确附件不读取正文、不打印压缩包。JSON/HTML 下载错误页（含 HTTP 200 错误）记录脱敏正文后仍交给原下载校验处理。所有日志参数先脱敏，OAuth 令牌兑换响应不加入此正文日志。

诊断保留根级 HTTP `code` 和 A 网 `errors[].code`，便于辨认 `parameter_invalid`、`no_permission` 等服务端原因；其他授权 code/state/verifier 继续隐藏。未开启 Debug 时不增加正文缓冲；开启后缓冲的 API 正文仍可被原解析流程读取。元数据缓存命中时没有新 HTTP 响应日志。

参考：[.NET 日志分类与筛选](https://learn.microsoft.com/dotnet/core/extensions/logging)、[ReadAsStringAsync 缓冲语义](https://learn.microsoft.com/dotnet/api/system.net.http.httpcontent.readasstringasync)。

## 验证边界

自动化测试使用模拟响应、真实本机 loopback 和隔离 SQLite/DPAPI，覆盖平台请求、下载重定向、加密保存、OAuth PKCE/刷新/取消以及界面交互。浏览器接管另覆盖候选文件、完整性、取消和保留源文件；不操作用户真实下载目录。本轮没有真实浏览器线上下载验收，也未完成注册 Client ID 的线上 OAuth 联调。

此前用户提供的真实日志显示详情/版本成功，而下载返回 HTTP 400 / `unexpected_error_occurred` 且 `can_download:false`。官方指南仍明确说明只读 Guest 工具 key 可解析下载，因此不能仅凭“只读”认定原因就是无下载权限；默认浏览器方案用于绕开已观察到的 API 下载失败，开发工具仍允许主动测试 API。

2026-10-15 使用开发环境 Key 完成真实只读列表、详情和版本 GET：版本对象确认包含 `mod_version_id`、`version_string`、`release_date` 和 `files`，附件包含 `id`、`filename`、`size`；详情的版本字段是 `version`，作者主页在 `User.view_url`。详情和版本均观察到 `can_download:false`，本轮没有尝试下载。响应摘录、独立 JSON 方案及验证边界见[来源元数据](MOD_SOURCE_METADATA.md)。不能把只读 GET 或模拟测试通过当作真实下载或真实授权成功。

浏览器文件接管的流程参考：[DemocracyDefenderModManager](https://github.com/katsyk/DemocracyDefenderModManager)；系统下载目录使用 [SHGetKnownFolderPath](https://learn.microsoft.com/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)，源文件读锁使用 [FileShare](https://learn.microsoft.com/dotnet/api/system.io.fileshare)。

## 官方参考

- [管理器接入指南](https://docs.ayakamods.com/mod-manager-guide/)
- [端点与文件契约](https://docs.ayakamods.com/endpoints/)
- [认证与 OAuth2](https://docs.ayakamods.com/authentication/)
- [使用规范（当前为开发阶段草案）](https://docs.ayakamods.com/acceptable-use/)
