# 本地版本与重复导入

“设置 → 模组库”提供两个独立选项，保存后生效：

- **保留模组历史版本**（默认关闭）：开启前说明完整文件的额外磁盘占用、不自动清理，以及切换后的选项检查和自动增量部署行为。取消确认保持关闭。
- **允许重复导入**（默认关闭）：默认拒绝库内当前版本或历史版本中已有的相同内容；开启后可导入独立副本。已有重复项不会自动合并或删除。

库页与配置页的模组详情共用“本地版本”：可使用压缩包更新当前模组、切换任意已保留版本、删除不再需要的历史版本。列表显示版本号、保存时间、大小和短标识，区分同版本号的不同内容。当前版本显示在详情顶部。

压缩包更新与 N 网/A 网在线更新保留原模组 ID、配置成员、启用状态、顺序和已保存选项；先准备独立内容，再提交内容更新，不删除原有部署记录。保留开关开启时保存旧文件及内容、安装文件标识、预览等快照；关闭时按原逻辑清理被替换的库内副本。切换或更新后若该模组当前已部署且在所属配置中启用，会按当前部署记录自动增量更新变化文件；未参与部署或已停用则保留记录，等待下次手动部署。界面始终提示检查模组选项。

未关联网站平台链接的本地模组使用压缩包更新时，版本号由管理器按小版本自动递增（`1.0.0 → 1.1.0 → 1.2.0`，无有效数字版本时从 `1.0.0` 开始）；N 网/A 网等平台更新仍使用网站文件版本。新导入或更新过的模组在模组库与配置文件页的卡片上都会显示“新增/更新”提示，点击任一页面的卡片后两处同步清除。提示方式在“设置 → 模组库 → 新增/更新模组提示”中选择：仅标志、仅描边（描边淡入淡出）或标志闪烁。

关闭保留开关不会删除已有历史；主动切换仍保存刚离开的版本，方便再次切回。删除历史只释放所选历史内容；删除模组会一并清理历史版本。仅下载压缩包不修改模组或版本记录。

## 存储与实现

- 版本索引：模组库的 `.hd2mimnt-versions/{ModId:N}.json`，使用相对模组库路径。完整文件保留在库内独立目录，不作为额外模组加入列表，不修改标准 `manifest.json`。
- 卡片提示状态保存在 `Mods.Notice`（`None`/`New`/`Updated`），导入成功为 `New`，任何更新提交为 `Updated`；点击卡片调用 `ModService.DismissModNoticeAsync` 只更新该列，不触发全库刷新。
- `ModService` 统一处理压缩包更新、在线替换、切换和删除。索引原子发布；数据库提交失败恢复索引并清理未提交副本；取消不提交半份内容。
- `ModImportService` 在提交前串行检查重复内容。原始目录 Hash 仍保持完整文件指纹；重复判断另外忽略清单 GUID 和 HD2IR 内部运行时映射，比较作者元数据与完整文件内容，避免规范化生成的 ID 让同一包绕过检查。压缩方式、压缩包文件名不作为身份依据。
- 判重先按暂存树的内容身份预检，重复包在复制入库之前就被拒绝；提交时再按库内权威身份复检，判重与落库仍在同一把导入锁内完成，所以并发导入不会各存一份。候选行按 `Mods.Hash` 与 `Mods.ContentFingerprint` 索引查询，并补上尚未持久化身份的旧行和有本地版本索引的行，不再扫描整张 Mods 表逐行读盘。
- 本地更新/切换可取消，详情关闭和应用关闭会请求取消，应用退出等待操作完成清理。

## 验证与参考

回归入口：`ModVersionHistoryTests`、`ModVersionPreferencesTests`、`ModNoticeAndVersionTests`、`Hd2IrIntegrationTests.ImportSameNestedPackageTwice_RejectsDuplicateDespiteGeneratedMetadataIds`，以及 `ModVersionHistoryUiTests.cs`、`FluentAvaloniaUiTests.LibraryNotice_ShowsConfiguredStyleAndClearsOnCardClick`、`FluentAvaloniaUiTests.ProfileNotice_ShowsConfiguredStyleAndClearsLibraryCardToo` 中的真实 DI、点击、取消确认、语言切换与两页版本切换测试。在线下载回归复用 `ModOnlineServiceTests`；自动化测试不代表本次实际访问网站下载验收。

- [.NET File.Move 原子发布入口](https://learn.microsoft.com/dotnet/api/system.io.file.move)
- [SHA256](https://learn.microsoft.com/dotnet/api/system.security.cryptography.sha256)
- [Avalonia UI 线程](https://docs.avaloniaui.net/docs/guides/development-guides/accessing-the-ui-thread)
- [MVVM RelayCommand](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/relaycommand)

本功能复用项目已有依赖，未引入新的开源项目或第三方代码。项目许可证见仓库 [LICENSE](../../LICENSE)。
