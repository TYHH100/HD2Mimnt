# HD2Mimnt 协作规则

本文件是仓库级常驻规则。详细背景和按领域的易错点放在 [`docs/agent-guides/`](docs/agent-guides/) 中，处理对应任务时再读取，避免每次加载全部历史规则。

## 必须遵守

- 使用中文回复；先确认现状，再小步修改和验证，不做无关的过度审计。
- 保留工作区已有的未提交修改；不要覆盖或回退与当前任务无关的文件。
- 临时复制、解压、测试目录在流程结束后删除；不要留下临时产物。
- UI 通过 Application 服务访问业务和数据，View/ViewModel 不直接访问 SQLite、部署实现或文件复制逻辑。
- 所有可取消的耗时操作保持 UI 响应，并在取消、失败和关闭时完成后台清理。
- 代码修改后先构建，再顺序运行相关测试；共享 `bin/obj` 时不要并行构建。确认目标测试确实执行；仅文档变更检查链接、格式和内容完整性即可。
- 提交或推送时使用 Conventional Commits；用户未要求时不要主动推送。
- 反复出现的问题提炼为“易错点与正确做法”，更新对应领域指南并保留外部参考链接；根文件只维护常驻规则和索引，不再追加逐次问题记录。

## 按需读取指南

修改对应模块前先读取匹配的指南；跨领域任务读取涉及的几份即可，不要一次加载全部指南。

| 场景 | 指南 |
| --- | --- |
| 项目结构、入口、命令、架构 | [`project-basics.md`](docs/agent-guides/project-basics.md) |
| 部署、配置文件、加载器、补丁清理 | [`deployment-and-loaders.md`](docs/agent-guides/deployment-and-loaders.md) |
| 外部平台、Nexus Mods、下载和凭据 | [`platforms-and-downloads.md`](docs/agent-guides/platforms-and-downloads.md) |
| Avalonia、FluentAvalonia、选择、拖放和通知 | [`ui-and-avalonia.md`](docs/agent-guides/ui-and-avalonia.md) |
| 多语言、系统语言检测、翻译资源和实时切换 | [`localization.md`](docs/agent-guides/localization.md) |
| 构建、测试、任务、备份、日志和性能 | [`testing-and-lifecycle.md`](docs/agent-guides/testing-and-lifecycle.md) |
| 导入、压缩包、TOC、Type ID、哈希 | [`import-and-resource-types.md`](docs/agent-guides/import-and-resource-types.md) |

## 快速工作基线

- 解决方案：`HD2Mimnt.slnx`；常用检查为 `dotnet build HD2Mimnt.slnx --no-restore` 和相关 `dotnet test`。
- 分支模型：`master` 只保存稳定版本（`dev` 压缩合并回 `master` 并打 `V` 版本标签），日常开发与提交都在 `dev`；流程见 [RELEASE_PROCESS](docs/RELEASE_PROCESS.md)。
- 变更前先查看 `git status` 和目标文件的现有差异，避免误处理用户的脏工作区。
- 分隔符的持久化、折叠、颜色与固定区域边界见 [UI 指南](docs/agent-guides/ui-and-avalonia.md)。
- 领域指南延续原有约束。用户最新明确要求优先；版本、路径和实现说明需核对当前代码，发现文档过期时同步修订，不以既有缺陷推翻行为约定。
