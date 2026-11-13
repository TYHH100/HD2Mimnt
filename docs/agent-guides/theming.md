# 主题与换肤指南

处理外观、主题、皮肤相关任务前先读本文件；控件模板重绘与选择器的通用易错点见 [UI 指南](ui-and-avalonia.md)。结论先行的可行性评估记录：FluentAvalonia 之上做运行时换肤皮肤是可行的，但必须遵守本文的挂载/卸载与命名约束，否则会出现控件不刷新、元素被隐藏、布局被撑高等问题（来源均为 Ore 皮肤落地过程中实际踩过的坑）。

## 现有架构

- 换肤入口 [AppThemeService.cs](../../src/HD2Mimnt.UI/Services/AppThemeService.cs) 的 `Apply(string)`：`Dark` / `Light` / `System` 映射 FluentAvalonia 变体；皮肤主题（当前为 `Ore` 与 `Helldivers`，注册在 `Skins` 字典）先卸载上一个皮肤（先移除样式再移除字典），再合并皮肤资源字典并挂载应用级样式，用 `ConditionalWeakTable` 按应用实例记录已挂载内容。
- 皮肤采用三件套结构，放在 `src/HD2Mimnt.UI/Styles/Skins/`，以 Ore 为例：
  - `OreSkin.axaml`：语义色 token（`Surface0Brush`–`Surface3Brush`、`BorderBrush`、文字画刷等）、强调色 `SystemAccentColor*`、`DisplayFontFamily`（外置显示字体的回退值，运行时由 `DisplayFontService` 覆盖）、`CardShadow` / `CardBorderThickness`、圆角 token；
  - `OreControls.axaml`：FluentAvalonia 控件资源覆盖 + 专属 `ControlTheme` / `ControlTemplate`（按钮、开关、输入框、下拉框、滑块、复选框等）；
  - `OreStyles.axaml`：应用级样式——显式设置 `Theme` / `Template`、模板部件覆盖、伪类状态样式。
- 默认 Fluent 观感的可覆盖 token 定义在 [AppResources.axaml](../../src/HD2Mimnt.UI/Styles/AppResources.axaml) / [AppStyles.axaml](../../src/HD2Mimnt.UI/Styles/AppStyles.axaml)：`DisplayFontFamily`、`CardShadow`、`CardBorderThickness`；未覆盖时保持 Fluent 默认。
- 外观设置链路：`SettingsViewModel.ThemeOptions` → `UiLocalization.OptionKeys`（主题名映射三语 `Options.*` 文案）→ `MainWindowViewModel` 监听设置变更并调用 `AppThemeService.Apply`。

## 窗口玻璃（浅色主题）

玻璃有两套实现，由 `GlassMaterialService.Apply` 选择：

1. **系统丙烯酸（默认）**：`TransparencyLevelHint = AcrylicBlur, Blur`，Win11 22H2+ 再补 `DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW`。背后是真实画面、实时，窗口能被截图/录屏正常拍到；半径由 DWM 固定，`UI.GlassBlur` 在此时不起作用。
2. **自绘实时玻璃（可选，模糊可调）**：仅在用户开启「可调模糊玻璃」（`UI.GlassLiveBackdrop` = true）时启用。`LiveBackdropService` 每 60ms 用 GDI `StretchBlt` 取一次“窗口所在的屏幕区域”（比窗口四周各多取 56 DIP，供模糊采样），交给 `GlassBackdropImage` 用 `BlurEffect` 模糊，半径就是设置里的「模糊强度」（`UI.GlassBlur`，0–48，0 为完全清晰）。

关键约束：

- **取屏前必须先 `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)`**，否则取到的是自己，画面会无限递归（表现为“窗口里套窗口”）。该 API 失败就直接退回丙烯酸，不能“先试试再说”。
- **「可调模糊玻璃」（`UI.GlassLiveBackdrop`）默认必须关闭，这是硬性要求**：排除自身的必然代价是启用期间本窗口对任何截屏/录屏不可见（截图里是一块“洞”），用户会当成“窗口消失了”的严重 bug。所以默认走系统丙烯酸，只有用户主动开启该开关才启动取屏（此时模糊滑块才有作用对象）。不要把默认值改成 true，也不要试图“只在别人截图时恢复可截屏”——系统没有任何“正在被截图”的通知。
- **取屏区域每帧从 Win32 读**：用 `GetClientRect` + `ClientToScreen`（物理像素），不要用 `PointToScreen`/`ClientSize` 缓存值——拖动窗口时 Avalonia 的位置事件会晚一拍，背景就整体错位。窗口客户区原点与 Avalonia 视觉坐标 (0,0) 一致（实测 `PointToScreen(0,0) == ClientToScreen(0,0)`）。
- **图层尺寸与偏移必须无条件写入**：`Image.Width` 初始是 `NaN`，任何“值变了才更新”的判断（`Math.Abs(NaN - w) > 0.5` 恒为 false）都会吞掉首次赋值，整块背景会偏移一整圈外扩边（56 DIP），表现为“背景完全错位”。正确写法见 `MainWindow.OnGlassFrameUpdated`：`Width/Height = frame.Size`，`Canvas.Left/Top = -LiveHeadroomDips`。
- 取屏耗时实测约 17ms 且**与区域大小无关**（本质是一次 60Hz 合成等待），所以按 60ms 间隔取帧即可，不必为了省 CPU 去降分辨率。
- 扩展客户区（`ExtendClientAreaToDecorationsHint=True` + `ExtendClientAreaTitleBarHeightHint=40`）：Avalonia 12 会把内容铺满整窗、在内容**之上**自绘标题栏（标题文字 + 置顶/最小化/最大化/关闭）。玻璃层因此能覆盖标题栏，整窗才是同一块玻璃，也不会再出现“边框一种颜色、主体另一种颜色”。
- 扩展客户区后**没有系统标题栏可拖**：第 0 行必须放一个 `Background="Transparent"` 的命中区（`TitleBarDragArea`）并在 `PointerPressed` 里调用 `BeginMoveDrag`，双击切最大化也要自己处理。玻璃层与着色层都是 `IsHitTestVisible="False"`，点它们接不到输入，窗口就拖不动。
- `ApplicationSurface` 自身背景必须是 `Transparent`，着色层改成独立 `Rectangle`（`ShellFillBrush`）并排在玻璃图片**之后**。若仍用 Grid 的背景，着色会被画在玻璃图片下面，玻璃就原样显示未着色的桌面。
- 强度换算与卡片透明度规则不变：`ShellSurfaceOpacity` 只由一层承担（内容区/顶栏/底栏同时置 0，否则同一块玻璃会叠成深浅三段）；卡片取 `min(用户设置, 强度上限)`，强度只能增加通透度；着色下限 0.62，再低会让深色壁纸前的正文难以看清。
- 只对浅色变体生效，且与“自定义背景图”互斥。`IsGlassMaterialVisible` 控制三个设置项的显隐；`IsGlassBlurEnabled` 在强度为 0 或未开启「可调模糊玻璃」时把模糊滑块置灰（此时没有作用对象）。
- 历史结论：DWM 的丙烯酸/Mica 半径没有应用侧参数（`ACCENT_POLICY` 只有着色 alpha，`DWMWA_SYSTEMBACKDROP_TYPE` 只有材质类型），所以“半径可调”与“实时”只能靠自建取屏同时满足；早前试过的“自绘模糊壁纸”虽然半径可调，但壁纸是静态图、不反映身后窗口，已废弃。
- 验收方式：自绘玻璃路径下 `WDA_EXCLUDEFROMCAPTURE` 会让窗口在截图里消失，无法用常规截屏验收（默认的丙烯酸路径不受影响，直接截屏即可）。要核对取屏对齐，先临时开启「可调模糊玻璃」让取屏真正跑起来，再临时在进程内把 `WDA_NONE` 设几十毫秒后立刻 `BitBlt` 整屏再恢复排除（时间短于一个取帧周期才不会被递归画面污染），或把取到的一帧与整屏做模板匹配：匹配偏移应为 0。核对版式时也可临时把模糊调 0。

## 添加一个新主题的步骤

1. 在 `AppThemeService` 增加主题常量、皮肤字典 URI 与样式 URI；`Apply` 的挂载/卸载逻辑是通用的，无需修改。
2. `SettingsViewModel.ThemeOptions` 加入新值；`UiLocalization.OptionKeys` 增加“主题名 → 文案键”映射；三语 `Strings*.resx` 补 `Options.<名称>`。
3. 按“三件套”结构新建皮肤文件（建议命名 `XxxSkin.axaml` / `XxxControls.axaml` / `XxxStyles.axaml`），先复制 token 清单再逐组替换颜色与形状。
4. 补回归测试：仿照 [ThemeSkinUiTests.cs](../../tests/HD2Mimnt.UITests/ThemeSkinUiTests.cs) 断言资源覆盖与切走后恢复；仿照 [ThemeLayoutUiTests.cs](../../tests/HD2Mimnt.UITests/ThemeLayoutUiTests.cs) 断言布局不回归。
5. 按下文“验收清单”做无头截图与视觉比对。

## 注意事项（易错点）

- **启动期主题必须在窗口创建前应用**：`App.axaml` 的 `RequestedThemeVariant="Dark"` 只是无设置时的默认外观；保存的主题由 `App.InitializeDesktopAsync` 在 `new MainWindow()` 之前用 `SettingsService.GetSetting(SettingKeys.Theme, AppThemeService.DefaultTheme)` 同步读取并 `Apply`（`SettingsService` 构造时已同步载入设置）。若只在 `MainWindowViewModel.InitializeAsync` 里应用，此时窗口已经 `Show()`，浅色/皮肤主题用户会先看到一帧深色再切换，对光敏感用户尤其明显。`Apply` 按 Application 记录当前主题，同一主题重复调用直接返回，所以设置加载触发的二次 `Apply` 不会卸载重装皮肤产生闪动。
- **不要注册自定义 `ThemeVariant`**：FluentAvalonia 只按 Light/Dark 解析自身资源；皮肤必须以 Dark（或 Light）变体为基础叠加覆盖。
- **皮肤不能常驻 `App.axaml`**：合并进 App.axaml 会污染无头 UI 测试和默认外观，只能在 `Apply` 时动态挂载。
- **换肤卸载顺序**：先移除样式、再移除字典；皮肤已挂载内容由 `AppliedSkin` 记录，不要在皮肤之外手动增删字典或样式。
- **隐式 `ControlTheme` 只在控件挂载时解析**：运行时换肤必须用应用级样式显式设置 `Theme` 或直接设置 `Template`，否则已挂载的控件不会刷新模板（开关与按钮都靠这一机制换装）。
- **模板部件命名避开 FluentAvalonia 已用名**（`OuterBorder`、`SwitchKnobBounds` 等）：FA 的隐式主题会按名字设置 Opacity，重名元素会在特定状态下被隐藏（曾导致 Ore 开关开启态整体消失）。FA 必需的 `PART_*` 部件必须保留；开关行程 = `PART_SwitchKnob` 宽度，键位槽/滑块尺寸必须与行程对齐，否则选中态盖不住目标位置。
- **`/template/` 选择器只命中一层 TemplatedParent**：要样式化内层控件（如 `FANavigationViewItemPresenter`）的模板部件，选择器必须按内层控件类型书写；FA 主题在模板里硬编码的属性值优先级高于普通样式，覆盖时需要带激活器（`:selected` 等）的样式，或放弃覆盖。
- **皮肤样式的 setter 值必须写资源键，不能写字面量**：从 `Application.Styles` 移除皮肤样式容器不会撤销已经生效的 setter（Avalonia 已知行为），写死的 `BorderThickness`、`BoxShadow`、`#F2E33B` 会永久留在已挂载控件上——“切回 Dark/Light/System 后导航黄框还在”就是这个原因（重建模板、切 ThemeVariant、`ApplyTemplate()` 都救不回来，只有把元素从可视树摘下再挂回才会重算）。`DynamicResource` 值会随皮肤字典卸载自动回落，所以皮肤私有键（`HdNav*`、`OreNav*`、`Ore*Shadow`）不要在内置主题里定义，未命中即等于不覆盖。FA 与内置主题同名的键（如 `BorderBrush`）卸载后会落到内置值，仍可能留下隐形描边，需要时用皮肤私有键复制一份。回归断言见 `ThemeSkinUiTests.SkinThemes_ShouldRestoreTemplatePartOverridesOnLeave`：挂载后模板部件快照必须变化，切走后必须回到基线快照。来源：[Avalonia #3495 运行时换样式不会更新全部控件](https://github.com/AvaloniaUI/Avalonia/issues/3495)、[Avalonia Styles 排障文档](https://docs.avaloniaui.net/troubleshooting/ui-development/styles)
- **透明度 token 不能被新画刷替换**：`CardSurfaceOpacity` / `ChromeSurfaceOpacity` / `ContentSurfaceOpacity` 是运行时绑定目标，皮肤只能覆盖画刷颜色，不能覆盖成丢失绑定的新画刷，否则跟随系统切换主题时颜色失联。
- **强调色只覆盖 `SystemAccentColor*` 五个颜色**：FluentAvalonia 的全部强调画刷由它们派生，逐个覆盖画刷既费力又容易漏。
- **白色 2px 描边只作为 `:focus` 状态**：不要写成常驻样式；引入浅色输入控件后，搜索图标等叠在输入框上的前景要跟随 `TextControlForeground`，否则深色图标在浅底上看不清。
- **隐藏的 `TabItem` 必须在元素上直接设置本地 `IsVisible="False"`**：只靠样式隐藏时，运行时换肤触发样式重算会让页签短暂参与测量，把页签条撑到约 192px，页面内容整体下移（详见 [UI 指南](ui-and-avalonia.md)对应条目）。
- **素材版权**：不要打包 Minecraft 的纹理、字体、音效（ore-ui 仓库仅开源 @react-facet，MIT，见 <https://github.com/Mojang/ore-ui>）；第三方字体必须连同许可证文件入库（Monocraft 为 OFL-1.1，见 `Assets/Fonts/OFL.txt`）。显示字体不写进 `AvaloniaResource`（csproj 用 `Exclude` 从 `Assets\**` glob 剔除），只以 `Content` 复制到输出与发布目录，因此单文件 exe 内不含字体。
- **显示字体走外置文件**：`DisplayFontService` 在换肤时把应用目录 `Assets/Fonts` 注册成 `fonts:` 字体集合（`EmbeddedFontCollection`），再覆盖皮肤字典里的 `DisplayFontFamily`；字体文件缺失就保留皮肤自带的回退字体。易错点：集合 Key 必须是 `fonts:` 方案并带尾斜杠（如 `fonts:HD2MimntDisplay/`），与 `FontFamily("fonts:HD2MimntDisplay/#Monocraft, …")` 解析出的来源完全一致，否则不报错、直接静默回退系统字体；`new FontFamily(baseUri, names)` 会丢掉 `fonts:` 来源，只能用字符串形式；来源未注册就排版文本会抛 `InvalidOperationException`；字体集合由各自 Application 的 `FontManager` 持有，缓存要按实例（`ConditionalWeakTable`），否则无头测试新建应用时会复用失效注册。XAML 里不要再写 `avares://.../#Monocraft`。来源：[Avalonia FontCollectionBase](https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Base/Media/Fonts/FontCollectionBase.cs)、[Avalonia FontManager](https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Base/Media/FontManager.cs)、[Monocraft](https://github.com/IdreesInc/Monocraft)
- **用户自选界面字体覆盖皮肤**：外观设置可把界面字体切到系统字体族或字体文件，由 `AppFontService` 在应用级资源里写入 `UiFontFamily`（`Window` 样式引用）和 `DisplayFontFamily`；恢复默认时移除这两个覆盖，皮肤字体重新生效。字体文件经 `FontFileReader` 读出字体族名后注册成 `fonts:` 集合；**TTF/OTF 的 `name` 表是大端，`BinaryReader` 是小端**，必须逐字节按大端拼装，否则所有表标签都读反、解析静默失败。系统字体族用 GDI+ `InstalledFontCollection` 枚举，列表**枚举完成后整体赋值**再用普通 `ComboBox` 展示；不要用 `AutoCompleteBox` 做字体候选——Avalonia 12.1 在提交选择关闭下拉时会走 `SelectingItemsControlSelectionAdapter` → `PopupRoot.SetChild` → `AvaloniaList.Remove`，列表在打开期间增减就会抛 `ArgumentOutOfRangeException` 并冒到界面线程。
- **改模板前先读对应源码**：FA 控件主题与 Avalonia 控件（如 ToggleSwitch）的滑块驱动方式直接决定模板结构，凭感觉改模板会做出“能编译但状态错乱”的外观。

## 验收清单

1. `dotnet build HD2Mimnt.slnx --no-restore` 通过；共享 `bin/obj` 时不并行构建。
2. 回归测试：`ThemeSkinUiTests`、`ThemeLayoutUiTests` 及既有外观相关测试全部通过，并为新主题补齐资源覆盖/恢复断言。
3. 视觉验收用无头截图：临时测试通过 `UiScope.CreateAsync` 启动真实 `MainWindow`，`AppThemeService.Apply` 切到新主题后用 `CaptureRenderedFrame`（实际合成渲染，`RenderTargetBitmap.Render` 不能代替）保存 PNG，与参考图逐状态（Normal / Hover / Focused / Pressed / Disabled）比对。
4. 截图前关闭教程、排空通知横幅，避免遮挡控件；焦点/悬停等状态用无头鼠标与 `Focus()` 驱动后再次截帧。
5. 运行中的正式程序会占用 `bin` 输出：验证用 `dotnet test -p:OutputPath=bin/TempVerify/` 独立构建，结束后删除临时输出目录、临时测试文件与截图，不留在仓库。
6. 文档同步：皮肤结构或约束变化时更新本文件与 [UI 指南](ui-and-avalonia.md)的对应条目。

## Ore 皮肤待扩展项

设计系统已评估、尚未实现的组件（按约定顺序）：菱形单选框、黄/蓝色消息条（InfoBar）、九宫格纹理与音效、自定义窗口标题栏。实现任一项时先按“注意事项”核对模板机制，再按“验收清单”回归。
