# 多语言与运行时切换

修改语言服务、翻译资源、系统语言检测或后续界面本地化时读取本指南。构建和测试另读[测试指南](testing-and-lifecycle.md)。

## 后端入口与行为

- Core 的 `ILocalizationService` 定义应用级语言状态、资源查找、格式化和变更通知；`ISystemLanguageProvider` 隔离系统检测。
- Application 的 `LocalizationService` 管理 `UI.Language`，默认值为 `auto`；手动选项为 `zh-CN`、`zh-TW`、`en-US`。`SupportedLanguages` 中只包含实际语言，自动选项通过 `LanguageCodes.Automatic` 单独表示。
- `TranslationCatalog` 使用内置 resx：`Strings.resx` 是英语回退资源，`Strings.zh-CN.resx` 与 `Strings.zh-TW.resx` 是简繁中文。资源覆盖导航、设置、各页、菜单、弹窗、教程、任务/通知状态和资源类型标签。
- Infrastructure 的 `SystemLanguageProvider` 在 Windows 使用 `GetUserDefaultUILanguage` 读取用户显示语言，不以区域格式或安装语言代替；其他系统使用 `CurrentUICulture`。
- 自动匹配先查具体 Culture，再查父 Culture/语言：香港、澳门及 `zh-Hant` 归繁体，`zh-Hans`、新加坡及中性中文归简体，英语地区归英语，其他语言回退英语。检测失败同样回退英语并记录日志。
- App 在创建 ViewModel/窗口前解析单例服务，立即应用已保存的选择并启动检测；自动模式每 3 秒复查一次，手动模式保持用户选择。退出时释放服务并等待检测循环清理。

## 调用与扩展

```csharp
await localization.SetLanguageAsync(LanguageCodes.TraditionalChinese);
var saveLabel = localization["Common.Save"];
var errorText = localization.Format("Settings.SaveFailed", exception.Message);
await localization.SetLanguageAsync(LanguageCodes.Automatic);
```

语言写入成功后，后端状态、资源查找和通知立即生效；写入失败保持之前的语言。设置批量保存、重置 UI 分类、重置全部及备份恢复后的 `ReloadAsync` 都会同步语言状态。相同语言/模式不重复发布通知；从自动转为相同实际语言的手动模式仍通知模式变化。

新增语言时添加对应 Culture 的 `Strings.<culture>.resx` 和 `TranslationCatalog.SupportedLanguages` 项。系统匹配会读取父 Culture/语言；存在多种脚本时补充明确映射。为新增文字提供英语回退和三份对应翻译，格式参数保持一致。

## 前端接入

“设置 → 外观 → 语言”提供跟随系统、简体中文、繁体中文和 English，并显示实际生效语言。选择独立立即保存，不受游戏路径验证或“保存设置”按钮影响；保存失败恢复旧选项，连续选择串行保存。重置、设置重载和自动检测均同步当前界面。

`UiLocalization` 在窗口创建前关联后端，将变更派发到 UI 线程并更新 `Revision`。XAML 使用 `xmlns:loc="using:HD2Mimnt.UI.Localization"`：

```xml
<TextBlock Text="{loc:Loc Settings.Title}"/>
<TextBlock Text="{loc:LocalizedBinding StatusMessage}"/>
<TextBlock Text="{loc:LocalizedBinding ModCount, FormatKey=UI.Mods}"/>
```

- `Loc` 按资源键动态绑定；`LocalizedBinding` 只用于明确属于应用的只读展示，保留原始属性值。旧消息通过三语资源的完整模板匹配，捕获参数原样带入；不认识的外部诊断原样显示。
- 类别、主题、排序和部署方式只翻译 ItemTemplate，SelectedItem/SelectedValue 和命令参数仍使用稳定原值/语言代码。语言名称显示本地名称。
- 用户模组名、作者、描述、配置/分组名、路径、输入内容及原始日志不套翻译。固定 Lua 分隔符通过 `LocalizeWhenPath=!IsUserSeparator` 区分；用户命名即使与资源文字相同也保留。格式化用户参数不递归翻译；应用生成的权限模式可显式 `TranslateValue=True`，资源类型标签可 `TranslateParts=True`。
- 代码创建的菜单及确认弹窗使用 `UiLocalization.Current.TextBinding(text)` 保持打开时实时更新；原生文件选择器的标题使用打开时的当前语言。

## 易错点与正确做法

- `LanguageChanged` 和 `PropertyChanged` 可能从检测后台或设置写入线程触发。Avalonia 适配层通过 `Dispatcher.UIThread.Post` 更新绑定，不能在后台直接改控件或集合，也不能同步等待 UI 线程。
- 服务发布 `SelectedLanguage`、`IsAutomatic`、`CurrentLanguage`、`CurrentCulture` 与 `Item[]` 变更。界面需绑定索引器或订阅事件刷新派生文本；仅在构造时调用 `GetString`、使用静态资源或保留硬编码文字，都不能实现已有界面的实时更新。
- 使用服务的 `CurrentCulture` 明确查询和格式化。服务不修改 `CurrentCulture`、`CurrentUICulture` 或默认线程 Culture，避免影响协议、路径、数字序列化。新增复杂消息优先保存资源键和参数在展示时格式化；旧消息仅通过已收录的展示模板重译，不能对用户数据全局替换。
- 设置事件在持久化成功且释放设置闸门后发布，防止监听者重新读设置时死锁；重置和重载同样发布实际变化。服务构造读取已加载的内存设置，禁止在 UI 启动阶段同步等待异步读取。
- 自动模式保存 `auto`，不能把检测结果写成手动语言，否则下次系统语言变化将无法继续跟随。界面语言使用稳定代码，语言的本地名称只作显示。
- Avalonia 12 的绑定类型使用 `BindingBase`，不沿用旧 `IBinding`。静态文案也通过 `Revision` 的 MultiBinding 刷新，不能只假定索引器 `Item[]` 通知会让已有控件重读资源；用实际语言控件切换后断言已有标签。
- 代码创建弹窗的静态文案使用 `LocExtension.CreateBinding(资源键)`；不要先取译文再传给 `TextBinding` 反查资源键，相同中文或英文可能命中其他资源，切换语言后出现错误文案。沿用上述 `Revision` 刷新机制，回归已打开弹窗的三语标题和按钮。来源：[Avalonia MultiBinding](https://docs.avaloniaui.net/docs/concepts/data-binding/multibinding)。
- 自定义扩展创建反射绑定时用 `ReflectionBindingExtension.ProvideValue(serviceProvider)` 保留 `$parent` 的解析上下文。这类路径不需要编译绑定的 `((vm:类型)DataContext)` 转型；直接从父控件 DataContext 访问属性，防止自定义扩展字符串中的类型前缀无法解析。
- ComboBox 初始化选中值时可能短暂回写空值，过滤这次过渡，避免持久化空代码或生成误报；真实写入失败必须提示并回滚。资源模板匹配接受 CRLF/LF 差异，但不修改捕获到的用户参数或路径。
- 动态翻译的 `ComboBoxItem` 使用内层 `TextBlock Text="{loc:Loc ...}"`；直接绑定字符串 `Content` 时，Avalonia 12 的已选展示可能保留选择时的字符串，语言切换后属性已更新但画面仍旧。验证实际选中区域的截图，不只检查 `SelectedItem.Content`。来源：[ComboBox.UpdateSelectionBoxItem](https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Controls/ComboBox.cs)。
- 英文长说明在最小窗口可能挤掉列表；限制辅助说明的展示高度并用完整悬停提示，检查三语、深浅主题和最小尺寸，不只验证编译成功。

- 多段 HTTP 错误不能只把整段字符串交给模板匹配：保存异常和操作对象，展示时分别翻译操作、状态模板、建议及来源标签，并通过 `Revision` 重新格式化。服务端原始诊断保持原文，不能对其递归替换；运行时切换要检查已有错误。

## 验证与参考

相关测试：`LocalizationServiceTests`、`SettingsServiceTests`、`TranslationCatalogTests`；UI 接入在 `HD2Mimnt.UITests/LocalizationUiTests.cs`，覆盖实际键盘选项、立即持久化、后台派发、reset/reload、保存失败、连续选择、已打开弹窗/菜单/通知和用户数据保留。设置和配置方向行为回归：`SettingsViewModelTests`、`ProfileViewModelTests`。

- [.NET 资源回退过程](https://learn.microsoft.com/dotnet/core/extensions/package-and-deploy-resources)
- [ResourceManager.GetString](https://learn.microsoft.com/dotnet/api/system.resources.resourcemanager.getstring)
- [CultureInfo.Parent](https://learn.microsoft.com/dotnet/api/system.globalization.cultureinfo.parent)
- [Windows GetUserDefaultUILanguage](https://learn.microsoft.com/windows/win32/api/winnls/nf-winnls-getuserdefaultuilanguage)
- [Avalonia UI 线程](https://docs.avaloniaui.net/docs/guides/development-guides/accessing-the-ui-thread)
