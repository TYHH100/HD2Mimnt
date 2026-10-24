# HD2Mimnt — Phase 2 Profiles 页面完整重构执行文档

> 项目：HD2Mimnt
> 阶段：Phase 2
> 目标：完整重构 Profiles / ProfileView 页面
> 前置阶段：Phase 0、Phase 0.5、Phase 1 已完成
>
> 本阶段不是简单换颜色、调整 Padding 或修改 CornerRadius。
> 本阶段需要按照已经确定的 HD2Mimnt 桌面工具视觉体系，对 Profiles 页面进行完整的信息架构与视觉结构重构。
>
> --------------------------------------------------
> 一、执行原则
> --------------------------------------------------

1. 本阶段只处理 Profiles 页面及其直接相关的 UI。
2. 不重新设计 App.axaml 全局视觉系统。
3. 不重新设计 Library 页面。
4. 不修改 Mod Library 的业务语义。
5. 不修改 Profile 数据模型、数据库结构、部署逻辑、清理逻辑、Mod 解析逻辑。
6. 不进行大规模 MVVM 重构。
7. 不因为 UI 重构而改变现有业务行为。
8. 优先复用 Phase 1 已建立的全局 Style、Theme Resource、Spacing、Typography、Button 等资源。
9. 如果现有 ViewModel 已经能够提供所需数据，则优先直接使用现有数据。
10. 如果确实缺少 UI 所需的最小状态或命令，可以进行最小范围的 ViewModel 调整，但不得借此重写业务架构。

本阶段核心原则：

    先确定信息结构
    ↓
    再确定布局
    ↓
    再实现视觉层
    ↓
    最后验证交互与状态

不要反过来先堆 Border、Card 和按钮。

--------------------------------------------------
二、必须保持的核心业务语义
--------------------------------------------------

Profiles 页面必须严格遵循以下关系：

    Mod Library
        ↓
    Profile
        ↓
    Profile Mod
        ↓
    Profile-specific Options

具体含义：

### Mod Library

表示用户已经导入、管理、保存的 Mod 资源。

Library 中存在的 Mod：

- 不代表当前正在使用
- 不代表所有 Profile 都使用
- 不代表当前 Profile 正在使用
- 不应该因为存在于 Library 就显示成“当前启用”

### Profile

表示一个独立的 Mod 配置环境。

例如：

Profile A
    Mod 1
    Mod 2
    Mod 3

Profile B
    Mod 1
    Mod 4

Profile A 与 Profile B 必须保持独立。

### Profile Mod

表示：

“这个 Profile 当前使用了这个 Library Mod。”

它不是一个新的独立 Mod。

同一个 Library Mod 可以被多个 Profile 使用。

例如：

    Library
        └── Mod A

    Profile A
        └── Mod A

    Profile B
        └── Mod A

这两个 Profile 对 Mod A 的使用状态必须彼此独立。

### Profile-specific Options

Mod 的配置选项必须属于具体 Profile。

例如：

Profile A
    Mod A
        Option X = 1

Profile B
    Mod A
        Option X = 3

不能把 Profile A 修改 Option X 的行为解释成修改 Library 中 Mod A 的全局设置。

--------------------------------------------------
三、Profiles 页面最终回答的问题
--------------------------------------------------

用户进入 Profiles 页面以后，必须能够非常快速地回答：

1. 我有哪些 Profile？
2. 当前正在使用哪个 Profile？
3. 当前 Profile 有多少 Mod？
4. 当前 Profile 使用了哪些 Mod？
5. 我能否快速切换 Profile？
6. 我能否创建、复制、重命名、删除 Profile？
7. 我能否查看当前 Profile 的 Mod？
8. 我能否进入某个 Profile Mod 的配置？
9. 当前 Profile 是否存在异常状态？

不要增加与这些问题无关的 Dashboard 信息。

不要增加：

- CPU
- 内存
- 游戏统计
- Mod 总市场数据
- 图表
- 大型统计卡
- 装饰性数据
- 与 Profile 工作流无关的“概览”

Profiles 是工作区，不是 Dashboard。

--------------------------------------------------
四、最终页面整体结构
--------------------------------------------------

Profiles 页面采用：

    页面标题区域
    ─────────────────────────────

    左侧 Profile 列表
    │
    │        当前 Profile 工作区
    │        ─────────────────
    │        Profile 信息
    │
    │        Profile Mod 列表
    │
    │        操作区域
    │
    └──────────────────────────

推荐整体结构：

    Grid
        Column 0 = Profile List
        Column 1 = Current Profile Workspace

不要使用三列复杂 Dashboard。

不要再增加第三个“统计栏”。

不要使用大量 Card。

--------------------------------------------------
五、页面顶部
--------------------------------------------------

页面顶部只保留必要信息。

建议结构：

    Profiles
    管理独立的 Mod 配置文件

右侧可以放置：

    New Profile

如果现有页面已有创建入口，则统一为 Phase 1 的 Primary Button。

顶部不要放：

- 大型统计卡
- Profile 数量大数字
- 当前部署状态 Dashboard
- 装饰性图标
- 大型 Banner

页面标题应该保持紧凑。

--------------------------------------------------
六、左侧 Profile 列表
--------------------------------------------------

左侧是 Profile 工作区的核心导航。

建议：

    宽度约 260–320 px

不要使用固定死的单一宽度，如果当前窗口允许，应使用合理的 MinWidth / MaxWidth。

整体结构：

    Profile
    ─────────────────
    Search
    ─────────────────
    Profile Row
    Profile Row
    Profile Row
    Profile Row

不要使用：

    Card
    Card
    Card

不要做成卡片网格。

Profile 必须是“列表项”。

--------------------------------------------------
七、Profile Row 结构
--------------------------------------------------

每一个 Profile Row 至少应该包含：

    Profile Name
    Mod Count
    当前状态

例如：

    Default
    12 mods

    Testing
    7 mods

当前 Profile：

    Default
    12 mods
    Current

“Current”是状态，不应该使用巨大的 Badge。

不要使用：

    CornerRadius=999

不要制作胶囊型巨大标签。

当前状态可以通过：

- 左侧 Accent indicator
- 背景轻微变化
- 小型状态文本

三者之一或组合表达。

优先使用：

    Accent indicator + subtle background

而不是：

    巨型绿色 Current pill

--------------------------------------------------
八、Profile Row 交互状态
--------------------------------------------------

Profile Row 必须至少具有：

1. Normal
2. Hover
3. Selected
4. Current

其中：

Selected：

表示当前 UI 正在查看该 Profile。

Current：

表示该 Profile 是当前实际工作 Profile。

如果当前选择的 Profile 就是 Current Profile：

    Selected + Current

必须能够同时成立。

不要把 Selected 和 Current 混为一个状态。

例如：

用户查看 Profile B，
但当前实际 Profile 是 Profile A。

此时：

Profile A = Current
Profile B = Selected

右侧应该显示 Profile B 的内容。

但部署操作仍然必须按照现有业务逻辑作用于 Current Profile。

如果现有业务逻辑规定“选择即当前”，则保持现有业务语义，不要为了视觉模型自行改变行为。

必须以现有 ViewModel / Service 行为为准。

--------------------------------------------------
九、Profile 列表底部操作
--------------------------------------------------

Profile 列表底部可以提供：

    New
    Copy
    Rename
    Delete

但不要同时显示四个巨大按钮。

推荐：

    + New Profile

以及选中 Profile 后的：

    More / ...

或者紧凑的文字按钮。

如果现有业务已经有明确的 Copy / Rename / Delete Command：

必须保留功能。

视觉上采用紧凑工具栏。

不要制作：

    [CREATE PROFILE]
    [COPY PROFILE]
    [RENAME PROFILE]
    [DELETE PROFILE]

四个大按钮纵向排列。

--------------------------------------------------
十、右侧 Profile 工作区
--------------------------------------------------

右侧是页面主体。

结构：

    Profile Header
    ─────────────────────────

    Profile Mods
    ─────────────────────────

    Mod List

    ─────────────────────────
    Bottom Actions

不要把右侧整个区域包成一个巨大 Card。

右侧应该是“工作区”。

可以使用：

- Border bottom separator
- Surface0 / Surface1
- thin border
- spacing

但是不要：

    巨大 Surface2 Card
        └── Card
            └── Card
                └── Card

--------------------------------------------------
十一、Profile Header
--------------------------------------------------

Profile Header 至少包含：

    Profile Name
    Mod Count
    当前状态
    必要操作

例如：

    Default

    12 mods · Current

右侧操作：

    Rename
    Copy
    Delete

或者通过 More 菜单提供。

如果存在当前 Profile：

    Current Profile

可以使用小型状态文本。

不要制作：

    CURRENT PROFILE
    12
    MODS
    ACTIVE

四个大型 Dashboard 数字块。

--------------------------------------------------
十二、Profile Mod 列表
--------------------------------------------------

这是右侧最重要的区域。

必须明确表示：

“这些 Mod 是这个 Profile 当前正在使用的 Mod。”

不要让用户误认为这是整个 Library。

列表项应该至少显示：

    Mod Name
    Version
    Author（如果现有数据提供）
    Profile-specific 状态 / 配置状态
    操作入口

可以根据现有数据决定是否显示：

    Category
    Version
    Author

但不要为了填充空间而强制显示所有字段。

--------------------------------------------------
十三、Profile Mod Row
--------------------------------------------------

推荐结构：

    [Mod Icon]  Mod Name
                Version / Author / secondary information

                         [Status] [More]

不要使用大卡片。

每一项之间使用：

    1px separator

或者非常轻的间距。

不要给每一个 Mod 单独添加：

    Border
    Shadow
    Radius 8
    Surface2

导致“卡片套卡片”。

--------------------------------------------------
十四、Profile Mod 与 Library Mod 的视觉区分
--------------------------------------------------

必须明确：

Library：

    “我拥有的 Mod”

Profile：

    “这个 Profile 使用的 Mod”

因此 Profile Mod 行不应该再次显示类似：

    Global Library
    Installed

这种会制造歧义的状态。

如果需要显示状态，应优先表达：

    Enabled
    Disabled
    Configured
    Has Options
    etc.

但必须以当前业务模型已有状态为准。

不要凭空创造新的业务状态。

--------------------------------------------------
十五、添加 Mod 到 Profile
--------------------------------------------------

如果当前系统已经存在“从 Library 添加到 Profile”的功能：

必须在 Profile 页面提供清晰入口。

推荐：

    Add Mod

点击后可以：

1. 打开现有 Mod Library 选择器
2. 打开现有选择对话框
3. 使用已有 Profile Mod 添加流程

不要复制一套新的 Mod Library 管理系统。

Profile 页面只需要表达：

    Add from Library

不要允许用户误以为这里创建的是新的全局 Mod。

--------------------------------------------------
十六、从 Profile 移除 Mod
--------------------------------------------------

这是一个非常重要的语义。

“从 Profile 移除 Mod”

不等于：

“从 Mod Library 删除 Mod”。

因此 UI 必须明确区分。

例如操作菜单：

    Remove from Profile

而不是：

    Delete Mod

除非当前操作确实是全局删除。

这是必须遵守的语义规则。

--------------------------------------------------
十七、Profile Mod Options
--------------------------------------------------

如果某个 Mod 有可配置选项：

Profile 页面可以提供：

    Configure

或者：

    Options

进入该 Mod 的 Profile-specific 配置。

必须明确：

    这些配置属于当前 Profile。

不要在 UI 中写成：

    Mod Global Settings

不要让用户误以为修改的是 Library Mod 本身。

如果当前系统已有 ModDetailPanel / Mod Options 相关组件：

优先复用现有组件。

不要重新实现另一套 Mod 配置体系。

--------------------------------------------------
十八、空状态
--------------------------------------------------

必须重新设计 Profile 空状态。

### 没有任何 Profile

应该表达：

    No Profiles

    Create a profile to start managing
    an independent mod configuration.

    [Create Profile]

不要使用大型 Emoji。

不要使用：

    📦
    🛠️
    🚀

等 Emoji 作为主要 UI 图标。

### Profile 存在，但没有 Mod

应该表达：

    No Mods in this Profile

    Add mods from your Mod Library.

    [Add Mod]

这两个空状态必须区分。

不要把：

    没有 Profile

和：

    Profile 没有 Mod

混成同一个 EmptyState。

--------------------------------------------------
十九、错误状态
--------------------------------------------------

错误状态必须保持紧凑。

例如：

    Unable to load profile

    [Retry]

不要制作巨大红色 Card。

错误颜色只用于真正的错误。

--------------------------------------------------
二十、删除 Profile
--------------------------------------------------

删除操作必须保持现有业务逻辑。

UI 层需要明确告诉用户：

    删除的是 Profile

而不是：

    删除 Library 中的 Mod

如果删除 Profile 会同时清理其 Profile-specific 数据：

应该在确认对话框中明确说明。

如果当前后端已经定义了删除语义：

严格按照现有语义显示。

不要自行改变删除行为。

--------------------------------------------------
二十一、复制 Profile
--------------------------------------------------

如果现有功能支持复制：

Copy Profile 应该保持语义：

    复制 Profile 配置

而不是：

    复制 Library Mod

复制后的 Profile 应该成为独立 Profile。

UI 不需要展示复杂动画。

--------------------------------------------------
二十二、Profile 切换
--------------------------------------------------

Profile 切换必须保持高响应。

不要因为页面视觉重构而增加：

- 页面级动画
- 大型 Loading Overlay
- 延迟式切换
- 卡片滑动
- 页面淡入淡出

如果切换 Profile 涉及异步加载：

只在真正需要时显示局部状态。

推荐：

    Profile row selection
        ↓
    immediate UI selection
        ↓
    async data refresh
        ↓
    update Profile Mod list

不要整个页面闪白。

--------------------------------------------------
二十三、部署语义
--------------------------------------------------

必须保持：

Deployment operates on the current profile.

Profiles 页面可以提供：

    Deploy
    Clean

但这些操作必须明确作用于：

    Current Profile

如果当前选择 Profile 与 Current Profile 不同：

不要让用户误以为：

    Deploy

会部署 Selected Profile。

如果现有系统规定“切换 Profile 后即成为 Current”：

保持现有业务逻辑。

如果 Selected 与 Current 可以不同：

UI 必须明显区分两者。

--------------------------------------------------
二十四、底部操作栏
--------------------------------------------------

如果现有 Profiles 页面已经提供：

    Switch
    Deploy
    Clean

等操作：

必须重新组织视觉优先级。

推荐：

    左侧：
        当前状态

    右侧：
        Primary Action
        Secondary Actions

例如：

    Current: Default

                         Deploy
                         Clean

不要让所有按钮视觉权重完全一样。

Primary Action 使用 Phase 1 Primary Button。

Secondary 使用 Secondary / Tertiary。

危险操作使用 Danger。

--------------------------------------------------
二十五、禁止 Dashboard 化
--------------------------------------------------

本阶段严禁出现以下视觉结构：

- 四个统计 Card
- 大型数字
- KPI
- 环形进度
- 图表
- 巨大 Current Profile Card
- Profile 卡片网格
- 彩色渐变
- Glow
- Glassmorphism
- 大型阴影
- SaaS Dashboard 风格
- Web Admin 风格
- 巨型圆角容器
- 大面积空白
- 装饰性插画

HD2Mimnt 是 Windows 桌面工具。

最终视觉必须更接近：

    文件管理器
    配置工具
    IDE 工具窗口
    专业桌面应用

而不是：

    Web Dashboard
    SaaS Admin Panel

--------------------------------------------------
二十六、CornerRadius
--------------------------------------------------

严格使用 Phase 1 的 CornerRadius 体系。

优先：

    0
    2
    4
    6

Profile Row 推荐：

    0–4

输入框：

    使用 Phase 1 全局 Style

按钮：

    使用 Phase 1 全局 Button Style

禁止新增：

    CornerRadius="999"

除非确实是：

- 圆形按钮
- 明确的圆形视觉元素
- Phase 1 已允许的特殊语义

不要重新制造 Pill UI。

--------------------------------------------------
二十七、Surface 使用
--------------------------------------------------

Profile 页面应减少 Surface 嵌套。

推荐：

    Page
        Surface0

    Toolbar
        Surface1

    List / Workspace
        Surface0 / Surface1

    Selected Row
        subtle Accent background

避免：

    Surface2
        Surface3
            Surface2
                Surface3

如果一个区域只是为了“看起来像卡片”而添加 Surface：

删除。

--------------------------------------------------
二十八、Spacing
--------------------------------------------------

使用 Phase 1 已建立的 Spacing。

优先：

    4
    8
    12
    16
    20
    24
    32

Profile 列表行不要过高。

Profile Row 应该具有：

    足够点击面积
    但不浪费垂直空间

目标是：

    信息密度高
    但不会拥挤

--------------------------------------------------
二十九、Typography
--------------------------------------------------

遵循 Phase 1 Typography。

推荐层级：

Page Title
    ↓
Section Title
    ↓
Profile Name
    ↓
Secondary Information
    ↓
Metadata

不要通过巨大 FontSize 制造视觉层级。

不要使用粗体 + 大字号 + Badge + Card 同时强调一个信息。

--------------------------------------------------
三十、图标
--------------------------------------------------

不要新增 Emoji 图标。

禁止新增：

    📦
    ⚙️
    🗑️
    📋
    🔧
    🚀

等 Emoji 作为核心操作图标。

如果项目已有统一 Icon 资源：

优先复用。

如果当前项目尚未建立完整统一 Icon 系统：

本阶段不要为了 Profiles 页面单独引入一个大型 Icon 框架。

可以继续使用现有的文字按钮 / 简单图标资源。

--------------------------------------------------
三十一、响应式布局
--------------------------------------------------

Profiles 页面必须考虑窗口尺寸变化。

至少保证：

### 宽窗口

    Profile List
        +
    Profile Workspace

正常显示。

### 中等窗口

适当压缩：

    Profile List
        +
    Workspace

不要立即产生巨大空白。

### 较窄窗口

如果现有程序支持窗口缩小：

必须避免：

- 内容互相覆盖
- Mod 名称完全不可见
- 操作按钮溢出
- 横向滚动条成为主要操作方式

如果无法实现真正响应式：

至少设置合理 MinWidth。

不要通过固定巨大 Width 强行撑开。

--------------------------------------------------
三十二、不要过度使用 MaxWidth
--------------------------------------------------

Phase 0.5 已经指出 Settings / More 存在 MaxWidth 导致宽屏空白问题。

Profiles 页面不要复制这种问题。

不要：

    MaxWidth="760"

然后把整个工作区压在窗口中央。

Profiles 是桌面工作页面，应优先使用可用宽度。

--------------------------------------------------
三十三、现有 ProfileView 结构参考
--------------------------------------------------

当前 ProfileView 已知结构：

    Grid
        RowDefinitions="Auto,Auto,*"
        Margin="24"

    Grid
        ColumnDefinitions="2*,3*"

左侧：

    Profile list

右侧：

    Detail / compare / Profile Mods

现有 Profile item：

    Surface3
    CornerRadius=6

当前 Profile：

    CornerRadius=4 state label

列表：

    ListBox
    VirtualizingStackPanel

本阶段不要求保留这个具体视觉结构。

可以重构。

但必须保留已有业务功能。

--------------------------------------------------
三十四、Compare 功能
--------------------------------------------------

当前 Profile 页面存在 Compare 功能。

不要因为重构而删除。

但不要让 Compare 成为页面默认视觉主体。

推荐：

    Profile 页面默认：
        Profile List
        Current/Selected Profile Workspace

    Compare：
        作为次级工具状态

可以继续使用：

    Compare

入口。

进入 Compare 后才显示比较区域。

不要让默认页面一打开就是大型比较 Dashboard。

如果现有 Compare 是独立区域：

保持功能，但重新套用 Phase 1 的视觉语言。

--------------------------------------------------
三十五、ViewModel / Code-behind
--------------------------------------------------

不要进行以下操作：

- 重写 MainWindowViewModel
- 重写 ProfileViewModel
- 改数据库
- 改 Profile Service
- 改 Deployment Service
- 改 Mod Library Service
- 改 Navigation 架构
- 把 Code-behind 全部迁移 MVVM
- 引入新的 UI 框架
- 引入新的状态管理框架

本阶段目标是 UI 重构。

如果现有 ProfileView.axaml.cs 存在交互逻辑：

继续使用。

只在确实必要时修改。

--------------------------------------------------
三十六、代码质量要求
--------------------------------------------------

不要为了快速完成而：

- 重复创建 Style
- 在 View 中硬编码大量颜色
- 在 View 中硬编码大量 FontSize
- 创建大量局部 Border 样式
- 创建与 App.axaml 冲突的新 Button Style
- 复制 Phase 1 已存在资源

优先：

    DynamicResource
    Phase 1 global styles
    existing ViewModel properties
    existing commands

--------------------------------------------------
三十七、实现顺序
--------------------------------------------------

严格按照以下顺序执行：

### Step 1 — 阅读现有实现

读取：

    ProfileView.axaml
    ProfileView.axaml.cs
    ProfileViewModel
    Profile 相关 Service
    Profile 相关 Model
    MainWindow 相关导航代码

确认：

- 当前 Profile 列表来源
- 当前选中 Profile
- 当前 Current Profile
- Profile Mods 来源
- Add / Remove 命令
- Copy / Rename / Delete
- Compare
- Deploy / Clean
- Profile Options
- Loading / Error / Empty 状态

不得凭 UI 猜测业务逻辑。

### Step 2 — 绘制当前结构

在脑中明确：

    Page
      ├── Header
      ├── Profile List
      │     ├── Search
      │     ├── Profile Rows
      │     └── Actions
      │
      └── Workspace
            ├── Profile Header
            ├── Profile Mods
            ├── Mod Rows
            └── Actions

### Step 3 — 重构 XAML

先完成结构。

此阶段暂时不要追求细节。

### Step 4 — 套用 Phase 1 Style

统一：

- Button
- Typography
- Surface
- Border
- List
- Selected
- Hover
- Spacing

### Step 5 — 完善状态

实现：

- Empty
- Loading
- Error
- Selected
- Current
- No Mods
- Compare
- Options

### Step 6 — 完整交互验证

验证所有原有命令。

### Step 7 — 编译与测试

至少执行：

    dotnet build

以及现有测试。

不要因为 UI 修改导致现有测试大面积失败。

--------------------------------------------------
三十八、验收标准
--------------------------------------------------

Phase 2 完成必须同时满足：

### A. 信息结构

[ ] 左侧明确是 Profile 列表
[ ] 右侧明确是当前查看 Profile 的工作区
[ ] 用户可以快速识别 Current Profile
[ ] Profile Mod 明确属于当前 Profile
[ ] Library Mod 与 Profile Mod 不混淆

### B. 视觉

[ ] 没有 Dashboard 卡片堆叠
[ ] 没有巨大 Profile Card
[ ] 没有大面积空白
[ ] 没有大圆角 Pill
[ ] 没有 999 CornerRadius 新增
[ ] 没有渐变
[ ] 没有 Glow
[ ] 没有 Glassmorphism
[ ] 没有大型阴影
[ ] 没有 Emoji 核心图标

### C. 桌面工具感

[ ] 列表优先
[ ] 信息密度合理
[ ] 操作直接
[ ] 状态明确
[ ] 窗口扩大后能够利用空间
[ ] 窗口缩小时不会明显破坏布局

### D. 功能

[ ] 创建 Profile
[ ] 选择 Profile
[ ] 切换 Current Profile（如果现有业务支持）
[ ] 复制 Profile
[ ] 重命名 Profile
[ ] 删除 Profile
[ ] 查看 Profile Mods
[ ] 从 Library 添加 Mod
[ ] 从 Profile 移除 Mod
[ ] Profile Mod Options
[ ] Compare
[ ] Deploy / Clean（如果现有 Profile 页面提供）
[ ] Empty State
[ ] Error State
[ ] Loading State

### E. 业务语义

[ ] 删除 Profile 不等于删除 Library Mod
[ ] 从 Profile 移除 Mod 不等于删除 Library Mod
[ ] Profile-specific Options 不被显示成 Global Mod Settings
[ ] Deployment 仍遵循 Current Profile 语义
[ ] Profile 之间保持独立

--------------------------------------------------
三十九、禁止自行扩展
--------------------------------------------------

如果执行过程中发现：

- 某个 UI 可以做成 Card
- 某个地方可以增加统计
- 某个地方可以增加动画
- 某个地方可以增加图表
- 某个地方可以增加快捷入口
- 某个地方可以增加新的 Dashboard 信息

不要自行加入。

本阶段严格按照本文档执行。

如果发现现有业务逻辑与本文档视觉语义存在冲突：

不要自行修改业务逻辑。

记录冲突。

--------------------------------------------------
四十、完成后必须输出报告
--------------------------------------------------

完成后创建：

    UI_Phase2_Profiles.md

报告必须包含：

1. 修改文件列表
2. Profiles 页面最终结构
3. Profile List 结构
4. Profile Workspace 结构
5. Profile Mod 结构
6. Empty / Loading / Error 状态
7. Compare 如何保留
8. Profile-specific Options 如何表达
9. Current Profile 如何表达
10. Library / Profile 语义如何区分
11. 是否修改 ViewModel
12. 是否修改 Code-behind
13. 是否修改业务逻辑
14. 是否修改数据库
15. CornerRadius 使用情况
16. 是否新增 Style
17. Build 结果
18. Test 结果
19. 发现的问题
20. 未完成项目

--------------------------------------------------
四十一、最终 Git Diff 审核
--------------------------------------------------

完成后必须检查：

    git diff --stat

以及：

    git diff

确认修改范围。

如果出现以下文件被无关修改：

- Deployment
- PatchTool
- Mod parser
- Database
- unrelated View
- unrelated ViewModel
- unrelated Service

必须回滚无关修改。

本阶段必须保持严格的修改边界。

--------------------------------------------------
四十二、Phase 2 完成条件
--------------------------------------------------

只有满足以下条件，Phase 2 才算完成：

1. Profiles 页面已经按照新的桌面工具工作区结构完成。
2. Profile 与 Library 的语义清晰。
3. Profile Mod 与 Library Mod 没有视觉混淆。
4. Current Profile 明确。
5. 所有原有 Profile 功能仍然可用。
6. Profile-specific Options 语义保持正确。
7. 没有重新引入 Dashboard / Card-heavy UI。
8. 没有破坏 Phase 1 全局视觉系统。
9. Build 成功。
10. 现有测试结果已记录。
11. Git Diff 已审核。
12. 已生成 UI_Phase2_Profiles.md。

完成以上条件后停止。

不要自动进入 Phase 3。

等待下一阶段指令。