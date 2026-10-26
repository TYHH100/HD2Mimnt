# UI 与 Avalonia 交互

涉及 Views、ViewModels、Controls、样式、选择、拖放、设置或通知时先读本文件；UI 测试另读 [测试指南](testing-and-lifecycle.md)，Lua 固定分组另读 [部署指南](deployment-and-loaders.md)。

## 易错点与正确做法

- 模组库、配置页及详情面板点击卡片图片放大时，通过 Application 的 `ModService.ReadOriginalPreviewAsync` 读取清单指定图片或根目录图片，再完整解码；不能把卡片的 `IconBitmap` / `Icon` 或生成的 256px 缩略图直接交给放大层。原图由共享预览持有，关闭时释放；加载期间关闭或打开其它图片后，旧加载结果不能重新打开覆盖层。回归同时断言缩略图为 256px、放大图保持原始尺寸。来源：[Avalonia Bitmap](https://docs.avaloniaui.net/docs/reference/graphics/bitmaps)。

- 配置页本地启停、选项和排序操作的 `ProfilesChanged` 经过异步刷新队列时，必须在事件发布时捕获 `IsApplyingLocalChange`，队列执行时按捕获值跳过当前页重复重载；不能读取已在服务返回后复原的抑制标记。自动冲突检查在最后一次变更后等待 1500 ms 合并，过期任务取消并串行清理；后台范围包含配置读取、加载器识别和资源扫描准备，不能仅把最终资源解析放进 `Task.Run`。回归检查连续开关、选项保存、排序后卡片实例保留与只完成一次扫描。来源：[异步场景与 Task.Run](https://learn.microsoft.com/dotnet/csharp/asynchronous-programming/async-scenarios)。

- 配置页右键“添加到其他配置文件”复用模组库的 `AddToProfileOverlay`、选择状态和批量添加命令，排除源配置且不预选其它配置；不切换页面、不移除源配置成员。加载器锁定不限制此入口，Escape 和背景点击共用取消流程。来源：[Avalonia UserControl](https://docs.avaloniaui.net/docs/reference/controls/usercontrol)。

- `ProfileService.GetProfileByIdAsync` 是只读查询；需要加载器重排与分组边界修正时显式使用 `NormalizeProfileAsync`。完整配置的创建、改名、排序、激活、复制、成员与选项写入共用 `_mutationGate`，不能让未加闸门的旧快照覆盖并发修改。配置页面加载执行显式规范化，比较及其它查询保持只读。来源：[命令与查询职责](https://martinfowler.com/bliki/CommandQuerySeparation.html)。

- 配置变化事件刷新列表信息时，当前配置内容未变就保留已有模组卡片；修改其它配置或激活状态不能使当前右键菜单、详情和选择持有的卡片失效。内容变化仍须重载；模组元数据变化走完整刷新。回归见 `ProfileViewModelTests.ProfileRefresh_ShouldKeepCurrentCardsWhenOnlyAnotherProfileChanges` 和跨配置删除 UI 测试。

- 设置分类合并为“配置文件（含部署）”“模组库（含搜索）”“存储位置（含缓存）”；保留各分区原有 `settings.*` 稳定 ID，入口自定义仍按分区隐藏，只有组内全部隐藏才移除标签。旧分类跳转映射到所属标签；同标签的保存与恢复默认覆盖组内设置，不重置其它分类，存储路径恢复默认仍只回填、保存后才生效。更新分类列表后同时刷新分区可见性并恢复有效选择。来源：[Avalonia ListBox](https://docs.avaloniaui.net/docs/reference/controls/listbox)。

- 设置保存按当前标签构造一次事务，不验证或写入其它标签的草稿；合并标签的保存/重置都必须整体成功，失败不发布部分设置或运行时事件。外观恢复默认按明确键列表处理，不能清空含 `UI.HiddenEntries` 的整个 UI 分类，更不能调用全局重置。背景选图与清空复用同一流程，保存成功后才改变已保存的背景；异步入口捕获失败并允许重试。参考：[SQLite 事务](https://www.sqlite.org/lang_transaction.html)、[设置持久化检查记录](../reports/settings-persistence-review-2026-10-20.md)。

- 新增兼容性设置不能只接浮窗：同时接入设置分类、稳定的 `settings.compatibility` 入口 ID、持久化、分类重置、三语与实际执行开关。启动扫描和批量修复默认关闭；扫描不阻塞初始化、不自动修复，关闭和退出等待取消清理。游戏数据构建通过 Application 门面执行，进度回调核对操作令牌，避免已结束任务更新后续任务。行为与验证见[兼容性说明](../features/MOD_COMPATIBILITY.md)。来源：[MVVM 异步命令](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/asyncrelaycommand)。

- 模组创建的源目录自动填充须经过 Application 后台扫描，按目录分别取名称、直属第一张图片和 Include；空草稿才生成选项，已有清单和手动填写值不被扫描覆盖。自动生成选项时保留根目录补丁的 `.` 映射。Include 选择使用程序内目录勾选树，独立保存对话框选择，确认后替换路径、取消不写回；回归粘贴路径、勾选回显、取消、清空及保存后的实际清单。来源：[旧版创建逻辑](https://github.com/TYHH100/Helldivers2ModManager/blob/test/src/Helldivers2ModManager/ViewModels/CreatePageViewModel.cs)、[Avalonia TreeView](https://docs.avaloniaui.net/docs/reference/controls/treeview)。

- 配置页模组的详情、文件位置、来源及作者入口不能使用 `CanSelect` / `CanDrag` 作总开关；加载器锁定只限制选择与排序。右键按操作分别判断权限，双击详情先于拖动权限判断，忽略按钮/开关及 Ctrl/Shift 手势；详情中的启用和移除作用于正在查看的配置，Escape 优先关闭详情。“删除模组文件”与模组库共用 `ModService.DeleteModAsync` 和删除确认，右键与详情共用异步命令流程；删除后等待模组库列表和搜索索引刷新完成，不能只更新配置成员或留下未等待的刷新任务。来源：https://docs.avaloniaui.net/docs/concepts/input/pointer 、https://docs.avaloniaui.net/docs/concepts/input/routed-events 、https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/relaycommand

- 模组选项的子选项直接由选项头部展开；有/无子选项复用同一头部模板，勾选框固定在最右侧对齐，展开箭头放在其左侧预留槽位。Expander 头部内的图片预览要在 PointerPressed 阻止父按钮捕获，只有 Tapped.Handled 会来不及阻止展开状态切换。回归实际点击标题、箭头、勾选框与图片，并检查深浅主题、最小窗口和长名称下的勾选框坐标。来源：https://github.com/amwx/FluentAvalonia/blob/master/src/FluentAvalonia/Styling/ControlThemes/BasicControls/ExpanderStyles.axaml 、https://docs.avaloniaui.net/docs/concepts/input/routed-events

- 虚拟化模组列表的 ItemsControl 不要使用顶部负 Margin 抵消卡片间距。Avalonia 12.0.3 的 VirtualizingStackPanel 根据有效视口与估算行高定位；负边距会让滚动条归零时的面板视口起点仍大于 0，连续 ScrollIntoView 后滚轮返回顶部可能保留位置误差，裁切分隔符甚至普通卡片。保留非负列表边距，由卡片自身控制间距；跳转只调用 ItemsControl.ScrollIntoView，由虚拟化面板完成实现与布局，不重复 BringIntoView。回归必须实际用滚轮逐步返回顶部，检查首行位置及目标完整可见，并覆盖有/无分隔符；直接设置 Offset=0 无法代替该路径。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Controls/VirtualizingStackPanel.cs

- 配置文件列表右键只打开命中行的菜单，不改变 `SelectedProfile`，不触发自动激活；必须在 PointerPressed 隧道路由拦截右键，冒泡阶段已晚于 ListBoxItem 的默认选择。左键仍按设置切换配置，菜单命令绑定命中行；保留默认配置及使用中配置的删除保护。回归开启自动切换后实际右键删除非当前配置，并检查当前配置与数据库激活状态不变。来源：https://docs.avaloniaui.net/docs/concepts/input/routed-events

- 自动检查冲突在“设置 → 配置文件”默认关闭，保存后生效；合并连续变更，串行扫描、取消过期结果，关闭设置和退出均取消待执行任务。不要在 UI 线程解析索引或格式化大量详情。卡片关系共享去重后的成员集合，悬停最多列 8 个名称，点击仍可查看全部；冲突资源、持有者及定位选择列表保持虚拟化，不能嵌套无高度约束的 ScrollViewer 使所有条目同时创建。性能边界及实测见[自动检查评估](../reports/automatic-conflict-check-performance.md)。来源：https://docs.avaloniaui.net/docs/reference/controls/virtualizingstackpanel

- 配置页“检查冲突”直接扫描正在查看的配置，不自动切换页面；配置卡片按 ModId 回填资源覆盖的黄色缩略图角标，共用冲突页结果。卡片悬停提示用于快速排查，只列出去重后的关联模组名称；点击角标时单目标直接定位，多目标先在浮动面板选择，再清除搜索、展开目标分组并滚动到卡片，以主题强调边框标记。固定加载器可以定位但不能变成批量选中；Escape 优先关选择窗，配置切换、视图卸载和结果失效均清空候选。资源编号、类型、差异和预期生效者留在“冲突”页。启用、选项、顺序、成员或部署方向改变后清除旧提示；卡片重建时回填仍有效的结果。角标位置不能随选项按钮有无变化，警告图标使用 UtilityIcon 与主题 WarningBrush，检查长名称、选项按钮、深浅主题和窄窗口，截图前先排空通知事件再关闭横幅，防止遮挡卡片。来源：https://docs.avaloniaui.net/docs/reference/controls/tooltip 、https://github.com/amwx/FluentAvalonia

- 用户分组保存于 `ProfileSeparators`，固定 Lua 分组的折叠及颜色单独保存于 `Profiles`；颜色用 `#RRGGBB`，空值跟随主题，标题保留主题文字并以色条区分。两种分组折叠只影响显示，不改启用/选项/顺序、不参与部署；分隔符添加、删除、改名、颜色和折叠保存仍通知界面刷新，但通过 `ProfilesChangedEventArgs.AffectsConflictResults=false` 保留冲突结果，不能触发重检或取消正在进行的扫描。反向时 Lua 分隔符隐藏其上方固定成员，固定分隔符仍不可拖动或删除。搜索按完整成员显示匹配项，不改变保存的折叠状态；排序事务保存完整模组顺序及用户分隔符落点，不能按折叠/过滤后的索引改变归属。来源：https://docs.avaloniaui.net/docs/concepts/input/drag-and-drop 、https://docs.avaloniaui.net/docs/guides/styles-and-resources/resources

- 依赖加载器分隔符使用 `ChevronAngle` 表达部署方向：常规部署展开箭头向下（90°），从下到上部署向上（-90°）；不要只改固定图标，否则方向切换后 UI 与列表顺序不一致。来源：https://docs.avaloniaui.net/docs/concepts/input/drag-and-drop

- 收起的用户分组需要保存 `MemberCount` 结束边界，拖入其下方或移动整组时只保留原有成员，组外模组正常显示；仅保存起点会让后续模组自动归组。旧数据的 null 沿用原范围，操作时补齐边界；展开、搜索、重新加载、复制配置和组内移出/删除都要保持边界一致，排序与边界通过同一次服务写入保存。回归覆盖正反部署方向、末尾分组及实际 DragOver/Drop。来源：https://docs.avaloniaui.net/docs/concepts/input/drag-and-drop 、https://www.sqlite.org/lang_altertable.html

- 删除模组文件不能只依赖 `Mods → ProfileMods` 的外键级联：级联不会维护 `ProfileSeparators.Order/MemberCount`，应先由 `ModService` 调用共享 `ProfileService` 移除所有配置中的引用，再删除模组记录。单个、批量移除及删除文件均按移除前的完整成员换算分组边界，删空的组不能吸收下一组；验证即时显示、重载及其它配置。重新开启加载器锁定时，必须在固定成员重新排序之前按原完整顺序扣除分隔符起点之前及成员范围内的固定成员；展开组的 null 边界与收起组的明确边界均须覆盖，重复重载不能再次偏移。来源：[SQLite 外键级联](https://www.sqlite.org/foreignkeys.html#fk_actions)、[SQLite 公共领域许可声明](https://www.sqlite.org/copyright.html)；回归见 `ModRemoval_ShouldPreserveSeparatorMembersAndEmptyGroupsAfterReload`、`RelockingLoaders_ShouldPreserveOrdinarySeparatorMembers`。

- 外部文件导入拖放统一在 MainWindow 冒泡路由处理并启用 handledEventsToo（DragDrop 事件不走隧道路由），先识别本地文件再标记 Handled，避免页面子控件吞掉事件或内部排序覆盖 Copy；根容器使用透明背景覆盖空白区域。提示层不参与命中测试，DragLeave 只有窗口坐标越界才隐藏，不能因子控件之间移动反复显隐；无文件数据继续交给内部排序。来源：https://docs.avaloniaui.net/docs/concepts/input/routed-events

- 弹出通知的计时器随视图挂载/卸载启动和停止，横幅收起不能删除通知历史；后台通知事件先派发到 UI 线程。Avalonia 位移动画使用已创建的 `TranslateTransform` 和 `TranslateTransform.X`，不要直接对 `RenderTransform` 写关键帧，否则可能编译通过但运行时报无 Animator；必须用 Headless 实际显示通知验证。来源：https://docs.avaloniaui.net/docs/guides/graphics-and-animation/keyframe-animations

- 顶部导航使用 `FANavigationViewItem.Tag` 作为稳定页面编号，选择事件和反向高亮按 Tag 查找，不能将 MenuItems 的显示下标直接当作 SelectedTabIndex。插入导航时核对快捷命令和隐藏 TabControl 页面编号；教程恢复、导航与语言测试也按 Tag 找条目。现有页面 0–4 保留，“浏览模组”为 5、Ctrl+6，“模组创建”为 6、Ctrl+7，显示在模组库和浏览模组之后。数字快捷键的 XAML Gesture 必须写 Ctrl+D1 等枚举名，不能写 Ctrl+1：当前 KeyGesture 会将纯数字按枚举整数解析（例如 7 变成 Pause）。隐藏主页面标签的样式限定为 TabControl#PageHost > TabItem，不能用宽泛 TabItem 选择器，否则页面内部的编辑标签也会被隐藏；教程打开时还需在导航 RelayCommand 的 CanExecute 阻止快捷导航：窗口 KeyBinding 可能早于 KeyDown 隧道处理，单靠事件 Handled 不足以拦截。教程自身通过内部导航方法切换页面。回归使用真实按键和内部标签点击。来源：https://github.com/amwx/FluentAvalonia 、https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Base/Input/KeyGesture.cs 、https://docs.avaloniaui.net/docs/reference/styles/style-selector-syntax

- “设置 → 入口自定义”将隐藏项保存为 `UI.HiddenEntries` 中的稳定英文 ID，显示标签继续本地化；保存独立于游戏路径验证，恢复默认只恢复入口显示。顶部“设置”和设置分类“入口自定义”始终保留，隐藏仅影响入口显示，不停用功能或改变导航 Tag。设置分类刷新 ItemsSource 时先保存当前分类，刷新后恢复仍可见的分类，否则回到入口自定义，避免 ListBox 清空 SelectedItem 导致空白页。来源：https://docs.avaloniaui.net/docs/reference/controls/listbox

- UI 只通过 Application 服务访问模组、配置文件和部署数据；不要在 View 或 ViewModel 中直接访问 SQLite、部署实现或复制文件。

- 导出完整选项截图时复用管理器的 `ModOptionsSelector`，在独立控件中按无限高度测量并展开全部子项，不能截取 ScrollViewer 视口或修改屏幕预览的折叠状态。渲染前关闭 `Expander.ContentTransition` 与控件 `Transitions`，避免把半透明渐显中间帧写入 PNG；验证末项图片完整且不透明。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Controls/Expander.cs

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

- 模组缩略图不能只加载列表前 50/80 项，除非同时实现滚动和筛选后的补加载；当前按完整数据源快照异步加载，配置切换时取消旧队列，库刷新时停止旧数据源队列。文件暂不可读或解码失败不能永久缓存 null，否则刷新仍然缺图；`IconBitmap` 等派生绑定属性必须随 `Icon` 通知。回归覆盖超过原限制的列表、加载时无搜索结果，以及文件独占占用后恢复读取。来源：https://learn.microsoft.com/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getoradd 、https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/observableproperty

- 模组创建页排序必须移动原有选项对象，并通知草稿变更和 JSON 预览；只改界面顺序会让保存和 ZIP 导出失序，重建对象会丢失绑定在对象上的扩展字段。选项行可拖动，子选项仅从手柄启动且限制在所属选项内，输入框保留文本选择。预览和松开共用移除源项前的插入槽位，向下移动只扣减一次源项索引；原位、Escape、越界、捕获丢失及页面卸载均不提交，并清理透明度和插入线。回归实际拖动两级列表、双向滚轮和保存／ZIP 内清单顺序。来源：https://docs.avaloniaui.net/docs/concepts/input/pointer 、https://learn.microsoft.com/dotnet/api/system.collections.objectmodel.observablecollection-1.move

- 拖放排序预览必须与 Drop 共用插入位置计算，覆盖层设置 IsHitTestVisible=False 且不参与列表布局；DragLeave 可能从子控件冒泡，只有指针真正离开列表视口才隐藏预览，避免鼠标跨过文字/图片时闪烁。取消、完成、DataContext 变化和视图卸载都要恢复拖动源透明度。“跳过拖动源”的分支只能在落点正好压在源卡片上时生效，否则列表末尾的落点会被拉回源卡片下方（暗潮加载顺序实测）。来源：https://docs.avaloniaui.net/docs/concepts/input/routed-events

- 配置页内部排序使用页面指针捕获，避免系统拖放循环接管输入后滚轮失效；捕获后滚轮要明确滚动正在排序的列表，并在虚拟化布局完成后重算预览。滚动回收容器时重新按当前数据应用源透明度，不能让复用的其他卡片变淡。松开沿用同一 DragOver/Drop 落点校验，Escape、捕获丢失和卸载均释放捕获与数据。回归通过实际 MouseDown/MouseMove/MouseWheel/MouseUp 验证两类列表、双向滚动及保存/取消，Headless 移动和滚轮事件须显式传入 LeftMouseButton。来源：https://docs.avaloniaui.net/docs/concepts/input/pointer 、https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Base/Input/DragDrop.cs

- 枚举类型的 RelayCommand 必须用 `{x:Static vm:枚举类型.成员}` 传递 CommandParameter，不能传同名字符串，否则 CanExecute 会禁用按钮；命令驱动的筛选 RadioButton 选中状态使用 OneWay，避免转换器反写枚举属性。来源：https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/relaycommand

- 模组库普通单击只聚焦是明确的产品设计，不要改成单击进入批量选择；但必须记录范围锚点。Shift 替换范围且连续操作保留锚点，Ctrl+Shift 追加范围，Ctrl 增减选择并更新锚点；Ctrl+A 始终全选，与工具栏的全选切换按钮区分。交互按钮和文本输入不能触发卡片选择，验证须覆盖实际鼠标/键盘事件。来源：https://docs.avaloniaui.net/docs/reference/controls/listbox

- 配置页部署必须显式传入正在查看的配置 ID，并读取已保存的 `Deployment.DefaultMethod`；新增 ViewModel 依赖时同步修改 `App.ConfigureServices` 中的手动构造工厂，不能只改构造函数。回归测试要通过实际 DI 和按钮绑定验证，避免手动构造掩盖漏接依赖。

- 配置页模组卡片的顺序数字使用 1-based 显示；右键“移动到指定位置”应保留当前多选集，输入位置后复用 `CanMoveMods`/`MoveModsAsync`，继续校验 Lua 依赖与加载器的固定区域，不能直接改集合绕过持久化。

- 数字定位转换落点时，正向列表普通模组区域末尾要映射为 `Guid.Empty` 分隔符锚点，不能用第一项 Lua 模组 ID，否则合法的区域内移动被误拒绝；反向列表末尾保留 null 锚点。回归覆盖含 Lua 分组的单选、多选、正反向和越界拒绝。

- Lua 分隔符在两种方向下都位于普通模组与固定区域之间：正向为普通 → 分隔符 → Lua 依赖 / 加载器 / SmoothBoot，反向为 SmoothBoot / 加载器 / Lua 依赖 → 分隔符 → 普通，不能将反向分隔符索引写死为 0。显示、拖放校验、实际移动和数字定位共用分组边界；反向依赖区域末尾使用 `Guid.Empty`，普通区域末尾使用 null。固定区域为空时系统分隔符隐藏，两个方向下真实列表末尾的 null 锚点均须允许普通模组排序，不能仍按跨越固定分组拒绝。搜索后按可见成员计算分隔符位置。来源：https://docs.avaloniaui.net/docs/concepts/input/drag-and-drop

- 配置页普通模组和 Lua 依赖模组支持常规多选；按用户要求，BSL、MDL、BML、SmoothBoot 和分隔符不可选中。点击、范围选择、Ctrl+A 共用 `CanSelect`，范围选择和全选必须跳过固定卡片；交互控件保留自身输入行为，跨分组的多选集仍禁止排序。来源：https://docs.avaloniaui.net/docs/reference/controls/listbox

- 当前 FluentAvaloniaTheme 下右键菜单使用 `FAMenuFlyout` / `FAMenuFlyoutItem`；原生 `ContextMenu` 的 IsOpen 为 true 不代表菜单可见，必须实际验证条目布局和鼠标点击。模组列表统一捕获右键冒泡事件并启用 handledEventsToo，兼容子控件吞掉事件。顺序号使用版本号前的独立 chip，不能依赖缩略图占位文字。来源：https://github.com/amwx/FluentAvalonia

- 教程等跨页面聚焦覆盖层应通过真实控件坐标定位，等待页面布局后再高亮，并在缩放时重新定位；覆盖层阻止底层鼠标、快捷导航和外部文件导入，退出后恢复页面与分类。模糊效果的视觉验收使用 Headless 的 CaptureRenderedFrame（实际合成渲染）；RenderTargetBitmap.Render 的静态截图不能代替 Effect 的实际显示验证。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Headless/Avalonia.Headless/HeadlessWindowExtensions.cs

- 所有ui基本上都要采用这个FluentAvalonia组件库保证视觉统一性

- 配置页多选快捷键需接入 MainWindowViewModel.CloseOverlays 的 Escape 入口：窗口 KeyBinding 会先处理按键，不能只在卡片列表挂 Escape 事件。覆盖层优先关闭，普通配置页才清空选择；Ctrl+A 避开文本框。配置页和模组库共用 `ModSelection<T>`：普通单击清空批量选择，但必须以 IsFocused 保留 FluentAvalonia 原生蓝色竖条，不能让“只聚焦”变成没有视觉反馈；Ctrl 切换、Shift 替换范围、Ctrl+Shift 追加范围并清除单击标记，取消选择的卡片不能因仍是键盘焦点而保留蓝条。工具栏只跟随批量 IsSelected 数量。配置页按下已选卡片时暂留整组选中供拖动，松开且未拖动才切换为单击标记；单卡拖动不需要设置 IsSelected。全选切换与反选只作用于可见 CanSelect 成员，Escape/空白清空批量选择、单击标记和范围锚点。回归同时验证两页实际鼠标输入、蓝条、工具栏可见性与容器 IsSelected。来源：https://docs.avaloniaui.net/docs/concepts/input/hotkeys

- ViewModel 自行管理批量选择时，不能把 FocusedMod 绑定到原生 ListBox.SelectedItem，也不能让原生列表在筛选时清空持久的选中集合。模组库与配置页使用 ItemsControl + 共享 ModCardListBoxItemTheme（继承 FluentAvalonia 原生 ListBoxItem），容器 IsSelected 单向绑定 IsHighlighted（IsSelected || IsFocused），批量操作仅统计模型 IsSelected；选项弹窗继续使用 ViewModelSelectableListBoxItemTheme。鼠标/键盘在原生输入前处理并保留可聚焦的列表。回归必须检查批量选中集合、单击标记和容器 IsSelected，并覆盖搜索后容器重建；只断言 SelectedCount 无法发现视觉失效。ControlTheme 放入 ResourceDictionary，通用属性使用 ReflectionBinding 避免绑定到单一 ViewModel 类型。来源：https://docs.avaloniaui.net/docs/basics/user-interface/styling/control-themes

- 右键菜单在 PointerReleased / ContextRequested 阶段打开，不要在 PointerPressed 提前弹出后又在 ContextRequested 关闭重建；列表的 handledEventsToo 松开事件只作兜底，共享 AppContextMenu 对当前已打开菜单去重。回归检查同次手势仅赋值和打开一次、未发生关闭，并实际点击菜单项。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Controls/Control.cs


- 兼容性入口共用 Application 门面和全局浮窗；配置范围取完整 ProfileMods，不能取过滤／折叠后的显示列表，也不能用 CanDrag 禁用加载器的只读检查。混合 LOD 的 Unit 选择按 Mod ID 独立传入批量接口；修改选项先使旧计划失效，修复／恢复后的 UI 刷新必须等待。详情补算 Hash 属于派生缓存回填，不触发 ModsChanged 全库重载，否则会关闭正在读取的详情。前端流程见[兼容性说明](../features/MOD_COMPATIBILITY.md)。来源：[MVVM 异步命令](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/asyncrelaycommand)、[Avalonia 虚拟化](https://docs.avaloniaui.net/docs/reference/controls/virtualizingstackpanel)。

- 位图预览必须跟界面生命周期绑定：选项弹窗关闭时取消未完成的解码并 `Dispose` 已加载的卡片/组件/子选项位图，正在显示其中的图片预览也要先关闭；反复开关弹窗不能继续累积原生位图内存。列表缩略图用库页与配置页共享的弱引用解码缓存（键含文件修改时间），不要再用无上限强引用字典，也不要每次页面刷新都整批重新解码。解码期间弹窗可能已关闭，完成回调必须检查取消/销毁状态并直接释放位图。验证不能只看托管堆：Headless 下位图可能被 finalizer 提前回收，需结合 `Process.PrivateMemorySize64` 与真实应用观察；本轮用户在真实应用中报告滚动与反复开关选项后内存持续上升，修复后回归覆盖“关闭后 Image 为空且卡片已销毁、再次打开仍能正常解码”。来源：[Avalonia Bitmap](https://docs.avaloniaui.net/docs/reference/graphics/bitmaps)、[IDisposable 模式](https://learn.microsoft.com/dotnet/standard/garbage-collection/implementing-dispose)、[进程私有内存](https://learn.microsoft.com/dotnet/api/system.diagnostics.process.privatememorysize64)。

- 图片不能按原图尺寸解码后直接绑到卡片：选项/组件/子选项卡片实际只显示 40～64 像素，必须用 `Bitmap.DecodeToWidth` 按显示宽度（128px，兼顾 2x 屏）解码。HD2IAS 等角色包的预览图常为 3840×2160，整张解码每张约 31.6 MiB，一个含 7 个选项的包打开弹窗就要抬升约 221 MiB 原生内存，这跟“关闭时释放”无关，是打开期间的真实峰值。只有用户点击“预览原图”时才按原尺寸解码，并且由弹窗持有、在关闭预览或关闭弹窗时释放。相同内容必须复用：选项间常引用同一文件路径，导入还会把同一张图复制到多个组件目录（实测某 HD2IAS 包的 7 个选项引用只有 1 个唯一 SHA-256，4 份 9.6 MiB 副本内容完全相同），加载时按「内容哈希 + 解码宽度」复用同一 `Bitmap`，原图预览也按哈希复用，不能按路径反复解码同一张图。共享位图由弹窗按实例去重后统一释放，卡片 `Dispose` 只清引用；释放前把位图移出缓存，避免之后命中已 `Dispose` 的旧对象。回归用 1024/4K 图片断言 `PixelSize.Width` 不超过解码宽度、内容相同的不同路径拿到 `Assert.Same` 实例，并验证点击后覆盖层显示原图尺寸、关闭弹窗后覆盖层随之关闭。来源：[Avalonia Bitmap](https://docs.avaloniaui.net/docs/reference/graphics/bitmaps)、[SHA256.HashData](https://learn.microsoft.com/dotnet/api/system.security.cryptography.sha256.hashdata)、[进程私有内存](https://learn.microsoft.com/dotnet/api/system.diagnostics.process.privatememorysize64)。

- HD2IAS 组件包与散装文件夹合集共用的两级选项弹窗中，一级组件勾选表示“组件仍参与部署”，由组内选项派生：只要还有任意选项启用就保持勾选，关闭部分选项只排除对应文件，不能把组件显示成未启用；只有组内全部选项关闭时才取消一级勾选。点击一级开关则整组启用/关闭（关闭后再次勾选恢复全部选项）。一级状态必须由保存后的 `OptionToggles` 重新派生，关闭弹窗再打开不能出现一级已勾选、内部却全部关闭或相反的错位。回归覆盖“二级关闭一个选项返回后组件仍勾选、全部关闭才取消、一级重新勾选恢复全部并持久化”。来源：用户 2026-10-18 反馈与本轮回归；[Avalonia CheckBox](https://docs.avaloniaui.net/docs/reference/controls/checkbox)。

- 搜索设置“模糊搜索/拼音搜索”位于“设置 → 搜索”，分类标识 `搜索`、入口 ID `settings.search`、键 `Search.Fuzzy`/`Search.Pinyin`，默认关闭；保存后用 `SettingsService.SettingChanged` 让模组库、配置文件和日志中已有查询立即重算。所有本地搜索框共用 `SearchMatcher`：关闭时保持原有连续包含匹配，模糊搜索只增加不连续字符匹配，拼音搜索使用 `PinyinData.g.cs` 的全拼和首字母（含常见多音字候选），不引入运行时包；在线平台搜索由服务端执行，不套用本地规则。新增此类设置要同时接入三语、分类重置、入口自定义和真实控件回归；拼音表通过 `tools/generate-pinyin-data.py` 重新生成。来源：[xUnit](https://xunit.net/)、[Avalonia Headless](https://docs.avaloniaui.net/docs/concepts/headless/)。

- 创建页的压缩格式、档位、加密和密码库集中在“打包设置”标签页，避免继续增高顶部 Auto 区域、挤压编辑内容。TabControl 与表单 ScrollViewer 的内容显式 Stretch；长路径输入不能撑出右侧按钮。布局回归检查窄窗口、中英文和深浅主题下的实际控件边界、Extent/Viewport 与展开后的滚动，不能只检查 IsEffectivelyVisible。来源：[TabControl](https://docs.avaloniaui.net/controls/navigation/tabcontrol)、[ScrollViewer](https://docs.avaloniaui.net/controls/layout/containers/scrollviewer)。

- 暗潮页面复用本家（HD2 主窗口）的卡片与信息层级时，按暗潮实际数据裁剪：`DarktideModMetadata` 没有封面字段，卡片不得预留 52px 图片槽（会永久显示空灰块）；排序手柄、上下移动和移除入口用 `UtilityIcon`（`ListOrdered`、`Chevron` 旋转、`Close`）配 ToolTip，不能写 ⋮、↑、↓ 文本符号，字形与 `icon-button` 内边距不一致；状态用 12px 文字加语义前景色（`SuccessBrush`/`ErrorBrush`），不要实心 `SuccessBrush` 底配白色文字，也不要在 `chip` 上叠 `ErrorBackgroundBrush`；警告条只用 `WarningBackgroundBrush` 底色加 `Warning` 图标，不再叠 1px `WarningBrush` 亮边框；同一信息只在导航栏或状态栏出现一次（游戏状态在导航栏、路径在状态栏），页面内不再重复铺游戏路径行；空状态统一为图标 + 标题 + 提示 + `accent` 主操作按钮。`ListBox` 卡片列表须在 `ListBox.Styles` 内把 `ListBoxItem` 的 `Padding`/`Margin` 归零，否则原生选中底色与卡片边框形成双层描边。来源：[Fluent 图标使用规范](https://learn.microsoft.com/windows/apps/design/style/icons)、[Avalonia ListBox](https://docs.avaloniaui.net/docs/reference/controls/listbox)。

- 视觉走查不能只看 XAML：暗潮窗口可用 headless `Window.CaptureRenderedFrame()` 逐页导出 PNG（切换 `RequestedThemeVariant` 覆盖深浅色、切换 `SelectedTabIndex` 触发各页刷新），临时测试与输出目录用后即删。来源：[Avalonia Headless](https://docs.avaloniaui.net/docs/concepts/headless/)。
- 暗潮外观：`{loc:LocalizedBinding}` 只翻译 `UiLocalization.OptionKeys` 注册过的取值（`Dark`/`Light`/`System` 已在表内映射到 `Options.*`），不是任意 resx 键；未注册的值原样显示。因此主题下拉直接绑 `"Dark"/"Light"/"System"` 字符串，不要自造 `Xxx.Theme.Dark` 键再指望它翻译。来源：[Avalonia 数据绑定](https://docs.avaloniaui.net/docs/basics/data-bindings/)。

- 暗潮设置页刷新（`LoadAsync`）不得回读外观项：切页会把用户尚未保存的滑块与背景图调整冲回已保存值，外观只在启动时 `LoadAppearance()` 应用一次，改动即时预览、点“保存外观”才落盘。背景图存在时 `ContentSurfaceOpacity=0`、`ChromeSurfaceOpacity` 跟随卡片透明度，无背景图两者恢复 1.0；换下和清空的原生位图必须 `Dispose`，解码在 `Task.Run` 中完成并用忙标记合并连续触发。来源：[Fluent 透明材质](https://learn.microsoft.com/windows/apps/design/style/seven-essential-elements-of-fluent-design)。

- 顶部导航 `PaneFooter` 的宽度会挤占菜单项空间：带文字的“启动游戏”按钮会让英文界面在 1080 最小宽度下把“设置”推进溢出菜单。页脚操作使用紧凑图标按钮（如 36px 的 `accent` 播放图标）并保留 ToolTip；改动页脚后必须在 1080 宽、English 下确认五个导航项仍直接可见。来源：[FluentAvalonia NavigationView](https://github.com/amwx/FluentAvalonia)。
