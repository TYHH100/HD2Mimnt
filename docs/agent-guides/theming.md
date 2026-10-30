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

## 添加一个新主题的步骤

1. 在 `AppThemeService` 增加主题常量、皮肤字典 URI 与样式 URI；`Apply` 的挂载/卸载逻辑是通用的，无需修改。
2. `SettingsViewModel.ThemeOptions` 加入新值；`UiLocalization.OptionKeys` 增加“主题名 → 文案键”映射；三语 `Strings*.resx` 补 `Options.<名称>`。
3. 按“三件套”结构新建皮肤文件（建议命名 `XxxSkin.axaml` / `XxxControls.axaml` / `XxxStyles.axaml`），先复制 token 清单再逐组替换颜色与形状。
4. 补回归测试：仿照 [ThemeSkinUiTests.cs](../../tests/HD2Mimnt.UITests/ThemeSkinUiTests.cs) 断言资源覆盖与切走后恢复；仿照 [ThemeLayoutUiTests.cs](../../tests/HD2Mimnt.UITests/ThemeLayoutUiTests.cs) 断言布局不回归。
5. 按下文“验收清单”做无头截图与视觉比对。

## 注意事项（易错点）

- **不要注册自定义 `ThemeVariant`**：FluentAvalonia 只按 Light/Dark 解析自身资源；皮肤必须以 Dark（或 Light）变体为基础叠加覆盖。
- **皮肤不能常驻 `App.axaml`**：合并进 App.axaml 会污染无头 UI 测试和默认外观，只能在 `Apply` 时动态挂载。
- **换肤卸载顺序**：先移除样式、再移除字典；皮肤已挂载内容由 `AppliedSkin` 记录，不要在皮肤之外手动增删字典或样式。
- **隐式 `ControlTheme` 只在控件挂载时解析**：运行时换肤必须用应用级样式显式设置 `Theme` 或直接设置 `Template`，否则已挂载的控件不会刷新模板（开关与按钮都靠这一机制换装）。
- **模板部件命名避开 FluentAvalonia 已用名**（`OuterBorder`、`SwitchKnobBounds` 等）：FA 的隐式主题会按名字设置 Opacity，重名元素会在特定状态下被隐藏（曾导致 Ore 开关开启态整体消失）。FA 必需的 `PART_*` 部件必须保留；开关行程 = `PART_SwitchKnob` 宽度，键位槽/滑块尺寸必须与行程对齐，否则选中态盖不住目标位置。
- **`/template/` 选择器只命中一层 TemplatedParent**：要样式化内层控件（如 `FANavigationViewItemPresenter`）的模板部件，选择器必须按内层控件类型书写；FA 主题在模板里硬编码的属性值优先级高于普通样式，覆盖时需要带激活器（`:selected` 等）的样式，或放弃覆盖。
- **透明度 token 不能被新画刷替换**：`CardSurfaceOpacity` / `ChromeSurfaceOpacity` / `ContentSurfaceOpacity` 是运行时绑定目标，皮肤只能覆盖画刷颜色，不能覆盖成丢失绑定的新画刷，否则跟随系统切换主题时颜色失联。
- **强调色只覆盖 `SystemAccentColor*` 五个颜色**：FluentAvalonia 的全部强调画刷由它们派生，逐个覆盖画刷既费力又容易漏。
- **白色 2px 描边只作为 `:focus` 状态**：不要写成常驻样式；引入浅色输入控件后，搜索图标等叠在输入框上的前景要跟随 `TextControlForeground`，否则深色图标在浅底上看不清。
- **隐藏的 `TabItem` 必须在元素上直接设置本地 `IsVisible="False"`**：只靠样式隐藏时，运行时换肤触发样式重算会让页签短暂参与测量，把页签条撑到约 192px，页面内容整体下移（详见 [UI 指南](ui-and-avalonia.md)对应条目）。
- **素材版权**：不要打包 Minecraft 的纹理、字体、音效（ore-ui 仓库仅开源 @react-facet，MIT，见 <https://github.com/Mojang/ore-ui>）；第三方字体必须连同许可证文件入库（Monocraft 为 OFL-1.1，见 `Assets/Fonts/OFL.txt`）。显示字体不写进 `AvaloniaResource`（csproj 用 `Exclude` 从 `Assets\**` glob 剔除），只以 `Content` 复制到输出与发布目录，因此单文件 exe 内不含字体。
- **显示字体走外置文件**：`DisplayFontService` 在换肤时把应用目录 `Assets/Fonts` 注册成 `fonts:` 字体集合（`EmbeddedFontCollection`），再覆盖皮肤字典里的 `DisplayFontFamily`；字体文件缺失就保留皮肤自带的回退字体。易错点：集合 Key 必须是 `fonts:` 方案并带尾斜杠（如 `fonts:HD2MimntDisplay/`），与 `FontFamily("fonts:HD2MimntDisplay/#Monocraft, …")` 解析出的来源完全一致，否则不报错、直接静默回退系统字体；`new FontFamily(baseUri, names)` 会丢掉 `fonts:` 来源，只能用字符串形式；来源未注册就排版文本会抛 `InvalidOperationException`；字体集合由各自 Application 的 `FontManager` 持有，缓存要按实例（`ConditionalWeakTable`），否则无头测试新建应用时会复用失效注册。XAML 里不要再写 `avares://.../#Monocraft`。来源：[Avalonia FontCollectionBase](https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Base/Media/Fonts/FontCollectionBase.cs)、[Avalonia FontManager](https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Base/Media/FontManager.cs)、[Monocraft](https://github.com/IdreesInc/Monocraft)
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
