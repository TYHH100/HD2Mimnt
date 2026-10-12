# 文档导航

按用途读取下列文档。实现说明、设计计划和历史验收各有适用范围，版本及功能现状仍需核对当前代码；归档中的“已完成”仅表示当时阶段结论。

| 用途 | 入口 |
| --- | --- |
| 开发协作与易错点 | [仓库规则](../AGENTS.md)，按索引读取 `agent-guides/` |
| Nexus Mods 接入、认证与在线更新 | [NEXUS_MODS.md](features/NEXUS_MODS.md) |
| HTTP 分段下载、重试与断点续传 | [MULTITHREADED_DOWNLOADS.md](features/MULTITHREADED_DOWNLOADS.md) |
| Nexus Mods v3 API 契约快照 | [nexus_openapi.yaml](references/nexus_openapi.yaml) |
| 总体设计与路线 | [v2 总体开发计划](plans/HD2Mimnt%20v2%20%E6%80%BB%E4%BD%93%E5%BC%80%E5%8F%91%E8%AE%A1%E5%88%92.md)；计划不等于已实现功能 |
| 自动冲突检查与性能测量边界 | [性能评估](reports/automatic-conflict-check-performance.md) |
| 历史架构、UI、HD2IR 阶段报告 | [归档索引](Archive%20records/README.md) |

## 文档维护

- `agent-guides/`：稳定协作规则与反复出现的易错点，按领域维护，不堆进根 `AGENTS.md`。
- `features/`：当前模块的接口、使用与实现说明；行为变动时更新原文，不新增重复说明。
- `references/`：外部规范和契约快照，保留原始文件并注明来源；快照不代表远端最新版。
- `plans/`：设计目标与路线；`reports/`：注明日期、环境、数据规模及验证边界的报告。
- `Archive records/`：旧阶段计划、总结与验收。通过分类索引检索，不作为当前实现或测试通过的证据。
- 移动文档时同步修复相对链接及入口索引；无完全重复内容的历史记录保留，避免丢失独有结论。

外部参考：[GitHub 相对链接规则](https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/basic-writing-and-formatting-syntax#relative-links)、[Nexus Mods API](https://api-docs.nexusmods.com/)。
