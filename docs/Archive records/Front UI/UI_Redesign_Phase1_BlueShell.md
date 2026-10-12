# HD2Mimnt UI 重构 - Phase 1 总结

**日期：** 2026-10-05
**阶段：** Phase UI-1 - 蓝色视觉系统 + 七项导航 Shell + 总览/加载顺序/冲突
**状态：** 已完成
**前置：** Phase UI-0（Shell 定型，部分规范被本阶段推翻，见文末）

---

## 完成的工作

### 1. 品牌视觉系统（配色 + 开屏 + 图标）

- **配色**：整体采用 HD2Mimnt logo 的深蓝导航蓝体系。Dark 主题 token 全量替换：页面底色 `#0D131D`（与 logo 背景同色）、卡片 `#152033`、主蓝 `#3B82F6`、hover `#2563EB`、选中 `#16305E`；Light 主题强调色同步换蓝。清除全部散落硬编码颜色（ModDetailPanel、PluginPermissionDialog、ProfileView、TaskCenterView、Converters.cs），统一走主题 token。
- **开屏**：新增 `SplashWindow`（无边框、居中 logo）。启动流程改造：开屏先显示 → 数据库初始化改为异步（原来 `InitializeDatabaseAsync().Wait()` 阻塞 UI 线程）→ **主窗口 `Opened` 后在 `DispatcherPriority.SystemIdle` 关闭开屏**（对齐旧 WPF 仓库 `SplashWindow`/`App.OnStartup` 的"首帧渲染后才关开屏、无黑闪"语义；Avalonia 无 `ContentRendered`，用 SystemIdle 调度近似）。开屏失败不阻塞启动；不占任务栏。
- **图标**：logo.png 注册为 Avalonia 资源；主窗口标题栏 "M" 占位块替换为 logo；生成 `Assets/app.ico` 作为 exe/任务栏图标（当前仅 32px 单尺寸）。

### 2. Shell：顶部导航扩为 7 项

顺序（同步了 `SelectedTabIndex`、`NavigateTo*` 命令、隐藏 TabControl 页序、`Ctrl+1..7` 快捷键、通知跳转 switch 五处；Phase 1c 移除"加载顺序"后为 6 项，`Ctrl+4..6` 依次为冲突/工具/设置）：

| 索引 | 页面 | 快捷键 | 说明 |
| --- | --- | --- | --- |
| 0 | 总览 | Ctrl+1 | 新增 |
| 1 | 模组库 | Ctrl+2 | 既有 |
| 2 | 配置文件 | Ctrl+3 | 既有 |
| 3 | 冲突 | Ctrl+4 | 新增（Phase 1c 前为 4） |
| 4 | 工具 | Ctrl+5 | 原"更多"改名（Phase 1c 前为 5） |
| 5 | 设置 | Ctrl+6 | 既有（Phase 1c 前为 6） |

- 启动落点从模组库改为总览；状态栏文字改为中文。
- 按用户确认：**不做侧边栏快速导航**，导航保持顶部；侧栏的"当前配置文件"选择卡并入总览页顶部。

### 3. 总览页（OverviewView / OverviewViewModel）

- 当前 Profile 下拉选择（切换后同步刷新配置文件页与模组库状态）。
- 欢迎横幅（渐变底 + logo，暂为占位素材，待替换正式横幅图）。
- 统计卡：总模组数（`ModService.GetStatisticsAsync`）、已启用/已加入（当前 Profile）、文件冲突组数（冲突页扫描完成后回填）。
- 当前 Profile 模组快捷列表（前 8 个，勾选即启用/停用，`CheckBox` 采用 `Mode=OneWay` + 命令模式）。
- 快捷操作：立即部署（复用 LibraryViewModel 的部署流程语义：确认 → `DeployProfileAsync` → 通知；游戏目录未配置时引导到设置）、检查冲突（跳冲突页并触发扫描）、调整部署顺序（Phase 1c 起跳转配置文件页）、打开工具、打开设置。
- 最近动态：`NotificationService.GetRecentNotifications` 的活动流。

### 4. 加载顺序页（LoadOrderView / LoadOrderViewModel）

- 展示当前 Profile 模组（按 `ProfileMod.Order`），位置徽标 + 顺序调整（上移/下移）。
- 保存走 `ProfileService.ReorderModsAsync`（一次批量提交完整顺序，符合"批量操作一次读取一次更新"约定）；失败自动重载回同步。
- 旧 WPF 仓库的拖拽排序（GongSolutions DragDrop + 防抖快照保存）留作后续增强。

### 5. 冲突检测（ConflictDetectionService + ConflictsView）

- **检测语义**：对当前 Profile 的启用模组枚举目录内文件，相对路径（不区分大小写）被 ≥2 个模组提供即为一组冲突；按部署顺序排列持有者，**顺序最大者标注"生效"**（对齐旧仓库 ModConflictService 的 winner 语义）。
- 与旧仓库差异：旧仓库基于游戏 Unit 资源 ID（依赖其补丁解析/版本检测基础设施），当前架构先做文件路径重叠检测，Unit 级检测留作后续。
- 结果只读、不落库（`DeploymentTransaction.Conflicts` 字段仍留作部署事务使用）；扫描支持取消；目录缺失/不可访问的模组计入跳过数。
- 冲突页完成扫描后把组数回填到总览统计卡。

### 6. 布局基建

- `App.axaml` 新增 `Border.card` 样式与 `CardCornerRadius`（8px）token、`page-subtitle` 文本样式。
- `UtilityIcon` 新增 Home / ListOrdered / Warning / Wrench 图标。

---

## 被本阶段推翻的 Phase UI-0 规范

- ~~"无 Dashboard Card"~~ → 总览页包含统计卡。
- ~~"无 Glow / Gradient"~~ → 欢迎横幅使用克制的渐变。
- ~~"无大圆角"~~ → 卡片级 8px 圆角（控件仍为 4px）。
- ~~"顶部导航极简纯文字"~~ → 图标 + 文字。
- 保留：顶部一级导航（不做侧边栏导航）、中文导航、紧凑间距系统、状态栏单行。

## 遗留与后续

1. 欢迎横幅为占位素材，待替换正式游戏场景图（用户提供）。
2. 部署方式（Copy/SymLink/HardLink）目前总览固定 Copy、模组库页头仍留有下拉；下一步应把部署控件整体收敛到配置文件域（Phase UI-2 计划）。
3. 加载顺序拖拽排序、移到顶部/底部。
4. Unit 级冲突检测（依赖补丁解析能力）。
5. `app.ico` 目前 32px 单尺寸，发布前建议生成多尺寸。
6. 测试套件中 30 个失败为先前遗留（插件 ViewModel 中文化断言未更新 + ImageCache 测试隔离问题），与本阶段无关。

---

## Phase 1b 增补（同日反馈修复）

- **总览页布局修复**：主区两栏 Grid 漏写 `Grid.Column` 导致右栏叠加左栏（表现为"下部空白/快捷卡像侧边栏"）；已修，并删除欢迎横幅、当前配置选择器移至配置文件页页头。
- **配置文件页滚动修复**：主内容网格原本挂在根 Grid 的 `Auto` 行，内部 ListBox 拿到无限高度永不滚动；改为 `*` 行。添加模组弹层列表加 240px 上限。
- **通知改为手动删除**：Success/Info/Warning 不再自动过期（Error 本就不过期），仅通过面板逐条 ✕ 或"清空通知"删除；面板关闭按钮由空实现接到 `CloseRequested` 事件并改为 ✕ 图标；任务中心浮层关闭按钮同步改 ✕。
- **开屏最短展示 1.5s**（数据库初始化过快时一闪而过看不到 logo），仍保证首帧渲染后关闭。
- **模组增删联动刷新**：`ModService.ModsChanged` 事件（单条导入/批量导入/删除各触发一次），主窗口订阅后刷新总览统计与配置文件页。
- **工具页重做**：采用与设置页一致的"左侧分类导航 + 右侧内容"结构（插件/备份与恢复/日志/外部工具/开发工具/关于）。
- **日志与诊断重做**：日志行正则解析为结构化条目（时间 / 级别着色 / 来源短名 / 消息），固定 420px 高度虚拟化列表，不再随日志增长撑长页面；`处理中：False`/`加载中：False` 布尔直出改为条件显示。
- **游戏自动检测升级**（移植旧仓库 `SettingsPageViewModel.DetectGame` 语义）：Steam 注册表三级查找（HKCU SteamPath → HKLM InstallPath → Wow6432Node）→ 解析 `steamapps\libraryfolders.vdf` 枚举所有 Steam 库 → 常见静态路径 → 全盘符 `Steam\`/`SteamLibrary\` 兜底；目录校验收紧为：目录名 `Helldivers 2` + `data`/`tools`/`bin` 子目录 + `bin\helldivers2.exe`。旧仓库的 Unit 级版本检测（bundles.nxa 解码）留作后续。
- **开屏卡死修复 + 检测策略调整**：开屏关闭原本排在 `SystemIdle` 优先级，被启动期连续的 Normal 级工作（库索引、检测扫描）饿死导致永不关闭——改为 Background 优先级（仍晚于首帧）；文件系统探测挪到 `Task.Run` 后台线程，不再阻塞 UI；**已配置游戏路径时只校验应用、不再扫描**（重新检测走设置页"自动识别"按钮，`scanEvenIfConfigured: true`）。

## Phase 1c 增补（同日反馈：取消独立加载顺序页）

- **导航从 7 项收敛为 6 项**：移除"加载顺序"页（`LoadOrderView` / `LoadOrderViewModel` 及 DI 注册、`NavigateToLoadOrder`、`Ctrl+4` 旧绑定全部删除）。用户反馈：单独的顺序页与配置文件页信息割裂，实际部署顺序就是配置文件中模组的顺序（对齐旧 WPF 仓库语义）。
- **配置文件页内建顺序调整**：模组列表标题改为"模组与部署顺序"，每行展示位次徽标（01/02…），上移/下移按钮（首尾自动禁用），移除改为 ✕ 图标按钮；顺序改动通过 `ProfileService.ReorderModsAsync` 一次批量提交，失败自动重载回同步。新增模组仍由 `AddModToProfileAsync` 追加到末尾。
- **总览页快捷入口**："管理加载顺序"改为"调整部署顺序"，跳转配置文件页；Overview → 主窗口的导航索引同步重排。

## 编译状态

✅ 编译成功（0 错误）
