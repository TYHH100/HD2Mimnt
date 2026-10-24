# HD2Mimnt Phase 1：全局 UI Visual System

日期：2026-10-04  
范围：`src/HD2Mimnt.UI` 的全局资源与控件样式

## 阶段结论

Phase 1 的全局视觉基础已建立，但尚未满足进入 Phase 2 的全部验收条件。自动构建通过；现有测试为 479/487 通过，8 个 Avalonia Headless 图像缓存测试因测试初始化线程归属异常失败；完整的人工 UI 回归和主题切换验收仍需在桌面环境中完成。因此本阶段到此停止，不进入页面重构。

## 1. 全局 Resource

`App.axaml` 增加并统一了以下资源类别：

- Surface：保留 `Surface0Brush`、`Surface1Brush`、`Surface2Brush`、`Surface3Brush`，分别对应页面背景、次级工具区域、明确内容区域和当前选中/临时操作区域。
- 状态与强调：`SelectedSurfaceBrush`、`AccentBrush`、`AccentStrongBrush`、`SuccessBrush`、`WarningBrush`、`ErrorBrush`、`DisabledBrush`，Light/Dark 主题保持相同语义。
- 圆角：`FlatCornerRadius=0`、`SubtleCornerRadius=2`、`ControlCornerRadius=4`、`OverlayCornerRadius=6`。
- 间距：`Space4`、`Space8`、`Space12`、`Space16`、`Space20`、`Space24`、`Space32`。
- 排版：`PageTitleFontSize=22`、`SectionTitleFontSize=16`、`BodyFontSize=14`、`SecondaryFontSize=12`、`CaptionFontSize=11`。

## 2. Global Style

- Button：统一了 `Primary`、`Secondary`、`Tertiary`、`Danger`、`Icon`、`Navigation` 六类层级；普通按钮默认高度约 32px，圆角 4px，移除了全局按钮的 999 圆角。
- Navigation：导航按钮使用平面背景、文字颜色和底部强调线表达选中态，不再使用胶囊式选中背景。
- Input：TextBox 与 ComboBox 使用 4px 圆角、统一边框和 Focus 强调色。
- List：ListBoxItem 增加固定最小高度、轻微 Hover 背景、Selected 背景和左侧 Accent 指示。
- Typography：增加 Page Title、Section Title、Body、Secondary、Caption 样式资源。
- Focus：Button、TextBox、ComboBox、RadioButton 保留 Accent 边框 Focus 状态。

## 3. CornerRadius 规则

- 普通 Button、Icon Button、Navigation、Filter、Input：4px。
- 页面区域、列表、工具栏、分隔区域：优先 0px 或由现有边框结构表达。
- Overlay/Dialog：常规上限 6px；现有少量明确的内容容器仍待后续页面阶段逐项调整。
- 真正的状态标签保留胶囊语义：插件状态、高风险、权限模式等仍使用 999；通知图标使用 16px 形成圆形指示器。
- Library 的“已启用/已部署”标签和 Plugin Management 的操作按钮已改为 4px，避免把普通操作或属性呈现成胶囊。

## 4. Surface 规则

- `Surface0`：页面主背景和列表主体。
- `Surface1`：工具栏、列表头和次级分隔区域。
- `Surface2`：设置分组、Detail、Overlay 等明确内容区域。
- `Surface3`：选中项、批量操作条和临时操作区域。

本阶段没有重写页面容器层级；页面中仍存在的多层 Border/Card 感受记录到后续页面重构阶段处理。

## 5. Button 规则

Primary 用于当前页面主操作；Secondary 用于辅助操作；Tertiary 用于轻量操作；Danger 用于删除等破坏性操作；Icon 用于紧凑图标操作并保留 ToolTip；Navigation 只表达页面导航和当前选中态。没有新增阴影、渐变、Glow 或装饰动画。

## 6. Typography 与 Spacing

全局排版层级已通过资源和 TextBlock class 建立。新规则优先使用 4/8/12/16/20/24/32 间距刻度；现有页面中不规则的历史间距未在本阶段大规模重排，以避免越界到页面设计工作。

## 7. List 状态

全局 ListBoxItem 已建立 Normal、Hover、Selected 的基础表现；Disabled 和 Error 继续由页面业务状态样式承接。Selected 使用背景、Accent 左侧边界和文字层级表达，未使用 Glow 或大面积卡片高亮。

## 8. Icon 策略

本阶段没有扩大 Emoji/Unicode 图标使用范围，也没有引入新的图标库。现有 Emoji 和 Unicode 图标列入后续统一 Icon 资源阶段，避免在全局基础阶段混入资源迁移和页面改造。

## 9. Animation 策略

没有新增装饰性动画、Transition、Bounce、Spring、Scale、Glow 或大幅 Slide。现有设置项“启用界面动画”及其行为属于后续功能/页面验收范围。

## 10. Light/Dark Theme

Light 和 Dark 主题共用相同的 Resource Key、控件层级、圆角和间距，只调整颜色值。两套主题均提供 Surface、Border、Text、Hover、Selected、Accent 和状态颜色资源。

## 11. 页面问题留到后续 Phase

- MainWindow、Library、Profile、Settings、More、Task Center、Notification、Dialog、Plugin Management、Backup、Log 中仍有页面级容器、固定宽高、历史 Emoji/Unicode 和局部硬编码颜色，属于 Phase 2~7 的页面重构范围。
- Plugin Management 的真实状态标签和 Plugin Permission Dialog 的风险/权限标签保留胶囊语义，后续只在对应页面阶段继续校准。
- Icon 统一资源、Empty State 视觉收敛和页面级列表行重排暂不在 Phase 1 完成。

## 12. 编译结果

执行：

```text
dotnet build HD2Mimnt.slnx --no-restore /m:1
```

结果：成功，0 个错误；存在仓库已有的 CA2022、CA1416、xUnit 分析器等警告。UI 项目和 XAML 均成功编译。

## 13. 测试结果

执行：

```text
dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --verbosity minimal --blame-hang --blame-hang-timeout 30s
```

结果：479 通过，8 失败，总计 487。8 个失败均来自 `ImageCacheServiceTests` 的 Avalonia Headless 平台静态初始化，异常为 `The calling thread cannot access this object because a different thread owns it`，堆栈位于 `AvaloniaHeadlessPlatform.Initialize`，不是本次 UI Resource/Style 文件改动造成的编译错误。该测试环境问题已按仓库既有注意事项记录，未在本阶段扩大到业务层修复。

## 14. UI 回归结果

自动构建不能替代人工视觉验收。当前尚未完成桌面环境下 MainWindow、Library、Profile、Settings、More、Task Center、Notification、Dialog、Plugin Management、Backup、Log 的逐页交互、Light/Dark 切换、Focus、Hover、Selected 和 Dialog 回归检查，因此 Phase 1 不能标记为最终完成。

## 15. Git Diff 检查

本阶段修改集中在：

- `src/HD2Mimnt.UI/App.axaml`
- `src/HD2Mimnt.UI/Controls/ModDetailPanel.axaml`
- `src/HD2Mimnt.UI/Controls/NotificationPanel.axaml`
- `src/HD2Mimnt.UI/Views/DevToolsWindow.axaml`
- `src/HD2Mimnt.UI/Views/LibraryView.axaml`
- `src/HD2Mimnt.UI/Views/PluginManagementView.axaml`
- `src/HD2Mimnt.UI/Views/PluginPermissionDialog.axaml`
- `AGENTS.md`（补充 Avalonia Headless 测试线程初始化注意事项）
- 本记录文件

没有修改 Database、Infrastructure、Application、Domain、Deployment、PatchTool、Mod Parser、Profile Service 或 Library Service。

## 外部参考

- [Avalonia Styles](https://docs.avaloniaui.net/docs/basics/user-interface/styling)
- [Avalonia Theme Variants](https://docs.avaloniaui.net/docs/guides/styles-and-resources/how-to-use-theme-variants)
- [Avalonia Pseudo-classes](https://docs.avaloniaui.net/docs/reference/styles/pseudo-classes)
- [Microsoft Fluent 2 Design](https://fluent2.microsoft.design/)
