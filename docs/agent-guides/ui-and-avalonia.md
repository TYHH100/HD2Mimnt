# UI 与 Avalonia 交互

涉及 Views、ViewModels、Controls、样式、选择、拖放、设置或通知时先读本文件；UI 测试另读 [测试指南](testing-and-lifecycle.md)，Lua 固定分组另读 [部署指南](deployment-and-loaders.md)。

## 易错点与正确做法

- 模组选项的子选项直接由选项头部展开；有/无子选项复用同一头部模板，勾选框固定在最右侧对齐，展开箭头放在其左侧预留槽位。Expander 头部内的图片预览要在 PointerPressed 阻止父按钮捕获，只有 Tapped.Handled 会来不及阻止展开状态切换。回归实际点击标题、箭头、勾选框与图片，并检查深浅主题、最小窗口和长名称下的勾选框坐标。来源：https://github.com/amwx/FluentAvalonia/blob/master/src/FluentAvalonia/Styling/ControlThemes/BasicControls/ExpanderStyles.axaml 、https://docs.avaloniaui.net/docs/concepts/input/routed-events

- 虚拟化模组列表的 ItemsControl 不要使用顶部负 Margin 抵消卡片间距。Avalonia 12.0.3 的 VirtualizingStackPanel 根据有效视口与估算行高定位；负边距会让滚动条归零时的面板视口起点仍大于 0，连续 ScrollIntoView 后滚轮返回顶部可能保留位置误差，裁切分隔符甚至普通卡片。保留非负列表边距，由卡片自身控制间距；跳转只调用 ItemsControl.ScrollIntoView，由虚拟化面板完成实现与布局，不重复 BringIntoView。回归必须实际用滚轮逐步返回顶部，检查首行位置及目标完整可见，并覆盖有/无分隔符；直接设置 Offset=0 无法代替该路径。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Controls/VirtualizingStackPanel.cs

- 配置文件列表右键只打开命中行的菜单，不改变 `SelectedProfile`，不触发自动激活；必须在 PointerPressed 隧道路由拦截右键，冒泡阶段已晚于 ListBoxItem 的默认选择。左键仍按设置切换配置，菜单命令绑定命中行；保留默认配置及使用中配置的删除保护。回归开启自动切换后实际右键删除非当前配置，并检查当前配置与数据库激活状态不变。来源：https://docs.avaloniaui.net/docs/concepts/input/routed-events

- 自动检查冲突在“设置 → 配置文件”默认关闭，保存后生效；合并连续变更，串行扫描、取消过期结果，关闭设置和退出均取消待执行任务。不要在 UI 线程解析索引或格式化大量详情。卡片关系共享去重后的成员集合，悬停最多列 8 个名称，点击仍可查看全部；冲突资源、持有者及定位选择列表保持虚拟化，不能嵌套无高度约束的 ScrollViewer 使所有条目同时创建。性能边界及实测见[自动检查评估](../reports/automatic-conflict-check-performance.md)。来源：https://docs.avaloniaui.net/docs/reference/controls/virtualizingstackpanel

- 配置页“检查冲突”直接扫描正在查看的配置，不自动切换页面；配置卡片按 ModId 回填资源覆盖的黄色缩略图角标，共用冲突页结果。卡片悬停提示用于快速排查，只列出去重后的关联模组名称；点击角标时单目标直接定位，多目标先在浮动面板选择，再清除搜索、展开目标分组并滚动到卡片，以主题强调边框标记。固定加载器可以定位但不能变成批量选中；Escape 优先关选择窗，配置切换、视图卸载和结果失效均清空候选。资源编号、类型、差异和预期生效者留在“冲突”页。启用、选项、顺序、成员或部署方向改变后清除旧提示；卡片重建时回填仍有效的结果。角标位置不能随选项按钮有无变化，警告图标使用 UtilityIcon 与主题 WarningBrush，检查长名称、选项按钮、深浅主题和窄窗口，截图前先排空通知事件再关闭横幅，防止遮挡卡片。来源：https://docs.avaloniaui.net/docs/reference/controls/tooltip 、https://github.com/amwx/FluentAvalonia

- 用户分组保存于 `ProfileSeparators`，固定 Lua 分组的折叠及颜色单独保存于 `Profiles`；颜色用 `#RRGGBB`，空值跟随主题，标题保留主题文字并以色条区分。两种分组折叠只影响显示，不改启用/选项/顺序、不参与部署；分隔符添加、删除、改名、颜色和折叠保存仍通知界面刷新，但通过 `ProfilesChangedEventArgs.AffectsConflictResults=false` 保留冲突结果，不能触发重检或取消正在进行的扫描。反向时 Lua 分隔符隐藏其上方固定成员，固定分隔符仍不可拖动或删除。搜索按完整成员显示匹配项，不改变保存的折叠状态；排序事务保存完整模组顺序及用户分隔符落点，不能按折叠/过滤后的索引改变归属。来源：https://docs.avaloniaui.net/docs/concepts/input/drag-and-drop 、https://docs.avaloniaui.net/docs/guides/styles-and-resources/resources

- 依赖加载器分隔符使用 `ChevronAngle` 表达部署方向：常规部署展开箭头向下（90°），从下到上部署向上（-90°）；不要只改固定图标，否则方向切换后 UI 与列表顺序不一致。来源：https://docs.avaloniaui.net/docs/concepts/input/drag-and-drop

- 收起的用户分组需要保存 `MemberCount` 结束边界，拖入其下方或移动整组时只保留原有成员，组外模组正常显示；仅保存起点会让后续模组自动归组。旧数据的 null 沿用原范围，操作时补齐边界；展开、搜索、重新加载、复制配置和组内移出/删除都要保持边界一致，排序与边界通过同一次服务写入保存。回归覆盖正反部署方向、末尾分组及实际 DragOver/Drop。来源：https://docs.avaloniaui.net/docs/concepts/input/drag-and-drop 、https://www.sqlite.org/lang_altertable.html

- 外部文件导入拖放统一在 MainWindow 冒泡路由处理并启用 handledEventsToo（DragDrop 事件不走隧道路由），先识别本地文件再标记 Handled，避免页面子控件吞掉事件或内部排序覆盖 Copy；根容器使用透明背景覆盖空白区域。提示层不参与命中测试，DragLeave 只有窗口坐标越界才隐藏，不能因子控件之间移动反复显隐；无文件数据继续交给内部排序。来源：https://docs.avaloniaui.net/docs/concepts/input/routed-events

- 弹出通知的计时器随视图挂载/卸载启动和停止，横幅收起不能删除通知历史；后台通知事件先派发到 UI 线程。Avalonia 位移动画使用已创建的 `TranslateTransform` 和 `TranslateTransform.X`，不要直接对 `RenderTransform` 写关键帧，否则可能编译通过但运行时报无 Animator；必须用 Headless 实际显示通知验证。来源：https://docs.avaloniaui.net/docs/guides/graphics-and-animation/keyframe-animations

- 顶部导航使用 `FANavigationViewItem.Tag` 作为稳定页面编号，选择事件和反向高亮按 Tag 查找，不能将 MenuItems 的显示下标直接当作 SelectedTabIndex。插入导航时核对快捷命令和隐藏 TabControl 页面编号；教程恢复、导航与语言测试也按 Tag 找条目。现有页面 0–4 保留，“浏览模组”为 5、Ctrl+6，显示在模组库右侧。来源：https://github.com/amwx/FluentAvalonia

- UI 只通过 Application 服务访问模组、配置文件和部署数据；不要在 View 或 ViewModel 中直接访问 SQLite、部署实现或复制文件。

- Avalonia 的 `ObservableCollection` 和控件状态只能在 UI 上下文更新。后台筛选/排序先计算普通 `List`，回到 UI 上下文后再替换集合。

- 参考旧版 WPF 前端时只迁移可验证的业务行为语义（例如筛选列表中的范围选择），必须继续通过当前 UI 的 Application 服务和 ViewModel 对接；不要直接复制旧 UI 或绕过当前 Infrastructure/数据库边界。

- Avalonia `CheckBox` 如果绑定了“切换状态”的命令，`IsChecked` 应使用 `Mode=OneWay`，由命令完成持久化并更新 ViewModel；双向绑定会先写一次状态、命令再反转一次，造成 UI 与 Profile 数据库相反。

- 设置页保存的运行时前置条件必须同步到对应 Application 服务；例如游戏路径不能只写 `SettingsService`，还要通过 `GameDetectionService` 校验并更新当前状态，否则部署页仍会读取旧检测结果。

- 后台服务事件（游戏检测、通知、任务完成等）不能直接修改 Avalonia 绑定属性；事件处理器必须检查 UI 线程并通过 Dispatcher 派发，测试无 UI 上下文时才允许同步回退。

- 设置页的“自动识别游戏”必须同时约束启动流程和保存后的检测流程；关闭时只能应用用户配置路径，不能仍然调用会扫描常见目录的 `AutoDetectAsync`。

- UI 统一使用 FluentAvaloniaTheme，不能再叠加 Avalonia FluentTheme。Avalonia 12.0.3 对应 FluentAvaloniaUI 3.0.2；3.1.0 要求 Avalonia 12.1.0，升级时必须同步 UI 和 Headless 版本。来源：https://www.nuget.org/packages/FluentAvaloniaUI/3.0.2

- 页面色彩、圆角和间距集中到 `Styles/AppResources.axaml`、`Styles/AppStyles.axaml`；主要按钮使用组件库的 `accent` 类，图标使用 `UtilityIcon`（FluentAvalonia FASymbolIcon）。不要复制页面调色板、文字/emoji 图标或基础控件状态模板。来源：https://github.com/amwx/FluentAvalonia

- 运行时背景透明度只更新 `CardSurfaceOpacity`、`ChromeSurfaceOpacity`、`ContentSurfaceOpacity`，不能用新画刷覆盖动态资源，否则跟随系统切换主题时颜色会失联。

- 模态弹窗即使复用 `card` 样式，也必须显式使用不透明的主题背景（如 `{DynamicResource Surface2Brush}`），避免受卡片透明度设置影响而透出底层文字；验证低透明度及深浅主题切换。来源：https://docs.avaloniaui.net/docs/guides/styles-and-resources/resources

- 图片占位图标的可见性使用 `ObjectConverters.IsNull` 判断图片对象；不要将 `!Icon` 与 `NotNullConverter` 组合，布尔结果仍然非空，会导致占位图标覆盖已加载图片。来源：https://docs.avaloniaui.net/docs/reference/built-in-data-binding-converters

- 拖放排序预览必须与 Drop 共用插入位置计算，覆盖层设置 IsHitTestVisible=False 且不参与列表布局；DragLeave 可能从子控件冒泡，只有指针真正离开列表视口才隐藏预览，避免鼠标跨过文字/图片时闪烁。取消、完成、DataContext 变化和视图卸载都要恢复拖动源透明度。来源：https://docs.avaloniaui.net/docs/concepts/input/routed-events

- 配置页内部排序使用页面指针捕获，避免系统拖放循环接管输入后滚轮失效；捕获后滚轮要明确滚动正在排序的列表，并在虚拟化布局完成后重算预览。滚动回收容器时重新按当前数据应用源透明度，不能让复用的其他卡片变淡。松开沿用同一 DragOver/Drop 落点校验，Escape、捕获丢失和卸载均释放捕获与数据。回归通过实际 MouseDown/MouseMove/MouseWheel/MouseUp 验证两类列表、双向滚动及保存/取消，Headless 移动和滚轮事件须显式传入 LeftMouseButton。来源：https://docs.avaloniaui.net/docs/concepts/input/pointer 、https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Base/Input/DragDrop.cs

- 枚举类型的 RelayCommand 必须用 `{x:Static vm:枚举类型.成员}` 传递 CommandParameter，不能传同名字符串，否则 CanExecute 会禁用按钮；命令驱动的筛选 RadioButton 选中状态使用 OneWay，避免转换器反写枚举属性。来源：https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/relaycommand

- 模组库普通单击只聚焦是明确的产品设计，不要改成单击进入批量选择；但必须记录范围锚点。Shift 替换范围且连续操作保留锚点，Ctrl+Shift 追加范围，Ctrl 增减选择并更新锚点；Ctrl+A 始终全选，与工具栏的全选切换按钮区分。交互按钮和文本输入不能触发卡片选择，验证须覆盖实际鼠标/键盘事件。来源：https://docs.avaloniaui.net/docs/reference/controls/listbox

- 配置页部署必须显式传入正在查看的配置 ID，并读取已保存的 `Deployment.DefaultMethod`；新增 ViewModel 依赖时同步修改 `App.ConfigureServices` 中的手动构造工厂，不能只改构造函数。回归测试要通过实际 DI 和按钮绑定验证，避免手动构造掩盖漏接依赖。

- 配置页模组卡片的顺序数字使用 1-based 显示；右键“移动到指定位置”应保留当前多选集，输入位置后复用 `CanMoveMods`/`MoveModsAsync`，继续校验 Lua 依赖与加载器的固定区域，不能直接改集合绕过持久化。

- 数字定位转换落点时，正向列表普通模组区域末尾要映射为 `Guid.Empty` 分隔符锚点，不能用第一项 Lua 模组 ID，否则合法的区域内移动被误拒绝；反向列表末尾保留 null 锚点。回归覆盖含 Lua 分组的单选、多选、正反向和越界拒绝。

- Lua 分隔符在两种方向下都位于普通模组与固定区域之间：正向为普通 → 分隔符 → Lua 依赖 / 加载器 / SmoothBoot，反向为 SmoothBoot / 加载器 / Lua 依赖 → 分隔符 → 普通，不能将反向分隔符索引写死为 0。显示、拖放校验、实际移动和数字定位共用分组边界；反向依赖区域末尾使用 `Guid.Empty`，普通区域末尾使用 null。搜索后按可见成员计算分隔符位置。来源：https://docs.avaloniaui.net/docs/concepts/input/drag-and-drop

- 配置页普通模组和 Lua 依赖模组支持常规多选；按用户要求，BSL、MDL、SmoothBoot 和分隔符不可选中。点击、范围选择、Ctrl+A 共用 `CanSelect`，范围选择和全选必须跳过固定卡片；交互控件保留自身输入行为，跨分组的多选集仍禁止排序。来源：https://docs.avaloniaui.net/docs/reference/controls/listbox

- 当前 FluentAvaloniaTheme 下右键菜单使用 `FAMenuFlyout` / `FAMenuFlyoutItem`；原生 `ContextMenu` 的 IsOpen 为 true 不代表菜单可见，必须实际验证条目布局和鼠标点击。模组列表统一捕获右键冒泡事件并启用 handledEventsToo，兼容子控件吞掉事件。顺序号使用版本号前的独立 chip，不能依赖缩略图占位文字。来源：https://github.com/amwx/FluentAvalonia

- 教程等跨页面聚焦覆盖层应通过真实控件坐标定位，等待页面布局后再高亮，并在缩放时重新定位；覆盖层阻止底层鼠标、快捷导航和外部文件导入，退出后恢复页面与分类。模糊效果的视觉验收使用 Headless 的 CaptureRenderedFrame（实际合成渲染）；RenderTargetBitmap.Render 的静态截图不能代替 Effect 的实际显示验证。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Headless/Avalonia.Headless/HeadlessWindowExtensions.cs

- 所有ui基本上都要采用这个FluentAvalonia组件库保证视觉统一性

- 配置页多选快捷键需接入 MainWindowViewModel.CloseOverlays 的 Escape 入口：窗口 KeyBinding 会先处理按键，不能只在卡片列表挂 Escape 事件。覆盖层优先关闭，普通配置页才清空选择；Ctrl+A 避开文本框。配置页和模组库共用 `ModSelection<T>`：普通单击清空批量选择，但必须以 IsFocused 保留 FluentAvalonia 原生蓝色竖条，不能让“只聚焦”变成没有视觉反馈；Ctrl 切换、Shift 替换范围、Ctrl+Shift 追加范围并清除单击标记，取消选择的卡片不能因仍是键盘焦点而保留蓝条。工具栏只跟随批量 IsSelected 数量。配置页按下已选卡片时暂留整组选中供拖动，松开且未拖动才切换为单击标记；单卡拖动不需要设置 IsSelected。全选切换与反选只作用于可见 CanSelect 成员，Escape/空白清空批量选择、单击标记和范围锚点。回归同时验证两页实际鼠标输入、蓝条、工具栏可见性与容器 IsSelected。来源：https://docs.avaloniaui.net/docs/concepts/input/hotkeys

- ViewModel 自行管理批量选择时，不能把 FocusedMod 绑定到原生 ListBox.SelectedItem，也不能让原生列表在筛选时清空持久的选中集合。模组库与配置页使用 ItemsControl + 共享 ModCardListBoxItemTheme（继承 FluentAvalonia 原生 ListBoxItem），容器 IsSelected 单向绑定 IsHighlighted（IsSelected || IsFocused），批量操作仅统计模型 IsSelected；选项弹窗继续使用 ViewModelSelectableListBoxItemTheme。鼠标/键盘在原生输入前处理并保留可聚焦的列表。回归必须检查批量选中集合、单击标记和容器 IsSelected，并覆盖搜索后容器重建；只断言 SelectedCount 无法发现视觉失效。ControlTheme 放入 ResourceDictionary，通用属性使用 ReflectionBinding 避免绑定到单一 ViewModel 类型。来源：https://docs.avaloniaui.net/docs/basics/user-interface/styling/control-themes

- 右键菜单在 PointerReleased / ContextRequested 阶段打开，不要在 PointerPressed 提前弹出后又在 ContextRequested 关闭重建；列表的 handledEventsToo 松开事件只作兜底，共享 AppContextMenu 对当前已打开菜单去重。回归检查同次手势仅赋值和打开一次、未发生关闭，并实际点击菜单项。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Controls/Control.cs
