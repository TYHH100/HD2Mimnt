# HD2IR 前端接入

## 使用流程

通过模组库的“添加模组”、窗口拖放或已有下载导入入口添加角色包。支持 `.hd2irpack.zip`、包含 `HD2IndependentRig` 的文件夹，以及最多 5 层的嵌套压缩包；提取复用当前多格式提取器。

一个输入包在模组库中保存为一个模组，展示 HD2IR 标识；最外层名称固定为导入的压缩包名或文件夹名，内层作者清单名称只作为组件标题，不覆盖库条目名。详情列出组件和文件数量。后端合并判定需要确认时，导入浮窗列出组合，选择“合并添加”继续，或取消导入。后台/下载入口未提供确认回调时，待确认组合明确报错，不静默合并。

外层封面按包层级从外到内解析：先取最外层包自己的清单 `IconPath`，再取该包根级预览图（preview / cover / thumbnail / icon 优先），内层组件图片只作为组件卡片预览，不会顶替外层封面。模组库缩略图与配置页图片都基于库内副本路径解析。

配置页打开模组选项时，HD2IAS 角色包按组件分两级：一级列出组件（含开关与预览图），组件内还有选项或子选项时进入二级页继续配置，保存并返回一级；普通模组保持单级列表。散装文件夹/包裹压缩包的多模组合集也生成同样的组件分组并复用这两级页，见 [散装文件夹合集导入](MOD_COLLECTION_IMPORT.md)。一级组件勾选表示组件仍参与部署，由组内选项派生：关闭二级页的部分选项只排除对应文件，组件保持勾选；组内选项全部关闭时组件才显示未勾选；点击一级开关会整组启用或关闭，重新勾选恢复组内全部选项。原配套资源清单中的 V1 开关、互斥子选项和 Legacy 选项转换为统一选项，选择状态仍保存到对应配置的 `ProfileMods.OptionsJson`，不同配置互不影响。

## 文件与部署

- Application 的 `Hd2IrIntegrationService` 复用 `Hd2IrDetector`、`ModMerger` 与 `LogicalMod`/组件模型，生成可部署文件映射。
- 文件落入当前模组库中的独立副本，组件元数据保存到 `.hd2mimnt-hd2ir.json`。原包删除后仍可部署；临时解压/准备目录在成功、失败和取消后清理。
- 统一 Mod ID 和 ProfileMods 保持现有库、搜索、详情、自动加入配置、排序和删除链路。组件读取通过 ModService，UI 不读文件或 SQLite；不再为 UI 创建一套平行配置数据。
- 原 `manifest.json` 和 `hd2mimnt.json` 在组件目录保留原字节，不写入生成的选项；生成的组件分组与选项树保存到管理器自有 `.hd2mimnt-options.json`，并投影到数据库清单字段。清单缺失或旧导入缺少分组时，Application 从该自有 JSON 回填，不能把作者清单当作生成选项来源。
- 明确的 `HD2IndependentRig` 相对目录及根目录特征文件映射到游戏 `bin/HD2IndependentRig`；配套合法补丁复用原补丁编号、选项过滤和 `data` 部署逻辑（2026-10-25 起不再生成 0 字节占位，游戏加载占位会崩溃）。`.hd2author` 是作者元数据，随组件保留在模组库，但部署展开 `GetNativeFilesAsync` 时跳过（含旧导入的组件元数据），不写入游戏目录，依据为用户明确要求。
- 骨骼目标和补丁共同预登记到 `DeploymentFiles`，沿用 Copy / HardLink / SymbolicLink、配置切换前清理和删除追踪。路径保存为规范化绝对路径。
- 重复骨骼目标在清理旧部署前报错，需调整组件选择。已有且不属于当前部署的骨骼目标要求先备份处理，避免覆盖外部安装。
- 含 `update_notes.json` 的 HD2IR/HD2IAS 更新包使用其专用更新流程，不能作为角色模组添加。此接入不执行包内管理器、Lua 或原生载荷。

## 验证边界

`Hd2IrIntegrationTests` 使用真实 ZIP、SQLite 和文件复制覆盖嵌套导入、原包移除、组件选择、配置切换、清理、外层命名、外层封面优先级、组件分组/自有 JSON、原清单/图片与互斥变体、取消清理及已有外部目标保护。

`Hd2IrIntegrationUiTests` 与 `ModOptionExpanderUiTests` 使用 Avalonia Headless 和真实 Application 服务，验证导入确认按钮点击、取消、详情组件、一级组件列表进入二级选项页、保存并返回、预览图点击及持久化回显。文件级测试和 Headless 通过不代表真实角色包的游戏内兼容性已验证。

## 参考与许可证

- [Avalonia UI 线程](https://docs.avaloniaui.net/docs/guides/development-guides/accessing-the-ui-thread)
- [.NET 任务取消](https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation)
- [SharpCompress 格式说明](https://github.com/adamhathcock/sharpcompress/blob/master/docs/FORMATS.md)、[许可证文件](https://github.com/adamhathcock/sharpcompress/blob/master/LICENSE.txt)。沿用已有依赖，本次未新增第三方库或载荷。
- 本项目许可证：[LICENSE](../../LICENSE)。
