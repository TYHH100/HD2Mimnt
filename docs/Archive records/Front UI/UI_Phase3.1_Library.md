# HD2Mimnt — Phase 3.1 Library 页面信息架构与交互重构报告

日期：2026-10-04  
阶段：Phase 3.1  
范围：Library 页面 UI / UX 与状态表达

## 1. 修改范围

本阶段只调整 UI 层：

- `src/HD2Mimnt.UI/Views/LibraryView.axaml`
- `src/HD2Mimnt.UI/Controls/ModDetailPanel.axaml`
- `src/HD2Mimnt.UI/ViewModels/LibraryViewModel.cs`
- `src/HD2Mimnt.UI/ViewModels/ModItemViewModel.cs`

没有修改 Core、数据库、Repository、ModService、ProfileService 或 DeploymentService。`ModItemViewModel` 的修改仅用于投影当前 Profile 成员状态、按钮文案和行内状态，不改变实体语义或服务链。

## 2. 最终页面结构

页面按桌面工具密度组织为：

1. Page Header：模组库、简短说明、添加模组、部署当前 Profile、清理最近部署、更多。
2. Context / Filter Toolbar：当前 Profile 上下文、Library 总量、搜索、分类、排序。
3. Filter Row：全部、当前 Profile、当前 Profile 已启用、当前 Profile 未启用、部署状态已部署、部署状态未部署。
4. List Header + Mod List：紧凑表头与轻量列表行。
5. Selection Toolbar：只有存在选择时显示。
6. Detail Overlay：保留右侧固定宽度详情面板和部署进度覆盖层。

## 3. Library / Profile 状态表达

- Library 仍展示全部 Mod，不因 Active Profile 过滤掉全局资产。
- 页面显示“当前 Profile：默认配置”，并明确该文字只是当前操作上下文。
- 行内状态区分三种 Profile 状态：`未加入当前 Profile`、`已加入 / 未启用`、`已启用`。
- `已部署` 单独显示为“部署状态：已部署”，不与 Profile Enabled 合并。
- Enabled、Disabled 筛选均已改为当前 Profile 范围；Disabled 只匹配“已加入但未启用”的 ProfileMod。

## 4. Selection 行为

- 保留 ListBox 多选、全选当前筛选结果和现有反选命令。
- 反选入口位于更多菜单，范围为当前筛选结果。
- 选择工具栏显示 `已选择 N 个 Mod`，并提供启用、禁用、从当前 Profile 移除、从 Library 删除。
- SelectedMods 只用于选择和批量 UI 操作，不会成为 Deploy 输入。

## 5. Deploy / Clean 文案与范围表达

- Deploy 按钮显示“部署当前 Profile”，Tooltip 明确为当前 Profile 中全部已启用 Mod。
- 部署方式选择保留在 Header 中，默认行为和 `CurrentDeploymentMethod` 不变。
- Clean 按钮显示“清理最近部署”，Tooltip 明确清理最近一次部署会话记录的文件。
- Deploy 仍调用现有 Active Profile 部署链；Clean 仍调用 DeploymentService 的 Session / DeploymentFile 清理链。

## 6. Delete / Remove Profile 区别

- “从当前 Profile 移除”只调用现有 ProfileService 关系移除接口，Library Mod 和物理资产保留。
- “从 Library 删除 Mod”继续使用现有 Delete 命令，语义是删除 Library Mod 及其文件、缩略图、部署记录，并遵循 ProfileMods 级联。
- 行菜单、详情面板和选择工具栏均使用完整文案，避免把两种操作都简称为“删除”。

## 7. Empty / Loading / Error

- Library 空：`Library 为空`、`还没有导入 Mod`、添加模组。
- 搜索空：`搜索无结果`、清除搜索。
- 筛选空：`筛选无结果`、清除筛选。
- Loading、Error、Empty 和 Results 使用互斥可见性投影；加载时不会同时出现 Empty。
- Error 显示错误信息和重试按钮，不伪装为 Library 空。

## 8. Detail Panel

保留右侧固定宽度 400px 的详情面板、主列表和关闭行为。面板内状态徽标改为当前 Profile 语义：已启用、已加入 / 未启用、未加入当前 Profile；部署状态独立展示。面板操作区提供从当前 Profile 移除与从 Library 删除两种明确动作。

## 9. Responsive Layout

列表主区域使用剩余空间，窗口不固定整个 Library 宽度。1280x800、1280x832 和最大化窗口均完成截图检查，列表行、状态列、滚动区域和底部状态栏保持可见；默认窗口截图尺寸为 1282x832（含窗口边框）。

## 10. Visual System

- 保持现有深色桌面工具视觉系统和资源 Brush。
- 普通按钮、筛选项和状态标签没有新增 999 圆角、阴影、Glow 或渐变。
- 行使用轻量 Border + Grid；没有新增 Card 堆叠。
- 详情面板仍是固定侧栏，不扩展为浮动装饰卡片。
- 保留现有项目中的文本图标和 Mod 图标占位，未引入新的图标框架。

## 11. 性能保护措施

- 保留 `ListBox` + `VirtualizingStackPanel`。
- 没有包裹额外的外层 ScrollViewer。
- DataTemplate 保持单行 Grid，图标、文本、状态和操作均为轻量控件。
- 搜索、排序和筛选算法没有重写；仍使用现有 ViewModel 与 SearchService 链。

## 12. 测试结果

- `dotnet build HD2Mimnt.slnx --no-restore /m:1`：通过，0 错误；保留 9 个既有测试分析器 / 可空性警告。
- `git diff --check`：通过。
- `LibraryViewModelSelectionTests`：3/3 通过。
- `ProfileServiceTests`：11/11 通过。
- DeploymentService 相关测试：23/23 通过。

## 13. 人工验证结果

本地启动 Avalonia 应用并检查了 Library 首屏：

- 有 Mod：通过。当前本地数据展示 100 个 Library Mod。
- 当前 Profile 上下文：通过。显示默认配置、当前 Profile 计数和全 Library 计数。
- Profile 状态：通过。首屏可见“已启用”“已加入 / 未启用”“未加入当前 Profile”。
- 部署入口：通过。显示部署方式、部署当前 Profile、清理最近部署。
- 列表虚拟化结构：通过。列表行正常显示并可滚动。
- 1280x800：通过。列表行、右侧状态列和底部状态栏保持可见。
- 1280x832：通过首屏检查，实际截图尺寸为 1282x832（含窗口边框）。
- 最大化：通过。列表主区域占用剩余空间，顶部操作区和底部状态栏保持可见。
- 1600x900：本轮未完成独立截图复核。
- Search Empty、Selection Toolbar、Detail Panel、Remove/Delete 确认流程：代码路径已检查，未在本轮对持久化数据执行破坏性操作。

## 14. 未解决问题

- 本轮未对 1600x900 窗口单独截图复核。
- 未在人工验收中执行真正的删除或从 Profile 移除，以避免修改当前本地测试数据。
- 现有项目仍有构建输出中的 xUnit analyzer 与可空性警告，这些不是本阶段引入的错误。

## 15. Phase 3.2 必须继续处理的问题

- 补齐 1600x900 窗口的人工视觉验收记录。
- 在隔离的验收数据目录中执行可回滚的 Remove / Delete / Deploy / Clean 端到端场景。
- 如需进一步优化大数据量滚动性能，再独立评估 Recycling 与图标加载策略；本阶段没有私自改变虚拟化策略。

## 外部参考

- [Avalonia ListBox 文档](https://docs.avaloniaui.net/docs/reference/controls/listbox)
- [Avalonia 数据模板文档](https://docs.avaloniaui.net/docs/concepts/templates/data-templates)
- [Avalonia 绑定文档](https://docs.avaloniaui.net/docs/basics/data/data-binding)

本阶段报告到此为止，不自动进入 Phase 3.2。
