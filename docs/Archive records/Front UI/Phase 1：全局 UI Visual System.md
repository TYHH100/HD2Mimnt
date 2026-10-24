# HD2Mimnt UI 重构执行文档
# Phase 1 — Global UI Visual System
# 全局 UI 视觉系统建立阶段

版本：Phase 1
项目：HD2Mimnt
技术栈：Avalonia UI / .NET 10 / Jalium.UI
前置阶段：
- Phase 0：代码与架构现状审计 —— 已完成
- Phase 0.5：视觉现状审计 —— 已完成
当前阶段：
- Phase 1：Global UI Visual System

---

# 0. 阶段目标

本阶段不负责重新设计任何具体页面。

本阶段唯一目标：

> 建立 HD2Mimnt 统一、稳定、可复用的全局 UI 视觉系统。

后续所有页面：

- Profiles
- Profile Mod Workspace
- Mod Library
- Settings
- More
- Task Center
- Notification
- Dialog
- Overlay
- Plugin Management
- Backup
- Log

均必须使用本阶段建立的视觉规则。

本阶段完成后，不允许后续页面各自重新定义一套视觉语言。

---

# 1. 本阶段核心设计方向

HD2Mimnt 是：

> Windows 桌面工具软件

而不是：

- Web Dashboard
- SaaS 管理后台
- 数据分析平台
- 游戏启动器首页
- 卡片式控制中心
- Mobile App
- Marketing UI

因此 UI 必须遵循：

1. 平面化
2. 紧凑
3. 高信息密度
4. 清晰层级
5. 快速操作
6. 少装饰
7. 少卡片
8. 少圆角
9. 少阴影
10. 少动画
11. 强调列表和行
12. 强调当前状态
13. 强调操作效率

视觉重点应该来自：

- 排版
- 间距
- 分隔线
- 对齐
- 字体层级
- 选中状态
- 状态颜色
- 操作位置

而不是：

- 大面积背景卡片
- 阴影
- 发光
- 渐变
- 大圆角
- 玻璃效果
- 大型装饰图形
- 巨型统计数字

---

# 2. 本阶段允许修改的范围

允许修改：

- src/HD2Mimnt.UI/App.axaml
- src/HD2Mimnt.UI/App.axaml.cs（仅在确有必要时）
- 全局 Theme Resources
- 全局 Brush
- 全局 Typography Resource
- 全局 Control Styles
- 全局 Button Styles
- 全局 TextBox Styles
- 全局 ComboBox Styles
- 全局 ListBox/ListBoxItem Styles
- 全局 CheckBox Styles
- 全局 RadioButton Styles
- 全局 Border / Separator 相关样式
- 全局状态样式
- 全局动画/Transition 定义

允许为了统一视觉系统，对现有页面中直接依赖旧全局 Style 的少量引用进行适配。

---

# 3. 本阶段禁止修改

禁止：

- 重写业务逻辑
- 修改数据库结构
- 修改 Mod Library 数据模型
- 修改 Profile 数据模型
- 修改部署逻辑
- 修改清理逻辑
- 修改 Mod 解析逻辑
- 修改 Manifest 逻辑
- 修改 PatchTool
- 修改 `.patch_*` 规则
- 修改 `.gpu_resources` 规则
- 修改 `.stream` 规则
- 修改 Mod 导入逻辑
- 修改配置文件逻辑
- 修改 Profile-Mod 关系逻辑
- 修改 Profile 专属 Mod Options 逻辑
- 修改插件系统逻辑
- 修改任务系统逻辑
- 修改日志系统逻辑
- 修改备份逻辑
- 大规模 MVVM 重构
- 修改 Navigation 架构
- 修改页面功能
- 删除已有功能

本阶段是：

> Visual System Foundation

不是：

> Page Redesign

---

# 4. 全局视觉原则

## 4.1 不再使用“所有东西都是卡片”的设计

禁止把以下内容默认做成 Card：

- 普通列表
- 普通工具栏
- 普通设置项
- 普通 Profile
- 普通 Mod
- 普通按钮组
- 普通状态信息

Border 不等于 Card。

只有真正需要形成视觉分组的内容才允许使用容器。

---

# 5. Surface 系统

现有项目存在：

- Surface0
- Surface1
- Surface2
- Surface3

本阶段保留 Surface 概念，但重新定义职责。

## Surface0

用途：

> 页面主背景

特征：

- 最基础背景
- 不用于突出内容
- 不用于制作卡片

典型用途：

- 页面主体
- ScrollViewer 背景
- 主内容区域

---

## Surface1

用途：

> 次级区域 / 工具栏 / 分隔区域

典型用途：

- Filter Bar
- Toolbar
- List Header
- Secondary Header
- 工具区域

不得把 Surface1 大量嵌套在 Surface1 中。

---

## Surface2

用途：

> 明确的内容区域

典型用途：

- 设置分组
- Detail Panel
- Overlay Panel
- 明确需要视觉隔离的区域

Surface2 不等于“卡片”。

---

## Surface3

用途：

> 当前选中 / 强调 / 临时操作区域

典型用途：

- Selected Item
- Active Profile
- Batch Operation Bar
- 当前操作区域

不得把 Surface3 当成普通列表项的默认背景。

---

# 6. Surface 使用原则

禁止：

```text
Surface0
 └ Surface1
    └ Surface2
       └ Surface3
````

这种多层嵌套作为普通页面结构。

普通页面尽量保持：

```text
Surface0
 ├ Toolbar
 ├ List
 └ Detail
```

而不是：

```text
Surface0
 └ Surface1
    └ Surface2
       └ Border
          └ Content
```

目标：

> 减少视觉层级，而不是增加视觉层级。

---

# 7. CornerRadius 系统

当前项目存在大量：

```text
CornerRadius="999"
```

这是本次 UI 重构必须重点处理的问题。

## 7.1 全局禁止 999 圆角

除非元素本身具有明确的：

> 圆形 / 胶囊型语义

否则禁止：

```xml
CornerRadius="999"
```

尤其禁止用于：

* Primary Button
* Secondary Button
* 普通按钮
* Navigation Button
* List Item
* Badge
* 普通标签
* 普通状态
* Filter
* Toolbar

---

# 8. 标准圆角等级

统一采用：

```text
0 px
2 px
4 px
6 px
```

## 0 px

用于：

* 页面区域
* Separator
* List
* ToolBar
* Header
* Footer

---

## 2 px

用于：

* 小型状态区域
* 非重点容器
* 小型输入区域

---

## 4 px

默认圆角。

用于：

* Button
* List Item
* Input
* ComboBox
* Selected Item
* 普通 Panel

---

## 6 px

最大常规圆角。

用于：

* Dialog
* Overlay
* 较明显的内容容器
* 特殊信息区域

---

## 8 px 以上

默认禁止。

如果确实存在必须保留的特殊视觉组件，需要在对应页面设计阶段单独说明原因。

---

# 9. 胶囊 / 圆形元素

只有以下情况允许使用较大的 CornerRadius：

```text
圆形状态指示器
真正的 Pill Badge
极短文本的状态标签
```

例如：

```text
●
NEW
ACTIVE
ERROR
```

但不能因为“看起来漂亮”就将普通 Button 制作成 Pill。

---

# 10. Button 系统

统一建立以下按钮层级：

```text
Primary
Secondary
Tertiary
Danger
Icon
Navigation
```

---

# 11. Primary Button

用途：

> 当前页面最主要操作

例如：

* Add Mod
* Create Profile
* Save
* Deploy
* Confirm

视觉：

* 明确
* 稍突出
* 不使用胶囊形
* CornerRadius = 4
* 高度保持紧凑

禁止：

* 大型圆角
* 阴影
* 渐变
* Glow
* 夸张 Hover 动画

---

# 12. Secondary Button

用途：

> 常规辅助操作

例如：

* Cancel
* Refresh
* Copy
* Open
* Compare

视觉弱于 Primary。

CornerRadius：

```text
4 px
```

---

# 13. Tertiary Button

用途：

> 低优先级操作

例如：

* Clear
* Reset
* More
* Close

原则：

> 尽可能轻量。

不要为了“有按钮感”而给它增加厚重背景。

---

# 14. Danger Button

用途：

* Delete
* Remove
* Clear All
* Destructive Operation

要求：

* 颜色明确表达危险
* 不允许使用夸张动画
* 不允许持续发光
* 不允许大型警告卡片

危险状态应该通过：

```text
颜色
文字
确认流程
```

表达。

---

# 15. Icon Button

Icon Button 必须：

* 小
* 紧凑
* 无巨大圆角
* 无装饰背景
* 明确 Hover
* 明确 ToolTip

CornerRadius：

```text
4 px
```

除非按钮本身就是圆形图标按钮。

---

# 16. Navigation Button

主导航不是普通 Button。

导航状态应该主要通过：

* Foreground
* Background
* Bottom/Side indicator
* FontWeight
* Selected state

表达。

不要使用：

```text
胶囊按钮
大圆角
卡片导航
悬浮按钮
```

---

# 17. Button 高度

桌面工具统一采用紧凑控件尺寸。

建议：

```text
Small    ≈ 28 px
Normal   ≈ 32 px
Large    ≈ 36 px
```

默认优先：

```text
32 px
```

不要大面积使用 40~48 px 的巨大按钮。

---

# 18. Typography 系统

建立统一 Typography Hierarchy。

## Page Title

用途：

页面标题。

特点：

* 清晰
* 稍大
* 中等偏高权重
* 不要巨大

---

## Section Title

用途：

页面内部区域标题。

字号明显小于 Page Title。

---

## Body

普通正文。

---

## Secondary

用于：

* 描述
* 辅助信息
* 作者
* 版本
* 时间
* 文件大小

---

## Caption

用于：

* 极次要信息
* 小型状态说明

---

# 19. 字体层级原则

禁止通过：

```text
超大字体
粗体堆叠
巨大数字
```

制造所谓“高级感”。

桌面工具的层级主要来自：

```text
字号
字重
颜色
间距
位置
```

---

# 20. Spacing 系统

建立统一间距尺度：

```text
4
8
12
16
20
24
32
```

原则：

* 4：紧密元素
* 8：控件内部
* 12：普通元素之间
* 16：区域内部
* 20：主要区域间距
* 24：页面边距
* 32：大型区域分隔

避免出现大量：

```text
17
19
23
27
31
37
```

等无体系间距。

除非布局实际需要。

---

# 21. 页面边距

标准页面默认：

```text
24 px
```

但是：

> 24 px 是默认值，不是强制值。

高密度列表页面允许：

```text
16 px
20 px
```

避免因为统一 24px 导致桌面工具浪费空间。

---

# 22. List 系统

HD2Mimnt 的核心 UI 应该：

> 以 List / Row 为主，而不是 Card Grid。

这是本次重构的重要原则。

---

# 23. List Row

普通列表项应具有：

```text
固定高度
水平排列
明确选中状态
明确 Hover 状态
轻量分隔线
```

而不是：

```text
Border
圆角
背景
阴影
内嵌 Border
再套一层 Border
```

---

# 24. List Row 状态

至少统一：

```text
Normal
Hover
Selected
Disabled
Error
```

Selected 应明显，但不能做成巨大彩色卡片。

推荐：

```text
背景变化
左侧/底部轻量 Accent
文字颜色变化
```

而不是：

```text
大面积高亮
Glow
Shadow
```

---

# 25. List Separator

普通列表允许使用：

```text
1px Separator
```

分隔线颜色应该足够弱。

目标：

> 看得见结构，但不抢视觉注意力。

---

# 26. List 信息层级

例如 Mod：

```text
Mod Name
Version / Author / Category
Status
Size
```

应通过 Typography Hierarchy 区分。

不要给每一项都制作：

```text
Badge
Pill
Card
Border
Background
```

---

# 27. Badge / Status

状态标签用于：

> 真正的状态

例如：

```text
Enabled
Disabled
Deployed
Error
Installed
Missing
```

不要用 Badge 表达普通属性。

例如：

```text
作者
版本
分类
文件大小
```

不应该全部 Badge 化。

---

# 28. Status 与 Action 必须分离

例如：

```text
[Enabled]     ← 状态

[Deploy]      ← 操作
```

不能让 UI 看起来像：

```text
[Enabled]
[Deploy]
[Delete]
[Author]
[Version]
[Category]
```

全部都是同一种 Pill。

---

# 29. Profile / Library 视觉语义

必须在全局系统层面明确以下关系。

## Mod Library

表达：

> 我拥有的 Mod。

Library 中存在：

```text
Mod A
Mod B
Mod C
```

并不代表这些 Mod 当前正在使用。

---

## Profile

表达：

> 当前 Profile 正在使用的 Mod 集合。

---

## Profile Mod

表达：

> 某个 Profile 中某个 Mod 的配置实例。

因此：

```text
Library Mod
```

和：

```text
Profile Mod
```

视觉上必须能够区分。

---

# 30. Profile-specific Options

必须避免产生以下错误认知：

> 修改 Profile A 中的 Mod 设置，会修改 Library 中 Mod B 的全局设置。

实际语义：

```text
Library
    Mod A
       ↓
Profile A
    Mod A
       Options A

Profile B
    Mod A
       Options B
```

因此 Profile Mod Workspace 的 UI 必须明确表示：

```text
当前 Profile
当前 Profile 使用的 Mod
当前 Profile 对该 Mod 的配置
```

---

# 31. Icon 系统

当前项目大量使用：

```text
Emoji
Unicode Symbol
TextBlock
```

例如：

```text
📦
```

本阶段必须建立统一 Icon 策略。

---

# 32. Icon 使用原则

禁止：

> 一个页面 Emoji，另一个页面 Unicode，再另一个页面不同风格 SVG。

整个应用必须尽量保持统一。

优先顺序：

```text
统一 Icon Font / Icon Provider
>
统一 SVG/Icon Resource
>
Unicode Symbol
>
Emoji
```

Emoji 不应作为正式 UI 图标。

---

# 33. Empty State

例如 Library 没有 Mod：

不要只显示：

```text
📦
没有模组
```

应该建立：

```text
标题
说明
主要操作
```

但 Empty State 本身不应该成为巨大卡片。

---

# 34. Loading

Loading 应保持：

* 小
* 简单
* 局部

避免：

* 全屏动画
* 大型 Spinner
* 页面跳动
* 装饰动画

---

# 35. Error State

错误状态必须：

```text
明确
可读
可操作
```

例如：

```text
加载 Mod 失败

无法读取该 Mod 的文件。

[重试]
```

而不是：

```text
巨大红色卡片
```

---

# 36. Animation

当前审计未发现明显自定义 Transition / Animation。

本阶段不要为了“现代感”主动添加大量动画。

---

# 37. Animation 规则

允许：

```text
100~180ms
```

用于：

* Hover
* Selected
* Visibility
* Panel 状态变化

禁止：

* Bounce
* Spring
* Scale
* 大幅 Slide
* 页面飞入
* 卡片浮动
* Glow Animation

动画必须服务于：

> 状态反馈

而不是：

> 装饰。

---

# 38. Overlay / Dialog

Overlay 与普通页面不同。

允许：

* Surface2 / Surface3
* 轻微圆角
* 半透明遮罩

但仍然禁止：

* 巨大圆角
* 强烈阴影
* Glassmorphism
* Glow

---

# 39. Dialog 圆角

默认：

```text
6 px
```

不使用：

```text
999
```

---

# 40. Shadow

当前项目没有明显统一 Shadow 系统。

本阶段原则：

> 不主动引入强阴影系统。

如果需要层级：

优先使用：

```text
Background
Border
Separator
```

而不是：

```text
Shadow
```

---

# 41. Gradient

禁止新增大面积 Gradient。

允许极少数情况下用于：

* 特殊视觉状态
* 极小范围装饰

但普通：

* Button
* Header
* Card
* List
* Background

不得使用 Gradient。

---

# 42. Glow

禁止。

包括：

* Button Glow
* Border Glow
* Selected Glow
* Icon Glow
* Status Glow

---

# 43. Glassmorphism

禁止。

包括：

* Blur
* Frosted Glass
* 半透明大卡片
* 背景模糊

HD2Mimnt 不采用此设计语言。

---

# 44. 信息密度

目标：

> 在不降低可读性的情况下，提高有效信息密度。

尤其是：

* Profile
* Mod Library
* Profile Mod
* Task Center
* Plugin Management

不要为了所谓“呼吸感”产生大量空白。

---

# 45. 宽度策略

禁止无意义的：

```xml
MaxWidth="760"
```

或：

```xml
MaxWidth="860"
```

作为所有页面内容的统一限制。

页面宽度应该根据页面性质决定。

---

# 46. 页面宽度分类

## 列表型页面

例如：

* Profile
* Library
* Task

允许：

```text
尽量利用可用宽度
```

---

## 设置型页面

可以存在合理最大宽度。

但是：

> 不允许在宽窗口中出现巨大空白。

---

## Detail / Dialog

可以使用明确最大宽度。

---

# 47. 高度策略

禁止通过固定 Height 强制限制主要内容区域。

优先：

```text
Grid *
ScrollViewer
Auto
```

固定尺寸只用于：

* Icon
* Button
* ToolBar
* 小型 Overlay
* 明确规格的 Dialog

---

# 48. Responsive 行为

桌面窗口改变大小时：

必须保证：

* 主内容区域正常扩展
* 列表正常扩展
* Detail 区域正常变化
* 不出现巨大空白
* 不出现内容挤压
* 不出现固定宽度导致页面失衡

---

# 49. MainWindow 全局结构

保持现有：

```text
Top Navigation
Content
Bottom Status
```

结构原则。

但视觉上：

```text
Top Navigation
───────────────
Content
───────────────
Status
```

而不是：

```text
[圆角导航卡片]
[巨大内容卡片]
[圆角状态卡片]
```

---

# 50. Navigation

主导航保持安静。

导航只需要：

```text
页面名称
当前选中状态
```

不要把导航做成：

```text
一排胶囊按钮
```

---

# 51. Bottom Status

Bottom Status 应保持：

* 紧凑
* 低视觉优先级
* 小字号
* 简单状态

不要变成：

> 第二个 Dashboard。

---

# 52. Global Theme

Light / Dark Theme 必须保持：

```text
相同视觉结构
相同层级
相同圆角
相同 spacing
相同 typography
```

只改变：

```text
颜色
```

不应该：

> Light Theme 一套 UI，Dark Theme 另一套 UI。

---

# 53. Color 使用原则

颜色主要表达：

```text
Primary
Success
Warning
Error
Information
Disabled
```

而不是装饰。

---

# 54. Accent Color

Accent Color 用于：

* 当前选中
* Primary Action
* Focus
* Link
* 重要状态

禁止全屏大量使用 Accent。

---

# 55. Success

Success 用于：

* Enabled
* Deployed
* Completed
* Valid

不要把所有绿色文字都视为 Success。

---

# 56. Warning

Warning 用于：

* Potential Issue
* Missing Dependency
* Unusual State

---

# 57. Error

Error 用于：

* Failed
* Invalid
* Missing Required File
* Deployment Error

---

# 58. Disabled

Disabled 必须：

* 降低对比度
* 不抢注意力

但必须保持最低可读性。

---

# 59. Focus

所有可交互控件必须保留清晰 Focus State。

不要为了“干净”而完全删除 Focus Visual。

---

# 60. Hover

Hover 应该：

> 微弱。

不要：

* 放大
* 弹跳
* 发光
* 改变布局

推荐：

```text
轻微 Background
或
轻微 Border
```

---

# 61. Selection

Selection 比 Hover 更明显。

例如：

```text
Hover
    轻微背景变化

Selected
    明确背景变化
    + Accent Indicator
```

---

# 62. ToolTip

Icon-only Button 必须提供 ToolTip。

例如：

```text
刷新
删除
更多
关闭
```

不要让用户猜图标含义。

---

# 63. 本阶段必须统一清理的现有问题

根据 Phase 0.5 Audit：

必须重点处理：

### A

```text
App.axaml
Primary Button CornerRadius=999
```

改为标准 Button Radius。

### B

```text
Secondary Button CornerRadius=999
```

改为标准 Button Radius。

### C

```text
Icon Button CornerRadius=999
```

改为标准 Icon Button Radius。

### D

```text
Badge CornerRadius=999
```

重新区分：

```text
Status Badge
普通 Label
```

仅真正适合 Pill 的状态保留。

### E

Library 中的状态标签：

重新判断：

```text
Enabled
Deployed
```

是否真的需要 Pill。

### F

Plugin Management 中大量 999：

统一重新归类。

### G

Permission Dialog 中：

```text
Risk Badge
Mode Badge
Action Button
```

分别采用不同视觉等级。

---

# 64. 不允许机械式全局替换

禁止：

```text
搜索 999
全部替换成 4
```

必须逐个判断组件语义。

例如：

```text
Primary Button
Status Badge
Circular Indicator
```

三者不能机械使用相同规则。

---

# 65. Global Resource 建议结构

全局资源应逐步形成类似：

```text
Theme
├── Colors
├── Brushes
├── Typography
├── Spacing
├── CornerRadius
├── Button
├── Input
├── List
├── Navigation
├── Status
└── Overlay
```

具体 Resource Key 必须遵循项目当前命名体系。

不要为了“重构漂亮”而大规模改变代码架构。

---

# 66. Resource 命名要求

命名必须：

* 清晰
* 稳定
* 可复用
* 不与页面绑定

不要出现：

```text
LibrarySpecialButtonBrush
ProfileCoolBackground
MoreCardRadius
```

应该表达：

```text
PrimaryButtonBrush
SecondaryButtonBrush
SelectedItemBrush
SurfaceBrush
BorderBrush
```

---

# 67. Style Scope

优先级：

```text
Global Style
    ↓
Component Style
    ↓
Page-specific Style
```

只有确实需要特殊视觉表现时，才允许页面局部覆盖。

不要在每个页面复制一套 Button Style。

---

# 68. Page-specific Override

允许，但必须满足：

1. 有明确视觉原因
2. 不违反全局设计原则
3. 不复制整套全局 Style
4. 不重新定义基础 CornerRadius
5. 不重新定义 Typography System

---

# 69. 本阶段对现有页面的处理方式

本阶段只处理：

> 全局视觉基础。

如果发现某页面：

```text
Border 太多
布局错误
内容层级错误
工具栏位置错误
列表结构错误
```

记录为：

```text
Phase 2~7 Page Redesign
```

不要在 Phase 1 顺手改掉。

---

# 70. 页面重构顺序

完成 Phase 1 后：

```text
Phase 2
Profiles

Phase 3
Profile Mod Workspace

Phase 4
Mod Library

Phase 5
Settings

Phase 6
More

Phase 7
Task / Notification / Dialog / Overlay

Phase 8
Global Cleanup + Final Visual Acceptance
```

---

# 71. 为什么先做 Profiles

启动后用户最需要知道：

> 我当前到底在使用什么？

因此 Profiles 是核心工作区。

它不是普通页面。

它承担：

```text
当前 Profile
Profile 切换
Profile 列表
Profile 使用的 Mod
Profile 状态
```

因此 Phase 2 将作为第一个完整页面重构。

---

# 72. Library 的视觉定位

Library 不表示：

> 当前正在使用的 Mod。

它表示：

> 我拥有的 Mod。

因此后续 Library 页面必须让：

```text
存在于 Library
```

和：

```text
当前 Profile 正在使用
```

成为两个清晰概念。

---

# 73. Profile Mod 的视觉定位

Profile Mod Workspace 必须明确：

```text
当前 Profile
    ↓
使用的 Mod
    ↓
该 Mod 在当前 Profile 中的配置
```

特别是：

> Options 必须表现为 Profile-scoped。

---

# 74. 本阶段禁止的视觉风格

以下全部禁止作为新的全局设计方向：

```text
Glassmorphism
Neumorphism
Cyberpunk UI
Dashboard UI
SaaS UI
Mobile UI
大量 Pill
大量 Card
大量 Emoji
巨大 Icon
渐变背景
Glow
强 Shadow
大圆角
漂浮卡片
动画卡片
营销式 Hero Section
巨大统计数字
装饰性插画
```

---

# 75. 本阶段允许的视觉特征

允许：

```text
Flat
Compact
Desktop-native
Information-dense
Clean
Quiet
Functional
Structured
Consistent
```

---

# 76. 验收标准

Phase 1 完成后必须满足：

## 圆角

全局普通 Button 不再使用：

```text
999
```

普通控件统一在：

```text
2~6 px
```

范围内。

---

## Card

不能再出现：

> 所有内容都有圆角 Border 的感觉。

---

## Button

必须存在清晰：

```text
Primary
Secondary
Tertiary
Danger
Icon
Navigation
```

层级。

---

## List

必须具备统一：

```text
Normal
Hover
Selected
Disabled
```

状态。

---

## Typography

页面标题、Section、Body、Secondary、Caption 有明确层级。

---

## Spacing

不再出现大量随意数字。

主要间距应该来自：

```text
4 / 8 / 12 / 16 / 20 / 24 / 32
```

---

## Icon

不得继续扩大 Emoji / Unicode Icon 的混用范围。

---

## Animation

不得出现装饰性动画。

---

## Surface

Surface0/1/2/3 的职责明确。

---

## Theme

Light / Dark 两套主题结构一致。

---

## Width

不得新增无意义的固定 MaxWidth。

---

# 77. 编译验证

完成修改后执行项目现有验证流程。

至少验证：

```text
dotnet build
```

以及项目现有测试命令。

不得因为 UI 修改导致：

* 编译错误
* XAML 解析错误
* Resource 找不到
* Style 找不到
* DynamicResource 找不到
* 页面无法加载

---

# 78. UI 回归检查

必须至少检查：

```text
MainWindow
Library
Profile
Settings
More
Task Center
Notification
Dialog
Plugin Management
Backup
Log
```

确认：

* 页面可以正常打开
* 控件可以正常交互
* Button 状态正常
* ListBox 正常
* ScrollViewer 正常
* Theme 正常
* Focus 正常
* Hover 正常
* Selected 正常
* Dialog 正常

---

# 79. 本阶段禁止截图式“假验收”

不能仅凭：

```text
Build Passed
```

宣布 UI Phase 1 完成。

必须确认：

> 全局 Style 已经真正作用于现有页面。

---

# 80. 输出要求

完成后生成：

```text
UI_Phase1_GlobalVisualSystem.md
```

至少记录：

1. 修改了哪些全局 Resource
2. 修改了哪些 Global Style
3. CornerRadius 最终规则
4. Surface 最终规则
5. Button 最终规则
6. Typography 最终规则
7. Spacing 最终规则
8. List 状态规则
9. Icon 策略
10. Animation 策略
11. Light/Dark Theme 变化
12. 哪些页面发现问题但留到后续 Phase
13. 编译结果
14. 测试结果
15. UI 回归结果

---

# 81. Git 修改范围检查

完成后检查 Git Diff。

必须确认：

> 本阶段主要修改集中于全局 UI 资源与 Style。

如果出现以下文件大量修改：

```text
Database
Infrastructure
Application
Domain
Deployment
PatchTool
Mod Parser
Profile Service
Library Service
```

必须停止并检查原因。

---

# 82. 最终原则

本阶段最重要的一条：

> 不要为了让 UI “看起来更现代”，继续往 UI 上堆东西。

HD2Mimnt 的视觉目标不是：

> 第一眼很炫。

而是：

> 第一次打开就知道东西在哪里，用几次以后几乎不用思考。

最终 UI 应该让用户感觉：

```text
这是一个 Windows 桌面工具。

它很干净。

它很直接。

它信息很多，但不乱。

它没有多余的装饰。

它的每一个按钮、列表、状态和区域都有明确用途。
```

---

# Phase 1 完成条件

只有同时满足以下条件，才允许进入 Phase 2：

[ ] 全局 CornerRadius 系统完成
[ ] 全局 Surface 系统完成
[ ] Button 系统完成
[ ] Typography 系统完成
[ ] Spacing 系统完成
[ ] List 状态系统完成
[ ] Icon 使用策略完成
[ ] Animation 策略完成
[ ] Light/Dark Theme 完成
[ ] 现有页面没有明显 Style 回归
[ ] dotnet build 通过
[ ] 现有测试结果已记录
[ ] Git Diff 检查完成
[ ] UI_Phase1_GlobalVisualSystem.md 已生成

完成以上所有项目后：

> 停止。

不要直接进入 Phase 2。

等待下一阶段执行指令。

```