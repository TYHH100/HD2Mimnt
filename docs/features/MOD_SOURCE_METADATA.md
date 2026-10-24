# 模组来源与独立元数据

核对日期：2026-10-15。

## 使用方式

- 从“浏览模组”的 A/N 网下载并导入时，自动关联当前所选模组和附件，保存平台、规范模组页、作者、所选文件身份及安装版本。A 网浏览器接管、A 网 API 测试下载、N 网网页授权和 Premium 下载使用同一来源上下文。
- 模组库卡片显示来源按钮、版本和作者；悬停来源可查看安装版本、远端版本、已安装文件及上次检查时间。右键提供“打开来源网页”“打开作者主页”和 A/N 网“检查和更新模组…”。详情面板也提供相同的网站与更新入口。配置页卡片与右键支持来源和作者主页跳转。
- 已有本地模组可以右键“模组链接…”获取并保存 A/N 网关联。手动绑定只获取远端信息，不把远端最新版冒充已安装版本；同一关联重新获取信息时保留已安装文件和版本。
- 爱发电等其他来源，在“模组链接…”填写作者主页并点击“保存作者主页”，无需 API。已有 A/N 网关联时仅替换主页，保留模组来源和更新身份。网站链接只接受 HTTP/HTTPS，不执行脚本或本地文件链接。
- 更新仍替换原 Mod，保留 ID 与配置关联。选择某个可选附件更新时，保存该附件的版本；检查到的默认版本与安装版本分别展示。单纯绑定或检查元数据通过独立事件刷新展示，不使资源冲突结果失效。

## 文件与兼容性

元数据按本地 Mod ID 保存在 **当前模组库根目录**的 `.hd2mimnt-metadata/{ModId:N}.json`，示意：

```json
{
  "SchemaVersion": 1,
  "ModId": "00000000-0000-0000-0000-000000000001",
  "Source": {
    "PlatformId": "ayakamods",
    "GameDomain": "helldivers2",
    "ModId": "5089",
    "Url": "https://ayakamods.com/mods/5089/",
    "Name": "Health Regeneration",
    "InstalledVersion": "2026-10-15",
    "InstalledFileVersionId": "ayaka-file:8442:39632",
    "InstalledWebsiteFileId": "39632",
    "Author": "Argxs",
    "AuthorUrl": "https://ayakamods.com/members/argxs.408385/"
  }
}
```

完整文件还包含可空的全局模组 ID、远端版本和获取时间。JSON 在模组内容目录之外，不修改原压缩包、`manifest.json`、补丁、Lua 或其它原档案，不参与内容哈希和部署。模组库迁移会随库目录搬迁元数据。原数据库 `PlatformLinkJson` 保留兼容镜像：旧库或 JSON 缺失、损坏时仍可读取已保存关联；写 JSON 后数据库失败则恢复旧 JSON。删除模组同时删除对应元数据。JSON 不保存 Key、OAuth/NXM 凭据或临时存储 URL。

创建页填写的作者网站保存在随包分发的 `hd2mimnt.json` 中，导入后无需关联 A/N 网即可显示“打开作者主页”；网址为空或无效时隐藏入口。该包内扩展与库根目录下的来源 JSON 分开；普通导入会把旧清单内嵌扩展移到库副本的独立文件，原压缩包及源目录保持原样，已经符合标准的包保持原字节和哈希。

N 网网页文件编号与 v3 全局文件版本 ID 分开保存：浏览页的 v1 下载只保存 `InstalledWebsiteFileId`；v3 更新保存全局 `InstalledFileVersionId`。A 网记录版本/附件组成的平台文件身份及附件 ID。

## 真实只读响应核对

使用开发环境已有的 DPAPI Key，只读打开凭据数据库；GET 串行、请求间隔 1.5 秒，未调用下载、上传、修改或账号接口，未保存完整原始响应与凭据。公开字段摘录用于构造回归 fixture，不能替代真实下载验收。

| 平台与接口 | 本次观察 |
| --- | --- |
| A 网 `/api/mod-games/119/mods/?page=1` | HTTP 200，根级 `mods`、`pagination` |
| A 网 `/api/mods/5089/` | HTTP 200，`mod.version="2026-10-15"`、`username="Argxs"`，`User.view_url` 提供作者主页；不是 `mod.version_string` |
| A 网 `/api/mods/5089/versions/` | HTTP 200，根级 `versions`；版本 `mod_version_id=8442`、`version_string="2026-10-15"`；附件 `id=39632`、`filename`、`size`；`can_download=false` 仅记录，未尝试下载 |
| N 网 v3 `/games/helldivers2/mods/16633` | HTTP 200，`data.id="26280904900857"`、`game_scoped_id="16633"`、`name`、`game_id`；没有作者或版本字段 |
| N 网 v3 `/mods/26280904900857/files` | HTTP 200，`data.mod_files`，示例文件 `id="8038767"` |
| N 网 v3 `/mod-files/8038767/versions` | HTTP 200，`data.versions`；示例全局版本 `id="26280904952602"`、网页文件 `game_scoped_id="68378"`、`version="0.1.6"`；该文件版本不是模组首页版本 |
| N 网 v1 `/games/helldivers2/mods/16633.json` | HTTP 200，`author="RatInPlat"`、`uploaded_by="RatInPlat"`、`version="0.2.3"`、`uploaded_users_profile_url`；作者与上传者相同时使用该主页，避免把其他上传者当作者 |

首次 A 网探测收到 HTTP 403，调整为文档化分页请求及应用 User-Agent 后上述请求成功；两项条件同时变化，不能据此断定 403 原因或 Key 权限。后续没有激进重试。N 网认证 GET 响应实际带有 `X-RL-Hourly-*` / `X-RL-Daily-*` 配额头；额度仅代表该次响应，不能作为固定配置。

## 外部参考

- [Ayaka Mods 管理器指南](https://docs.ayakamods.com/mod-manager-guide/)
- [Ayaka Mods 端点](https://docs.ayakamods.com/endpoints/)
- [Ayaka Mods 使用规范](https://docs.ayakamods.com/acceptable-use/)
- [Nexus Mods API](https://api-docs.nexusmods.com/)
- [Nexus Mods API 使用规范](https://help.nexusmods.com/article/114-api-acceptable-use-policy)
- [System.Text.Json](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/overview)
