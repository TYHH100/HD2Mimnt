# HD2Mimnt Phase 0.5：UI 视觉结构审计

**审计日期：** 2026-10-04  
**审计范围：** `src/HD2Mimnt.UI` 中的 XAML、应用级资源和控件结构。  
**审计目的：** 记录当前 UI 的视觉实现事实，为后续 UI 工作提供可核对的结构基线。  
**本阶段边界：** 只读审计；未修改 XAML、ViewModel、Application、Infrastructure 或业务逻辑。  
**记录方式：** 数值、样式名、控件名和文件位置均按当前源码记录；未对视觉取舍做设计判断。

## 1. 全局 Style / Resource 审计

### 1.1 App 资源和主题

文件：`src/HD2Mimnt.UI/App.axaml`

| 资源/设置 | 当前值 | 使用范围 |
|---|---|---|
| `RequestedThemeVariant` | `Dark` | Application 默认主题变体，App.axaml:5 |
| `Surface0Brush` Light | `#F3F5F8` | 主题资源，页面背景层；App.axaml:11 |
| `Surface1Brush` Light | `#FFFFFF` | 主题资源，次级表面；App.axaml:12 |
| `Surface2Brush` Light | `#F8FAFC` | 主题资源，控件/卡片表面；App.axaml:13 |
| `Surface3Brush` Light | `#EEF2F6` | 主题资源，悬停/更高层表面；App.axaml:14 |
| `BorderBrush` Light | `#D7DEE8` | 通用边框；App.axaml:15 |
| `BorderStrongBrush` Light | `#B5C0CF` | 强调边框、输入控件边框；App.axaml:16 |
| `TextPrimaryBrush` Light | `#172033` | 主文本；App.axaml:17 |
| `TextSecondaryBrush` Light | `#526071` | 次级文本；App.axaml:18 |
| `TextMutedBrush` Light | `#697586` | 弱化文本；App.axaml:19 |
| `HoverSurfaceBrush` Light | `#E5EAF1` | 悬停表面；App.axaml:20 |
| `Surface0Brush` Dark | `#05070C` | 主题资源，页面背景层；App.axaml:23 |
| `Surface1Brush` Dark | `#0A0D12` | 主题资源，次级表面；App.axaml:24 |
| `Surface2Brush` Dark | `#0F131C` | 主题资源，控件/卡片表面；App.axaml:25 |
| `Surface3Brush` Dark | `#161D2B` | 主题资源，悬停/更高层表面；App.axaml:26 |
| `BorderBrush` Dark | `#1E2636` | 通用边框；App.axaml:27 |
| `BorderStrongBrush` Dark | `#374151` | 强调边框、输入控件边框；App.axaml:28 |
| `TextPrimaryBrush` Dark | `#E0E0E0` | 主文本；App.axaml:29 |
| `TextSecondaryBrush` Dark | `#9CA3AF` | 次级文本；App.axaml:30 |
| `TextMutedBrush` Dark | `#6B7280` | 弱化文本；App.axaml:31 |
| `HoverSurfaceBrush` Dark | `#2A3347` | 悬停表面；App.axaml:32 |

应用级 `StaticResource` 转换器资源位于 `App.axaml:36-43`：`BoolToColorConverter`、`InverseBoolConverter`、`GreaterThanZeroConverter`、`EqualConverter`、`NotNullConverter`、`TimeAgoConverter`、`LevelToColorConverter`、`LevelToIconConverter`。页面通过 `StaticResource` 引用这些转换器；未发现页面级颜色资源字典。

应用级 `DynamicResource` 主要引用上述 Surface、Border 和 Text 画刷。对 XAML 文件统计结果：`DynamicResource` 274 次，`StaticResource` 23 次；统计排除了 `bin` 和 `obj`。

### 1.2 Button 样式

| Style Selector | Background | Foreground | Border | Padding | Font | CornerRadius | 使用范围 |
|---|---|---|---|---|---|---|---|
| `Button.nav-button` | `Transparent` | `TextSecondaryBrush` | `0` | `16,8` | `14` / `Medium` | `6` | MainWindow 顶部导航；App.axaml:49-58 |
| `Button.nav-button:pointerover` | `Surface3Brush` | `TextPrimaryBrush` | 未设置 | 未设置 | 未设置 | 继承 6 | 导航悬停；App.axaml:60-63 |
| `Button.nav-button:pressed` | `BorderBrush` | `#FFFFFF` | 未设置 | 未设置 | 未设置 | 继承 6 | 导航按下；App.axaml:65-68 |
| `Button.nav-button.selected` | `#38BDF8` | `#FFFFFF` | 未设置 | 未设置 | 未设置 | 继承 6 | 定义了 selected 状态；App.axaml:70-73 |
| `Button.primary` | `#38BDF8` | `#FFFFFF` | `0` | `16,10` | `14` / `SemiBold` | `999` | 主要操作；App.axaml:75-84 |
| `Button.primary:pointerover` | `#0EA5E9` | 继承 | 继承 | 继承 | 继承 | 继承 999 | 主要操作悬停；App.axaml:86-88 |
| `Button.primary:pressed` | `#0284C7` | 继承 | 继承 | 继承 | 继承 | 继承 999 | 主要操作按下；App.axaml:90-92 |
| `Button.secondary` | `BorderBrush` | `TextPrimaryBrush` | `1` / `BorderStrongBrush` | `16,10` | `14` / `Medium` | `999` | 次要操作；App.axaml:94-104 |
| `Button.secondary:pointerover` | `HoverSurfaceBrush` | 继承 | `BorderStrongBrush` | 继承 | 继承 | 继承 999 | 次要操作悬停；App.axaml:106-109 |
| `Button.icon-button` | `Transparent` | `TextSecondaryBrush` | `0` | `8` | 未设置 | `999` | 任务、通知、开发工具、关闭和更多按钮；App.axaml:125-134 |
| `Button.icon-button` | `Transparent` | `TextSecondaryBrush` | `0` | `8` | 未设置 | `999` | 尺寸 `36x36`；App.axaml:130-132 |
| `Button.icon-button:pointerover` | `Surface3Brush` | `TextPrimaryBrush` | 继承 | 继承 | 继承 | 继承 999 | 图标按钮悬停；App.axaml:136-139 |
| `Button.icon-button:pressed` | `BorderBrush` | `#FFFFFF` | 继承 | 继承 | 继承 | 继承 999 | 图标按钮按下；App.axaml:141-144 |

页面还存在绕过全局样式的 Button 设置：`PluginManagementView` 和 `PluginPermissionDialog` 直接设置 `CornerRadius="999"`、背景、边框和 Padding；`ModDetailPanel` 直接设置 `Height="36"`、背景和前景；`NotificationPanel` 的操作按钮直接设置 `Padding="8,4"`、`FontSize="11"`。

### 1.3 RadioButton、TextBox、ComboBox、CheckBox

| Style/控件 | 当前值 | 使用范围 |
|---|---|---|
| `RadioButton.nav-button` | Background `Transparent`；Foreground `TextSecondaryBrush`；BorderThickness `0`；Padding `16,8`；FontSize `14`；FontWeight `Medium`；CornerRadius `6`；自定义 ControlTemplate 内嵌 Border | MainWindow 顶部四个导航项；App.axaml:170-209 |
| `RadioButton.filter-button` | Background `Transparent`；Foreground `TextSecondaryBrush`；BorderThickness `1`；BorderBrush `BorderStrongBrush`；Padding `16,8`；FontSize `13`；FontWeight `Medium`；CornerRadius `6`；自定义 ControlTemplate 内嵌 Border | LibraryView 统计筛选项；App.axaml:211-253 |
| `TextBox` | Background `Surface2Brush`；Foreground `TextPrimaryBrush`；BorderBrush `BorderStrongBrush`；BorderThickness `1`；Padding `12,8`；CornerRadius `6` | 全局 TextBox；App.axaml:111-122 |
| `TextBox:focus` | BorderBrush `#38BDF8` | 全局 TextBox 聚焦状态；App.axaml:120-122 |
| `ComboBox` | Background `Surface2Brush`；Foreground `TextPrimaryBrush`；BorderBrush `BorderStrongBrush`；BorderThickness `1`；CornerRadius `6`；Padding `12,8` | 全局 ComboBox；App.axaml:275-291 |
| `ComboBox:focus` | BorderBrush `#38BDF8` | 全局 ComboBox 聚焦状态；App.axaml:285-287 |
| `ComboBox:pointerover` | BorderBrush `BorderStrongBrush` | 全局 ComboBox 悬停状态；App.axaml:289-291 |
| `CheckBox` | Foreground `TextPrimaryBrush`，没有全局尺寸、Padding、CornerRadius 或 Background Setter | 全局 CheckBox；App.axaml:293-296 |
| `ToggleSwitch` | 未发现 | 全项目 `src/HD2Mimnt.UI` XAML |

### 1.4 Badge、状态指示器和其它全局结构

| Style/控件 | 当前值 | 使用范围 |
|---|---|---|
| `Border.badge` | Background `#EF4444`；CornerRadius `999`；Padding `6,2`；MinWidth `20`；Height `20` | MainWindow 运行任务数和未读通知数；App.axaml:146-153 |
| `Border.badge > TextBlock` | Foreground `#FFFFFF`；FontSize `11`；FontWeight `Bold`；水平/垂直居中 | Badge 文本；App.axaml:155-161 |
| `Ellipse.status-indicator` | Width `8`；Height `8`；VerticalAlignment `Center` | MainWindow 底部部署状态；App.axaml:163-168 |
| `TabControl` 局部 Style | Padding `0` | MainWindow 隐藏 TabControl；MainWindow.axaml:137-144 |
| `TabItem` 局部 Style | `IsVisible=False` | MainWindow 页面承载；MainWindow.axaml:141-144 |
| `ListBox` | Background `Transparent`；BorderThickness `0` | 全局 ListBox；App.axaml:255-259 |
| `ListBoxItem` | Padding `0`；Margin `0`；Background `Transparent` | 全局 ListBoxItem；App.axaml:261-265 |
| `ListBoxItem:selected /template/ ContentPresenter` | Background `Transparent` | 全局选中状态；App.axaml:267-269 |
| `ListBoxItem:pointerover /template/ ContentPresenter` | Background `Transparent` | 全局悬停状态；App.axaml:271-273 |

### 1.5 Border、Panel、List、Dialog、Popup、Overlay、StatusBar

- `Border` 没有应用级通用 Style；页面直接设置 Background、BorderBrush、BorderThickness、Padding、Margin 和部分 CornerRadius。
- `Panel` 没有应用级 Style；用于叠放空状态、列表和 Overlay 内容。
- `ListBox` 和 `ItemsControl` 以页面内 ItemTemplate 负责行/卡片外观。`LibraryView`、`ProfileView`、`TaskCenterView` 和 `PluginManagementView` 明确使用 `VirtualizingStackPanel`；其它 `ItemsControl` 是否虚拟化由局部模板决定，当前没有全局 ItemsControl Style。
- `Separator` 只在 `LibraryView` 模组操作 MenuFlyout 中发现 1 处：`LibraryView.axaml:390`。
- `Icon`、`PathIcon`、`SymbolIcon` 未发现；当前图标主要使用 TextBlock 内的 Emoji/Unicode 字符。
- `Popup` 元素未发现；菜单使用 `MenuFlyout`，位置为 `BottomEdgeAlignedRight`，见 `LibraryView.axaml:54`、`383`。
- 独立 Dialog 使用 `PluginPermissionDialog.axaml` 的 `Window`，不是 `Dialog` 控件。
- Overlay 使用 Grid/Border 叠放并设置 `ZIndex`：MainWindow 的通知/任务中心，LibraryView 的详情/部署，PluginManagementView 的执行历史。
- `StatusBar` 控件未发现；底部状态栏由 `MainWindow.axaml:169-206` 的 Border + Grid + StackPanel 实现。
- `BoxShadow`、`Shadow` 未发现。

## 2. Card / Surface 审计

下表中的“层级”从页面外层容器向内容项递增记录。`Border` 作为卡片使用的判断依据仅记录实际结构：是否有背景、边框、Padding、圆角或作为独立功能区域承载内容；不对其视觉必要性下结论。

| 文件 | 控件/区域 | 层级 | 当前实现 | 用途 |
|---|---|---:|---|---|
| `MainWindow.axaml:31` | 顶部导航 Border | 1 | `Surface2Brush`，下边框 `0,0,0,1`，Padding `16,12` | 顶部导航区 |
| `MainWindow.axaml:133` | 主内容 Border | 1 | `Surface0Brush` | 承载隐藏 TabControl |
| `MainWindow.axaml:169` | 底部状态 Border | 1 | `Surface2Brush`，上边框 `0,1,0,0`，Padding `16,8` | 状态栏 |
| `MainWindow.axaml:208-215` | 通知 Overlay / Panel | 1 / 2 | 外层半透明 `#CC05070C`；内层 `Surface2Brush`，边框 1，`460x560` | 通知面板覆盖层 |
| `MainWindow.axaml:216-220` | 任务 Overlay / Panel | 1 / 2 | 外层半透明 `#CC05070C`；内层 `Surface2Brush`，边框 1，`760x560` | 任务中心覆盖层 |
| `LibraryView.axaml:20` | 页面标题栏 | 1 | `Surface2Brush`，下边框，Padding `24,20` | 标题和主操作 |
| `LibraryView.axaml:69` | 筛选栏 | 1 | `Surface1Brush`，无圆角 | 统计筛选 |
| `LibraryView.axaml:109` | 搜索工具栏 | 1 | `Surface0Brush`，下边框，Padding `24,12` | 搜索、分类、排序 |
| `LibraryView.axaml:112-143` | 搜索框 Border | 2 | `Surface2Brush`，强边框 1，CornerRadius `6`，Padding `12,8` | 包裹 TextBox 和搜索/清除字符 |
| `LibraryView.axaml:164` | 列表头 | 1 | `Surface1Brush`，下边框，Padding `24,12` | 全选、反选、数量 |
| `LibraryView.axaml:187` | 模组列表区域 | 1 | `Surface0Brush` | 列表及空/错误/加载状态 |
| `LibraryView.axaml:289` | 模组列表行 | 2 | `Surface2Brush`，下边框 1，Padding 由 `ModItemPadding` 绑定 | 全局 Mod 条目 |
| `LibraryView.axaml:302` | Mod 图标容器 | 3 | `Surface3Brush`，`48x48`，CornerRadius `6`，裁剪内容 | Mod 缩略图/占位符 |
| `LibraryView.axaml:346-363` | Mod 状态标签 | 3 | 绿色/蓝色背景，CornerRadius `999`，Padding `8,2` | 启用/部署状态 |
| `LibraryView.axaml:406` | 批量操作栏 | 1 | `Surface3Brush`，上边框，Padding `24,12` | 已选 Mod 的批量操作 |
| `LibraryView.axaml:437-441` | Mod 详情 Overlay 内 panel | 1 / 2 | 外层 `#CC000000`；`ModDetailPanel` 固定 Width `400` | 详情侧面板 |
| `ProfileView.axaml:24` | Profile 列表外层 Border | 1 | `Surface2Brush`，边框 1，Padding `12` | Profile 列表容器 |
| `ProfileView.axaml:34` | Profile 列表项 | 2 | `Surface3Brush`，CornerRadius `6`，Padding `12`，底部 Margin `8` | 单个 Profile |
| `ProfileView.axaml:39` | 当前配置标签 | 3 | `#22C55E`，CornerRadius `4`，Padding `5,2` | 标识当前 Profile |
| `ProfileView.axaml:73` | Profile 详情 Border | 1 | `Surface2Brush`，边框 1，Padding `20` | 当前 Profile 详情 |
| `ProfileView.axaml:93` | Profile Mod 行 Border | 2 | 仅 Padding `8,6`，无背景/边框 | 当前 Profile 的 Mod 项 |
| `ProfileView.axaml:108` | Profile 对比 Border | 1 | `Surface2Brush`，边框 1，Padding `20` | 配置对比 |
| `MoreView.axaml:5,12,15,18,21,28,82` | More 页面功能 Border | 1 | 多处 `Surface2Brush`、边框 1、Padding `16` | 开发工具、插件、备份、日志、性能、外部工具、关于 |
| `MoreView.axaml:41` | 外部工具条目 | 2 | 固定色 `#151B26`，Padding `10`，无 BorderBrush | 外部工具记录 |
| `MoreView.axaml:59` | 外部工具编辑区 | 2 | 固定色 `#0B1018`，Padding `12`，上方 Margin `8` | 编辑外部工具 |
| `SettingsView.axaml:6,17,24,32,40` | 设置分类区域 | 1 | 每个区域 `Surface2Brush`，边框 1，Padding `16` | 游戏、部署、模组库、外观、缓存设置 |
| `TaskCenterView.axaml:8` | 任务条目 | 2 | `Surface2Brush`，边框 1，Padding `12`，底部 Margin `8` | 单个后台任务 |
| `BackupView.axaml:9` | 备份条目 | 1 | 固定色 `#151B26`，Padding `10`，底部 Margin `6` | 单个备份 |
| `PluginManagementView.axaml:26-32` | `stat-card` Style | 1 | `Surface2Brush`，边框 1，CornerRadius `6`，Padding `16` | 插件统计/操作四格区域 |
| `PluginManagementView.axaml:118` | 插件条目 | 1 | `Surface2Brush`，边框 1，CornerRadius `6`，Padding `20`，底部 Margin `12` | 单个插件 |
| `PluginManagementView.axaml:126` | 插件图标容器 | 2 | `Surface3Brush`，`64x64`，CornerRadius `8` | 插件占位图标 |
| `PluginManagementView.axaml:147,163` | 插件状态/权限标签 | 2 | CornerRadius `999`，Padding `8,4` | 状态和权限模式 |
| `PluginManagementView.axaml:306` | 无插件空状态 Border | 1 | `Surface2Brush`，边框 1，CornerRadius `6`，Padding `48` | 空状态 |
| `PluginManagementView.axaml:371` | 执行历史 Overlay 内 panel | 1 / 2 | 外层半透明；内层 `Surface2Brush`，边框 1，`680x460`，Padding `16` | 执行历史弹层 |
| `PluginManagementView.axaml:381` | 执行记录条目 | 3 | 固定色 `#151B26`，Padding `10`，底部 Margin `6` | 历史记录 |
| `NotificationPanel.axaml:14` | 通知面板外框 | 1 | 边框 1，CornerRadius `8` | 通知控件外框 |
| `NotificationPanel.axaml:17` | 通知标题区 | 2 | `Surface3Brush`，Padding `16,12`，顶部圆角 `8,8,0,0` | 标题和关闭按钮 |
| `NotificationPanel.axaml:40` | 通知条目 | 2 | `Surface1Brush`，底边框 1，Padding `16,12` | 单条通知 |
| `NotificationPanel.axaml:46` | 通知级别图标容器 | 3 | `32x32`，CornerRadius `999` | 通知级别颜色和符号 |
| `NotificationPanel.axaml:118` | 通知底部操作区 | 2 | `Surface3Brush`，底部圆角 `0,0,8,8`，Padding `12,8` | 清除全部 |
| `ModDetailPanel.axaml:30` | 详情面板外框 | 1 | 固定色 `#0F0F0F`，左边框 1，Padding `24` | 详情侧面板 |
| `ModDetailPanel.axaml:22-27` | `Border.section` Style | 2 | 固定色 `#1A1A1A`，CornerRadius `8`，Padding `16`，底部 Margin `12` | 详情的多个分区 |
| `ModDetailPanel.axaml:88,97,106` | 状态标签 | 3 | CornerRadius `4`，Padding `8,4` | 启用、部署、未启用 |
| `ModDetailPanel.axaml:148` | 标签项 | 3 | 固定色 `#2A2A2A`，CornerRadius `4`，Padding `8,4`，Margin `0,0,8,8` | Mod Tags |
| `PluginPermissionDialog.axaml:23` | Dialog 标题区 | 1 | `Surface2Brush`，下边框，Padding `24,20` | 权限请求标题 |
| `PluginPermissionDialog.axaml:40` | 插件信息区域 | 1 | `Surface2Brush`，CornerRadius `6`，边框 1，Padding `16` | 插件信息 |
| `PluginPermissionDialog.axaml:108` | 警告区域 | 1 | `#1E1506`，边框 `#E9A568`，CornerRadius `6`，Padding `16` | 高风险警告 |
| `PluginPermissionDialog.axaml:130` | 权限条目 | 1 | `Surface2Brush`，CornerRadius `6`，边框 1，Padding `12`，底部 Margin `8` | 单项权限 |
| `PluginPermissionDialog.axaml:191` | Dialog 操作区 | 1 | `Surface2Brush`，上边框，Padding `24,16` | 拒绝/允许/保存操作 |

当前可见的嵌套 Border 结构主要有：MainWindow Overlay 外层 Border + 内层 panel；NotificationPanel 外框 + 标题/条目/底部区域；ModDetailPanel 外框 + 多个 `Border.section`；MoreView 页面功能 Border 内还包含外部工具条目和编辑 Border；PluginPermissionDialog 外层区域内包含信息、警告、权限条目和操作区域。源码没有名为 `Card` 的自定义控件或 `Card` Style。

## 3. 圆角审计

### 3.1 全项目 CornerRadius 明细

| 文件 | 控件/Style | CornerRadius | 用途 | 是否全局 Style |
|---|---|---:|---|---|
| `App.axaml:56` | `Button.nav-button` | 6 | 顶部导航按钮 | 是 |
| `App.axaml:82` | `Button.primary` | 999 | 主要操作按钮 | 是 |
| `App.axaml:102` | `Button.secondary` | 999 | 次要操作按钮 | 是 |
| `App.axaml:117` | `TextBox` | 6 | 输入框 | 是 |
| `App.axaml:132` | `Button.icon-button` | 999 | 图标按钮 | 是 |
| `App.axaml:149` | `Border.badge` | 999 | 数字徽标 | 是 |
| `App.axaml:178` | `RadioButton.nav-button` | 6 | 导航模板内 Border | 是 |
| `App.axaml:185` | 导航 ControlTemplate Border | TemplateBinding | 继承导航按钮圆角 | 是 |
| `App.axaml:220` | `RadioButton.filter-button` | 6 | 筛选按钮 | 是 |
| `App.axaml:227` | 筛选 ControlTemplate Border | TemplateBinding | 继承筛选按钮圆角 | 是 |
| `App.axaml:281` | `ComboBox` | 6 | 下拉框 | 是 |
| `MainWindow.axaml` | 任务/通知/状态栏 Border | 未设置 | 全局无圆角；仅边框/背景 | 否 |
| `LibraryView.axaml:116` | 搜索框 Border | 6 | 搜索输入包装 | 否 |
| `LibraryView.axaml:305` | Mod 图标 Border | 6 | 缩略图容器 | 否 |
| `LibraryView.axaml:347` | 已启用状态 Border | 999 | 状态标签 | 否 |
| `LibraryView.axaml:356` | 已部署状态 Border | 999 | 状态标签 | 否 |
| `ProfileView.axaml:34` | Profile 列表项 Border | 6 | Profile 条目 | 否 |
| `ProfileView.axaml:39` | 当前配置 Border | 4 | 当前 Profile 标签 | 否 |
| `PluginManagementView.axaml:30` | `stat-card` Style | 6 | 统计/操作卡片 | 否，页面局部 Style |
| `PluginManagementView.axaml:80,95` | 顶部插件操作 Button | 999 | 安装/刷新 | 否，控件局部值 |
| `PluginManagementView.axaml:121` | 插件条目 Border | 6 | 插件条目 | 否 |
| `PluginManagementView.axaml:128` | 插件图标 Border | 8 | 插件图标容器 | 否 |
| `PluginManagementView.axaml:147` | 状态 Badge | 999 | 插件状态 | 否 |
| `PluginManagementView.axaml:164` | 权限 Badge | 999 | 插件权限模式 | 否 |
| `PluginManagementView.axaml:216,234,251,268,285,329` | 插件操作按钮 | 999 | Enable/Disable/History/权限/Uninstall/空状态安装 | 否 |
| `PluginManagementView.axaml:309` | 无插件空状态 Border | 6 | 空状态容器 | 否 |
| `PluginManagementView.axaml:371` | 历史弹层内 Border | 未设置 | 执行历史容器 | 否 |
| `PluginPermissionDialog.axaml:41` | 插件信息 Border | 6 | 信息容器 | 否 |
| `PluginPermissionDialog.axaml:49` | 插件图标 Border | 8 | 图标容器 | 否 |
| `PluginPermissionDialog.axaml:91` | 权限模式 Badge | 999 | 权限模式 | 否 |
| `PluginPermissionDialog.axaml:112` | 警告 Border | 6 | 警告区域 | 否 |
| `PluginPermissionDialog.axaml:131` | 权限项 Border | 6 | 权限条目 | 否 |
| `PluginPermissionDialog.axaml:162` | High Risk Badge | 999 | 风险标签 | 否 |
| `PluginPermissionDialog.axaml:202,220,239` | Dialog 主要操作按钮 | 999 | Deny/Allow Once/Allow Always | 否 |
| `NotificationPanel.axaml:14` | 面板外框 | 8 | 通知面板 | 否 |
| `NotificationPanel.axaml:17` | 标题区 | `8,8,0,0` | 顶部圆角 | 否 |
| `NotificationPanel.axaml:48` | 通知级别图标 | 999 | 圆形级别图标 | 否 |
| `NotificationPanel.axaml:121` | 底部区域 | `0,0,8,8` | 底部圆角 | 否 |
| `ModDetailPanel.axaml:24` | `Border.section` Style | 8 | 详情分区 | 否，控件局部 Style |
| `ModDetailPanel.axaml:58` | Mod 详情图标 | 12 | 详情图像容器 | 否 |
| `ModDetailPanel.axaml:89,98,107,149` | 状态/标签 Border | 4 | 状态和标签 | 否 |
| `DevToolsWindow.axaml:33,80,98` | 输入/进度/状态 Border | 8 | 开发工具分区 | 否 |

### 3.2 圆角值分布

当前源码中明确出现的圆角值包括：

- `999`：全局 Primary/Secondary/Icon/Badge、Library 状态标签、插件操作和状态标签、权限 Dialog 操作与标签。
- `12`：`ModDetailPanel` 的 `120x120` 详情图像。
- `8`：NotificationPanel、Plugin 图标/信息区、ModDetailPanel 分区、DevTools 分区。
- `6`：导航、TextBox、ComboBox、搜索框、列表项、插件条目、权限项、警告、空状态。
- `4`：Profile 当前标签、ModDetailPanel 状态/标签。
- `0`：未发现 `CornerRadius="0"` 的显式声明；边角未设置的 Border 不等同于显式值 0。
- `16+`：未发现 `CornerRadius` 大于 16 的数值声明；`999` 另列为胶囊/圆形值。

## 4. 页面逐页视觉结构审计

### 4.1 MainWindow

文件：`src/HD2Mimnt.UI/MainWindow.axaml`

1. **最外层容器：** `Grid RowDefinitions="Auto,*,Auto"`，顶部导航、中间内容、底部状态三个区域，另有两个同属 Grid 的 Overlay Border。
2. **Border/Surface 层数：** 顶部、内容、底部各一层；通知和任务 Overlay 各为半透明外层 + 固定尺寸内层，共两层。
3. **Card 化结构：** 通知和任务中心内层 Border 具有背景、边框、固定尺寸和 Padding；源码没有 Card 控件。
4. **顶部工具栏：** 顶部 Border 内是应用名、四个 RadioButton 导航、游戏状态、任务中心按钮、通知按钮、开发工具按钮、版本号。
5. **列表实现：** MainWindow 本身没有列表；页面通过隐藏 `TabControl`/`TabItem` 承载。
6. **状态表现：** 游戏状态用 8x8 Ellipse + 文本；任务和通知用 999 圆角红色 Badge；底部部署状态用 8x8 Ellipse + 文本。
7. **按钮：** 四个导航 RadioButton；任务、通知、开发工具三个 `icon-button`；Overlay 关闭按钮；快捷键 Ctrl+1 至 Ctrl+4、Escape。
8. **Badge：** 有，运行任务数和未读通知数各一个。
9. **Pill：** 全局 icon-button 和 badge 是 999 圆角；顶部导航是 6 圆角；无独立命名为 Pill 的控件。
10. **大面积空白：** 中间内容区随当前页面撑开；固定 Overlay 仅占 `460x560` 或 `760x560`，剩余区域为半透明遮罩。
11. **固定 MaxWidth：** MainWindow 本身无 MaxWidth；窗口 `Width=1280`、`Height=800`、`MinWidth=960`、`MinHeight=600`。
12. **视觉层级事实：** 顶部与底部使用 `Surface2Brush`，主内容使用 `Surface0Brush`；Overlay 使用固定半透明颜色和内层 `Surface2Brush`。
13. **主要操作：** 页面导航、打开任务中心、打开通知、打开开发工具。
14. **次要操作：** Overlay 关闭、游戏状态和版本信息。

### 4.2 LibraryView

文件：`src/HD2Mimnt.UI/Views/LibraryView.axaml`

1. **最外层容器：** `Panel`，内部 `Grid RowDefinitions="Auto,Auto,Auto,Auto,*,Auto"`。
2. **Border/Surface 层数：** 标题、筛选、搜索、列表头、列表、批量栏为 6 个主要区域；列表行、图标、状态标签和搜索包装继续嵌套。
3. **Card 化结构：** Mod 列表行使用 `Surface2Brush` + 底边框；搜索框和 Mod 图标使用 Border 包装；没有名为 Card 的控件。
4. **顶部工具栏：** 标题栏内包括添加模组、部署方式 ComboBox、部署、清理、取消和更多 MenuFlyout。
5. **列表实现：** `ListBox` + `VirtualizingStackPanel`，多选；行内包含 CheckBox、图标、名称/版本/分类/作者、状态标签、大小和更多菜单。
6. **状态表现：** 空状态使用 `📦`、标题、说明和添加按钮；无结果、错误和加载分别使用文本/Unicode 字符/ProgressBar；行内启用和部署用 999 标签。
7. **按钮：** 添加、部署、清理、取消、清除筛选、反选、空状态添加、错误重试、批量启用/禁用/删除、行更多菜单和详情操作。
8. **Badge：** 没有使用 `Border.badge`；行状态标签使用独立 Border。
9. **Pill：** 启用/部署状态标签 `CornerRadius=999`；顶部更多和行更多使用 `icon-button`。
10. **大面积空白：** 空、无结果、错误、加载状态均在列表区域水平/垂直居中；错误文本 MaxWidth `400`。
11. **固定 MaxWidth：** 仅错误信息 `MaxWidth=400`；窗口设计尺寸 `1200x800`，详情 panel Width `400`，加载 ProgressBar Width `260`。
12. **视觉层级事实：** 标题和列表行边框使用 `Surface2Brush`；筛选与列表头使用 `Surface1Brush`；列表背景使用 `Surface0Brush`；批量栏使用 `Surface3Brush`。
13. **主要操作：** 添加模组、部署、清理、筛选、搜索、选择和批量操作。
14. **次要操作：** 更多菜单、清除筛选、反选、清除搜索、详情、删除和 Overlay 关闭。

### 4.3 ProfileView

文件：`src/HD2Mimnt.UI/Views/ProfileView.axaml`

1. **最外层容器：** `Grid RowDefinitions="Auto,Auto,*" Margin="24"`，内容区再使用 `Grid ColumnDefinitions="2*,3*"`。
2. **Border/Surface 层数：** 左侧 Profile 列表 Border 和右侧详情/对比 Border；列表项另有 Border，当前配置标签另有 Border。
3. **Card 化结构：** 左侧列表和右侧详情是 Surface2 容器；单个 Profile 是 Surface3 + CornerRadius 6 的条目；Profile Mod 行仅有 Padding。
4. **顶部工具栏：** 标题/说明左侧，新 Profile TextBox 和新建按钮右侧；第二行是搜索框和搜索说明。
5. **列表实现：** 左侧 Profile `ListBox` + `VirtualizingStackPanel`；详情 `ProfileMods` 使用 ListBox + `VirtualizingStackPanel`；对比结果也使用 ListBox + `VirtualizingStackPanel`。
6. **状态表现：** 当前 Profile 用绿色 `CornerRadius=4` 标签；空列表、无搜索结果、加载错误分别用居中文本和按钮。
7. **按钮：** 新建、切换、复制、重命名、删除、清除搜索、重试、比较、关闭对比；Profile Mod 使用 CheckBox。
8. **Badge：** 没有 `Border.badge`；当前配置标签是局部绿色标签。
9. **Pill：** 未发现 `CornerRadius=999`；当前配置标签为 4。
10. **大面积空白：** 空/无结果/错误状态在左侧列表容器内居中；右侧详情在无选择时依赖绑定 Fallback 文本。
11. **固定 MaxWidth：** 错误信息 `MaxWidth=260`；新 Profile 输入 Width `180`；对比筛选 ComboBox Width `130`。
12. **视觉层级事实：** 外层列表/详情/对比使用 `Surface2Brush`；Profile 条目使用 `Surface3Brush`；状态和文本颜色区分当前状态与普通状态。
13. **主要操作：** 创建 Profile、切换 Profile、选择 Profile、查看当前 Profile 的 Mod。
14. **次要操作：** 复制、重命名、删除、搜索、比较和关闭比较。

### 4.4 SettingsView

文件：`src/HD2Mimnt.UI/Views/SettingsView.axaml`

1. **最外层容器：** `ScrollViewer` + `StackPanel Margin="24" Spacing="16" MaxWidth="760"`。
2. **Border/Surface 层数：** 五个设置分类 Border，每个使用 `Surface2Brush`、边框 1、Padding `16`；页面末尾无 Border 的保存操作区。
3. **Card 化结构：** 每个分类区域是独立的 Border 容器；没有 Card 控件或圆角显式值。
4. **顶部工具栏：** 标题、说明和设置分类 ComboBox；分类区域按绑定可见性切换。
5. **列表实现：** 没有 ListBox/ItemsControl；使用 ComboBox、TextBox、CheckBox 和 Grid。
6. **状态表现：** 缓存信息由 `CacheStatus` 文本表示；底部由 `StatusMessage` 文本表示。
7. **按钮：** 检测、自动识别、刷新缓存信息、清理图片缓存、保存设置、恢复默认。
8. **Badge：** 无。
9. **Pill：** 无显式圆角；ComboBox 通过全局 Style 使用 CornerRadius 6。
10. **大面积空白：** StackPanel 最大宽度为 `760`，在宽窗口中右侧未被设置内容占用。
11. **固定 MaxWidth：** `MaxWidth=760`；ComboBox Width 为 `160` 或 `180`。
12. **视觉层级事实：** 各设置分类统一使用 `Surface2Brush` + 1px Border；页面背景由上层承载。
13. **主要操作：** 保存设置、选择设置分类、检测/自动识别游戏。
14. **次要操作：** 恢复默认、刷新缓存、清理图片缓存。

### 4.5 MoreView

文件：`src/HD2Mimnt.UI/Views/MoreView.axaml`

1. **最外层容器：** `ScrollViewer` + `StackPanel Margin="24" Spacing="16" MaxWidth="860"`。
2. **Border/Surface 层数：** 页面包含多个同级 `Surface2Brush` Border；插件、备份和日志 View 作为子内容；外部工具条目和编辑区继续嵌套 Border。
3. **Card 化结构：** 开发工具、插件、备份、日志、性能、外部工具、关于均由 Border 分隔；外部工具条目是第二层固定背景条目。
4. **顶部工具栏：** 仅有“更多”标题；第一个区块提供开发工具入口；外部工具区域有添加工具按钮。
5. **列表实现：** 外部工具使用 `ItemsControl`；插件、备份、日志由内嵌 View 自己实现列表。
6. **状态表现：** 外部工具有加载、未登记、状态消息和编辑状态；性能区域显示固定文字；子页面各自显示状态。
7. **按钮：** 打开开发工具、添加工具、外部工具启用/编辑/删除/启动/保存/取消；子 View 的备份、日志、插件按钮。
8. **Badge：** 无显式 Badge。
9. **Pill：** 外部工具 CheckBox 和按钮使用全局控件样式；MoreView 自身没有 CornerRadius。
10. **大面积空白：** MaxWidth `860` 限制内容宽度，宽窗口右侧不承载内容；页面整体通过 ScrollViewer 纵向排列多个区域。
11. **固定 MaxWidth：** `MaxWidth=860`。
12. **视觉层级事实：** 各功能区同样使用 `Surface2Brush` 和 1px Border，内部子 View 继续使用自己的 Surface/Border。
13. **主要操作：** 进入开发工具、插件管理、备份、日志和外部工具管理。
14. **次要操作：** 关于信息和性能基线说明。

### 4.6 TaskCenterView

文件：`src/HD2Mimnt.UI/Views/TaskCenterView.axaml`

1. **最外层容器：** `Grid RowDefinitions="Auto,*" Margin="24"`。
2. **Border/Surface 层数：** 列表条目 Border 一层；页面本身无外层 Border。
3. **Card 化结构：** 每个任务条目 `Surface2Brush` + 1px Border + Padding `12`。
4. **顶部工具栏：** 标题和状态消息 StackPanel；没有独立工具栏按钮。
5. **列表实现：** `ListBox` + `VirtualizingStackPanel`；水平滚动关闭。
6. **状态表现：** 每项显示 Name、Message、ProgressBar；空状态和错误状态使用居中 StackPanel，错误状态有红色标题和重试按钮。
7. **按钮：** 每个运行任务有取消按钮；错误状态有重试按钮。
8. **Badge：** 无。
9. **Pill：** 无页面内显式圆角。
10. **大面积空白：** 空状态和错误状态在列表区域居中；错误信息 MaxWidth `360`。
11. **固定 MaxWidth：** 错误信息 `MaxWidth=360`；ProgressBar Height `6`。
12. **视觉层级事实：** 页面通过 MainWindow 任务 Overlay 内层 Border 承载，TaskCenterView 内部任务条目再次使用 Surface2。
13. **主要操作：** 取消后台任务。
14. **次要操作：** 失败后重试。

### 4.7 PluginManagementView

文件：`src/HD2Mimnt.UI/Views/PluginManagementView.axaml`

1. **最外层容器：** `Grid RowDefinitions="Auto,Auto,*,Auto" Margin="24"`，根 Background `Surface0Brush`。
2. **Border/Surface 层数：** 顶部统计区域四个 `stat-card`；插件列表条目；无插件空状态；底部状态栏；执行历史 Overlay 另有两层。
3. **Card 化结构：** 统计四格、插件条目、空状态和执行记录均为 Border 容器；页面局部定义 `Border.stat-card`。
4. **顶部工具栏：** 标题/说明；统计区四列，其中第四列放安装和刷新操作。
5. **列表实现：** 外层 ScrollViewer，内部 `ItemsControl`；源码未在该 ItemsControl 内声明 `VirtualizingStackPanel`。
6. **状态表现：** Total/Enabled/Disabled 数值字号 `28`；插件名称旁有两个 `CornerRadius=999` 标签；底部显示状态消息和 Loading 文本；无插件时使用图标、文本和安装按钮。
7. **按钮：** 安装、刷新、启用、禁用、历史、权限、卸载、关闭历史、空状态安装。
8. **Badge：** 状态和权限模式是局部 999 Border，未使用全局 `Border.badge`。
9. **Pill：** 顶部操作、插件状态、权限模式、插件行操作和空状态安装按钮均显式使用 `CornerRadius=999`。
10. **大面积空白：** 无插件状态 Border 使用 Padding `48`；历史弹层固定 `680x460`。
11. **固定 MaxWidth：** 插件描述 `MaxWidth=600`；设计尺寸 `1200x700`；历史弹层 `680x460`；操作按钮 MinWidth `100`。
12. **视觉层级事实：** 统计与插件条目均使用 `Surface2Brush`；插件图标使用 `Surface3Brush`；历史记录使用固定色 `#151B26`。
13. **主要操作：** 安装插件、启用/禁用、卸载、权限管理。
14. **次要操作：** 刷新、查看执行历史、关闭历史弹层。

### 4.8 BackupView

文件：`src/HD2Mimnt.UI/Views/BackupView.axaml`

1. **最外层容器：** `Grid RowDefinitions="Auto,*,Auto" MinHeight="300"`。
2. **Border/Surface 层数：** 页面本身无外层 Border；备份列表项使用一层固定背景 Border。
3. **Card 化结构：** 每个备份条目 `#151B26` + Padding `10` + 底部 Margin `6`，无 CornerRadius 和 BorderBrush。
4. **顶部工具栏：** 标题/说明，刷新和创建备份按钮。
5. **列表实现：** `ListBox`，未在页面声明 ItemsPanel 或 VirtualizingStackPanel。
6. **状态表现：** 底部 StatusMessage 和 `处理中：{bool}` 文本。
7. **按钮：** 刷新、创建备份、恢复、删除。
8. **Badge：** 无。
9. **Pill：** 条目无圆角；按钮继承全局 Primary/Secondary 999 圆角。
10. **大面积空白：** Grid 主列表区域由 `*` 撑开；最小高度 `300`。
11. **固定 MaxWidth：** 无 MaxWidth；最小高度 `300`。
12. **视觉层级事实：** 备份条目使用固定深色 `#151B26`，与全局 `Surface2Brush` 不同。
13. **主要操作：** 创建备份、恢复备份。
14. **次要操作：** 刷新、删除备份。

### 4.9 LogView

文件：`src/HD2Mimnt.UI/Views/LogView.axaml`

1. **最外层容器：** `Grid RowDefinitions="Auto,*,Auto" MinHeight="360"`。
2. **Border/Surface 层数：** 没有 Border；列表和顶部/底部 Grid 直接布局。
3. **Card 化结构：** 未声明 Card 或 Border。
4. **顶部工具栏：** 标题、说明、错误筛选 CheckBox，刷新/复制/导出按钮。
5. **列表实现：** `ListBox ItemsSource="{Binding Entries}"`，未声明自定义 ItemTemplate 或 ItemsPanel。
6. **状态表现：** 底部 StatusMessage 和 `加载中：{bool}` 文本。
7. **按钮：** 刷新、复制、导出。
8. **Badge：** 无。
9. **Pill：** 按钮继承全局 Secondary 999 圆角；页面无其它圆角。
10. **大面积空白：** 中间列表行使用 `*`；最小高度 `360`。
11. **固定 MaxWidth：** 无 MaxWidth。
12. **视觉层级事实：** 页面没有额外 Surface/Border，显示背景由父级承载。
13. **主要操作：** 查看日志、错误筛选。
14. **次要操作：** 刷新、复制、导出。

### 4.10 NotificationPanel

文件：`src/HD2Mimnt.UI/Controls/NotificationPanel.axaml`

1. **最外层容器：** UserControl，Background `Surface2Brush`，MinWidth `320`、MaxWidth `400`；内部 Border。
2. **Border/Surface 层数：** 外框、标题区、通知条目、通知级别图标、底部操作区。
3. **Card 化结构：** 通知条目使用 `Surface1Brush` 和底边框；面板外框有 CornerRadius `8`。
4. **顶部工具栏：** 标题 `Notifications` 和 24x24 icon-button 关闭按钮。
5. **列表实现：** ScrollViewer + Panel + ItemsControl；未声明自定义 ItemsPanel。
6. **状态表现：** 通知级别用颜色转换器和图标转换器；空状态用 `🔔`、文本和 `Opacity=0.3`；每项有时间和可选动作按钮。
7. **按钮：** 关闭、单条删除、通知动作、Clear All。
8. **Badge：** 面板内部无 Badge；MainWindow 外层有未读 Badge。
9. **Pill：** 级别图标容器 CornerRadius `999`；关闭按钮使用全局 icon-button。
10. **大面积空白：** 空状态 `Margin="0,60,0,0"` 且居中；面板宽度限制 320-400。
11. **固定 MaxWidth：** UserControl `MaxWidth=400`；设计尺寸 `400x600`。
12. **视觉层级事实：** 标题/底部使用 `Surface3Brush`，条目使用 `Surface1Brush`，外框 UserControl/Border 使用 `Surface2Brush`。
13. **主要操作：** 查看通知和执行通知动作。
14. **次要操作：** 关闭面板、删除单条通知、Clear All。

### 4.11 Dialog：PluginPermissionDialog

文件：`src/HD2Mimnt.UI/Views/PluginPermissionDialog.axaml`

1. **最外层容器：** `Window`，固定 `600x700`，Min `500x500`，Max `700x900`，不可调整大小；内部 `Grid RowDefinitions="Auto,*,Auto"`。
2. **Border/Surface 层数：** 标题区、插件信息、警告、权限条目、底部操作区；权限条目内有图标/风险 Badge。
3. **Card 化结构：** 插件信息和每条权限都是 Surface2 + 1px Border + CornerRadius 6；警告是独立 Border。
4. **顶部工具栏：** 标题和说明，不含图标按钮。
5. **列表实现：** ScrollViewer 内 StackPanel，RequestedPermissions 使用 ItemsControl；未声明自定义 ItemsPanel。
6. **状态表现：** 权限模式 999 Badge；高风险使用红色 999 Badge；管理模式和安装请求模式显示不同底部按钮。
7. **按钮：** Deny、Allow Once、Allow Always；管理模式的取消、保存权限。
8. **Badge：** 权限模式和 High Risk 使用局部 Border；不是全局 `Border.badge`。
9. **Pill：** 权限模式、高风险和三个主要请求按钮为 999。
10. **大面积空白：** 内容区 ScrollViewer；权限条目间距 `8`，内容 StackPanel Spacing `20`；窗口尺寸固定。
11. **固定 MaxWidth：** Window `MaxWidth=700`、`MaxHeight=900`；无内容 Text MaxWidth。
12. **视觉层级事实：** 标题/内容/底部使用 Surface2，插件图标使用 Surface3，警告使用固定深色与橙色边框。
13. **主要操作：** Deny、Allow Once、Allow Always；管理模式下保存权限。
14. **次要操作：** 管理模式取消；信息和说明文本。

### 4.12 Popup/Flyout：LibraryView MenuFlyout 与 Overlay

当前没有 `<Popup>` 标签。可见的弹出/覆盖结构如下：

| 文件/区域 | 实现 | 尺寸/样式 | 操作 |
|---|---|---|---|
| `LibraryView.axaml:53-62` | 顶部更多 Button 的 `MenuFlyout` | `Placement=BottomEdgeAlignedRight` | 刷新、清除筛选、清空选择 |
| `LibraryView.axaml:382-395` | 每行更多 Button 的 `MenuFlyout` | `Placement=BottomEdgeAlignedRight`；含 Separator | 启用/禁用、查看详情、删除 |
| `LibraryView.axaml:433-442` | Mod 详情 Overlay | 半透明 `#CC000000`；右侧 `Width=400`；`ZIndex=100` | 详情面板 |
| `LibraryView.axaml:444-453` | 部署 Overlay | `#AA05070C`；`ZIndex=90`；ProgressBar `Width=260` | 部署中状态 |
| `PluginManagementView.axaml:370-397` | 执行历史 Overlay | `#CC05070C`；内层 `680x460`；`ZIndex=20` | 执行历史 |

## 5. Library / Profile 业务表达审计

以下只记录当前文本、位置和控件语义，不判断业务模型是否需要修改。

| 检查项 | 当前 UI 表现 | 事实依据 |
|---|---|---|
| Library 是否表达全局 Mod 仓库 | 页面标题为“模组库”；顶部有添加模组；列表行展示全部/启用/未启用/已部署/未部署筛选；列表操作含删除模组 | `LibraryView.axaml:23`、`:30`、`:71-100`、`:271-401` |
| Library Mod 是否容易被误认为当前启用 | 列表同时显示“已启用”和“已部署”标签；筛选栏显示“已启用”与“已部署”；行本身还有 CheckBox；当前 Profile 名称只在 MainWindow 底部状态栏出现 | `LibraryView.axaml:346-363`；`MainWindow.axaml:172-181` |
| Profile 是否表达独立工作集合 | 页面说明为“管理不同的模组组合与启用状态”；左侧 Profile 列表显示 `ModCount`；右侧标题为“配置中的模组”并显示数量 | `ProfileView.axaml:9-10`、`:43-44`、`:82-99` |
| Profile Mod 是否明确属于当前 Profile | 右侧详情在选择 Profile 后显示 Profile 名、描述和“配置中的模组”；每个 Mod 有启用 CheckBox | `ProfileView.axaml:73-104` |
| 从 Profile 移除 Mod 与全局删除是否区分 | 当前 Profile Mod 行只显示启用 CheckBox，没有“从配置移除”按钮；Library 行的更多菜单有“删除”，批量栏也有“删除” | `ProfileView.axaml:93-101`；`LibraryView.axaml:384-393`、`:419-427` |
| Mod Options 是否明确属于当前 Profile | 当前 Profile 页面展示 Profile 详情和 ProfileMods；Mod 详情由 Library Overlay 打开；Profile 页面没有独立的 Mod Options 区域 | `ProfileView.axaml:73-106`；`LibraryView.axaml:433-442` |
| 同一 Mod 出现在多个 Profile 时的表达 | Profile 列表显示每个 Profile 的 ModCount；详情只显示当前选中的 ProfileMods；未在页面中显示跨 Profile 共享关系或来源说明 | `ProfileView.axaml:24-106` |

## 6. Icon / Emoji 审计

### 6.1 图标控件/字体

- `PathIcon`：未发现。
- `SymbolIcon`：未发现。
- `<Icon>` 控件：未发现。
- 图标字体声明：未发现。
- 自定义 SVG/Path 图标：未发现。
- 图标主要由 `TextBlock.Text` 中的 Emoji、Unicode 符号或 ViewModel 绑定字符串提供。

### 6.2 当前图标清单

| 文件 | 控件 | 当前图标 | 类型 | 用途 |
|---|---|---|---|---|
| `MainWindow.axaml:90` | Task Center Button 内 TextBlock | `⚙` | Unicode symbol | 任务中心入口 |
| `MainWindow.axaml:106` | Notifications Button 内 TextBlock | `🔔` | Emoji | 通知入口 |
| `MainWindow.axaml:121` | Dev Tools Button 内 TextBlock | `🛠` | Emoji | 开发工具入口 |
| `LibraryView.axaml:49` | 顶部更多 Button | `⋮` | Unicode symbol | 更多操作 |
| `LibraryView.axaml:120` | 搜索框 TextBlock | `🔍` | Emoji | 搜索提示 |
| `LibraryView.axaml:134` | 搜索清除 Button | `✕` | Unicode symbol | 清除搜索 |
| `LibraryView.axaml:194` | 空状态 TextBlock | `📦` | Emoji | 无 Mod 占位 |
| `LibraryView.axaml:235` | 错误状态 TextBlock | `⚠` | Unicode symbol | 错误提示 |
| `LibraryView.axaml:260` | 加载状态 TextBlock | `⏳` | Unicode symbol | 加载提示 |
| `LibraryView.axaml:313` | Mod 图标占位 TextBlock | `📦` | Emoji | 无缩略图占位 |
| `LibraryView.axaml:350` | 启用状态文本 | `✓` | Unicode symbol | 状态标签前缀 |
| `LibraryView.axaml:359` | 部署状态文本 | `●` | Unicode symbol | 状态标签前缀 |
| `LibraryView.axaml:377` | 行更多 Button | `⋮` | Unicode symbol | Mod 更多操作 |
| `NotificationPanel.axaml:25,85` | 关闭/删除 Button | `✕` | Unicode symbol | 关闭面板/删除通知 |
| `NotificationPanel.axaml:51` | 通知级别 TextBlock | ViewModel `LevelToIconConverter` 结果 | 绑定 Unicode/图标字符串 | 通知级别 |
| `NotificationPanel.axaml:105` | 空状态 TextBlock | `🔔` | Emoji | 无通知占位 |
| `ModDetailPanel.axaml:47` | 关闭 Button | `✕` | Unicode symbol | 关闭详情面板 |
| `PluginManagementView.axaml:132,314` | 插件图标占位 | `📦` | Emoji | 插件条目/空状态 |
| `PluginManagementView.axaml:153,167` | 插件状态/权限图标 | ViewModel `StatusIcon` / `PermissionModeIcon` | 绑定字符串 | 状态和权限模式 |
| `PluginManagementView.axaml:359` | Loading 状态 | `⏳` | Unicode symbol | 插件加载提示 |
| `PluginPermissionDialog.axaml:53` | 插件图标占位 | `📦` | Emoji | 权限请求中的插件图标 |
| `PluginPermissionDialog.axaml:95` | 权限模式图标 | ViewModel `PermissionModeIcon` | 绑定字符串 | 权限模式 |

## 7. Animation / Transition 审计

| 搜索项 | 当前结果 |
|---|---|
| `Transition` | 未发现 XAML Transition 声明 |
| `Transitions` | 未发现 XAML Transitions 集合 |
| `Animation` | 仅在 `SettingsView.axaml:37` 作为“启用界面动画”设置文案出现；未发现动画对象或时长声明 |
| `Opacity` | 5 处显式设置：NotificationPanel 空状态 `0.3`；LibraryView 空状态 `0.3`、错误状态 `0.5`、加载状态 `0.5`、Mod 图标占位 `0.3` |
| `Scale` / `ScaleTransform` | 未发现 |
| `Translate` / `TranslateTransform` | 未发现 |
| `Transform` | 未发现 |
| 时长 | 未发现动画时长声明 |
| 触发条件 | 未发现动画触发器；上述 Opacity 均为静态值或 IsVisible 状态下的静态显示效果 |

## 8. 页面尺寸 / 空白审计

| 文件 | 区域 | 当前尺寸/对齐 | 结构事实 |
|---|---|---|---|
| `MainWindow.axaml:13-14` | 主窗口 | `1280x800`，最小 `960x600` | 固定默认尺寸和最小尺寸 |
| `MainWindow.axaml:209` | 通知面板内层 | `460x560`，右上对齐，Margin `0,16,24,0` | Overlay 固定尺寸 |
| `MainWindow.axaml:217` | 任务中心内层 | `760x560`，居中 | Overlay 固定尺寸 |
| `LibraryView.axaml:7` | 设计尺寸 | `1200x800` | UserControl 设计参考尺寸 |
| `LibraryView.axaml:249` | 错误说明 | `MaxWidth=400` | 居中错误文本限制 |
| `LibraryView.axaml:441` | 详情面板 | `Width=400`，右对齐 | 详情 Overlay 侧面板 |
| `LibraryView.axaml:448` | 部署进度 | `Width=260` | 居中 ProgressBar |
| `ProfileView.axaml:6` | 页面外边距 | `Margin=24` | 主 Grid 外边距 |
| `ProfileView.axaml:67` | 错误说明 | `MaxWidth=260` | 左侧区域内错误文本限制 |
| `SettingsView.axaml:2` | 设置内容 | `MaxWidth=760`，Margin `24`，Spacing `16` | 宽窗口右侧不承载内容 |
| `MoreView.axaml:3` | 更多内容 | `MaxWidth=860`，Margin `24`，Spacing `16` | 宽窗口右侧不承载内容 |
| `TaskCenterView.axaml:2` | 任务页面 | Margin `24` | 嵌入任务 Overlay |
| `TaskCenterView.axaml:11` | 错误说明 | `MaxWidth=360` | 错误状态文本限制 |
| `BackupView.axaml:2` | 备份页面 | `MinHeight=300` | 列表区域使用 `*` |
| `LogView.axaml:2` | 日志页面 | `MinHeight=360` | 列表区域使用 `*` |
| `PluginManagementView.axaml:6` | 设计尺寸 | `1200x700` | UserControl 设计参考尺寸 |
| `PluginManagementView.axaml:202` | 插件说明 | `MaxWidth=600` | 描述文本限制 |
| `PluginManagementView.axaml:371` | 历史弹层 | `680x460` | 固定弹层尺寸 |
| `PluginPermissionDialog.axaml:10-12` | 权限窗口 | `600x700`，Min `500x500`，Max `700x900` | 不可调整大小 |
| `NotificationPanel.axaml:7,11-12` | 通知控件 | 设计 `400x600`，MinWidth `320`，MaxWidth `400` | 宽度受限 |
| `ModDetailPanel.axaml:6` | 详情控件 | 设计 `400x800` | 被 Library Overlay 以 Width `400` 使用 |
| `DevToolsWindow.axaml:10-11` | 开发工具窗口 | `600x500` | 固定窗口尺寸 |

可能产生宽屏空白的当前布局事实：

- SettingsView 的主 StackPanel `MaxWidth=760`，MoreView 的主 StackPanel `MaxWidth=860`；两者由 ScrollViewer 承载，在更宽窗口中内容不会随窗口继续扩展。
- PluginManagementView 的内容使用设计宽度 `1200`，但插件描述限制 `MaxWidth=600`；统计区四列平均分配可用宽度。
- MainWindow 的通知/任务 Overlay 使用固定宽高，遮罩会覆盖剩余区域。
- ProfileView 的左右区域按 `2*,3*` 分配，页面外层 Margin 为 `24`。

## 9. 视觉问题分类（事实归类）

下列条目只描述当前实现中的可观察结构，不给出新的设计方案。

### A. Card / Container

- `MoreView.axaml:5-90`：多个功能模块均使用同样的 `Surface2Brush` + 1px Border + Padding `16`，插件、备份、日志等子 View 继续在其内部创建容器。
- `ModDetailPanel.axaml:30-235`：外层 Border 内含多个 `Border.section` 分区；`Border.section` 使用 Background `#1A1A1A`、CornerRadius `8`、Padding `16`。
- `PluginManagementView.axaml:24-108`：统计区四个 `stat-card` Border 与操作按钮同时存在于同一统计行。

### B. Corner Radius

- `App.axaml:82,102,132,149`：Primary、Secondary、Icon Button、Badge 的全局圆角均为 `999`。
- `PluginManagementView.axaml:216-290`：插件行操作按钮均使用 `999`；同页插件条目和图标分别使用 `6`、`8`。
- `PluginPermissionDialog.axaml:202-244`：请求操作按钮使用 `999`；信息、警告和权限条目使用 `6`。

### C. Spacing

- `MoreView.axaml:3`：主 StackPanel Spacing `16`；其内部多个 Border 之间由该间距分隔。
- `ProfileView.axaml:23`：左右内容列间距 `16`；列表项底部 Margin `8`。
- `NotificationPanel.axaml:59`：通知内容 StackPanel Spacing `4`，条目 Padding `16,12`。

### D. Typography

- `MainWindow.axaml:34-39`：应用标题 FontSize `18`、FontWeight `SemiBold`。
- `LibraryView.axaml:23-26`、`ProfileView.axaml:9-10`、`SettingsView.axaml:3-4`：页面标题通常 FontSize `24`、FontWeight `SemiBold`。
- `PluginManagementView.axaml:41-69`：统计数值 FontSize `28`、FontWeight `Bold`。
- `NotificationPanel.axaml:60-71`：标题 `14`、正文 `12`、时间 `11`，同一通知项内存在三级字号。

### E. Navigation

- `MainWindow.axaml:47-66`：一级导航是四个 RadioButton，内容为 `Library`、`Profiles`、`Settings`、`More`。
- `MainWindow.axaml:134-165`：页面由隐藏 `TabItem` 承载；TabItem 的 IsVisible 由局部 Style 设置为 False。
- `MainWindow.axaml:17-23`：Ctrl+1 至 Ctrl+4 为导航快捷键，Escape 关闭 Overlay。

### F. Button

- `App.axaml:75-104`：Primary/Secondary 全局 Button 都使用 Padding 和 `CornerRadius=999`。
- `LibraryView.axaml:30-48`：标题栏同时存在 Primary、Secondary 和取消按钮；部署方式 ComboBox 位于按钮组中。
- `PluginManagementView.axaml:77-106`：安装/刷新按钮直接在页面内设置样式，不完全依赖全局 `primary/secondary` 类。

### G. Status

- `MainWindow.axaml:75-82`、`:185-192`：游戏状态和部署状态用 Ellipse + 文本。
- `LibraryView.axaml:346-363`：Mod 启用/部署状态用绿色/蓝色 999 标签。
- `PluginPermissionDialog.axaml:158-168`：高风险状态用红色文字和红色边框 999 标签。

### H. Icon

- `MainWindow.axaml:90,106,121`：任务、通知、开发工具使用 `⚙`、`🔔`、`🛠` TextBlock。
- `LibraryView.axaml:120,194,235,260,313,377`：搜索、空状态、错误、加载、占位和行菜单使用 Emoji/Unicode。
- `PluginManagementView.axaml:132,314,359`：插件占位和加载状态使用 `📦`、`⏳`。

### I. Animation

- `SettingsView.axaml:37` 只有“启用界面动画”设置项；源码未发现 Transition、Animation、Transform 或时长。
- `LibraryView.axaml:197,239,263,317` 和 `NotificationPanel.axaml:108` 使用静态 Opacity 值，不是动画声明。

### J. Layout Width

- `SettingsView.axaml:2`：MaxWidth `760`。
- `MoreView.axaml:3`：MaxWidth `860`。
- `NotificationPanel.axaml:11-12`：MinWidth `320`、MaxWidth `400`。
- `MainWindow.axaml:209,217`：Overlay 内层固定 `460x560`、`760x560`。

### K. Information Density

- `LibraryView.axaml:20-184`：标题、统计筛选、搜索、列表头连续占用四个 Grid 行，列表从第 5 行开始。
- `MoreView.axaml:5-90`：单个纵向页面内连续放置七个功能 Border。
- `PluginManagementView.axaml:111-290`：单个插件条目同时包含图标、名称、状态标签、权限标签、版本、作者、安装时间、描述和最多五个操作按钮。

### L. Localization

- `MainWindow.axaml:47-66` 使用英文导航文字；`PluginManagementView.axaml:14-20,38-106,317-363` 大量使用英文；`NotificationPanel.axaml:20,109,123` 使用英文；`PluginPermissionDialog.axaml:25-30,122,164,182,197-244` 中英文并存。
- `LibraryView`、`ProfileView`、`SettingsView` 主要使用中文；同一应用内存在语言混用。

### M. Profile / Library semantic clarity

- `LibraryView.axaml:71-100` 同时显示全局数量筛选、启用数量和部署数量；`LibraryView.axaml:346-363` 在 Mod 行再次显示启用/部署状态。
- `ProfileView.axaml:73-104` 通过“配置详情”“配置中的模组”和数量显示当前 Profile 集合；Profile Mod 行没有删除/移除文字操作。
- `LibraryView.axaml:384-393` 的“删除”位于全局 Mod 行菜单；Profile 页面没有同名“删除”操作。

## 10. 审计结论

1. 全局视觉基础由 `App.axaml` 的 Light/Dark ThemeDictionaries、DynamicResource 画刷和少量全局控件 Style 组成。
2. 当前没有统一 Card 控件；Card 语义主要由各页面直接创建 Border 实现。
3. 圆角使用范围从未设置/边框直角到 `4/6/8/12/999`，其中按钮、Badge、状态标签和权限标签大量使用 `999`。
4. 当前没有项目自定义 Transition、Animation、Shadow 或图标控件；视觉图标主要由 Emoji/Unicode TextBlock 提供。
5. 页面容器主要使用 Grid、StackPanel、ScrollViewer、Border 和 ListBox/ItemsControl；Overlay 通过 Border/Grid/ZIndex 实现。
6. Library 与 Profile 已有不同的标题、状态和列表结构；当前页面中能观察到全局 Mod 状态、当前 Profile 状态和 Profile 内 Mod 列表，但共享关系没有专门的视觉说明。
7. 本报告只记录现状，不包含 UI 重构、视觉设计或业务逻辑变更。

## 11. 外部参考

- [Avalonia 官方文档：Styles](https://docs.avaloniaui.net/docs/guides/styles-and-resources/styles)
- [Avalonia 官方文档：Resources](https://docs.avaloniaui.net/docs/guides/styles-and-resources/resources)
- [Avalonia 官方文档：Panel](https://docs.avaloniaui.net/docs/reference/controls/panel)
- [Avalonia 官方文档：ListBox](https://docs.avaloniaui.net/docs/reference/controls/listbox)
- [Avalonia 官方文档：Flyouts](https://docs.avaloniaui.net/docs/fundamentals/controls/flyouts)
