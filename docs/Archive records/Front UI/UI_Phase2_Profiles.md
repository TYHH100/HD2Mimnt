# HD2Mimnt Phase 2 Profiles 页面实施报告

## 1. 范围

本次仅处理 Profiles 页面及其直接相关的 ViewModel 状态。附带的《Phase 2 Profiles 页面完整重构》是本次实施依据，不是代码文件中的运行时配置。

已修改：

- `src/HD2Mimnt.UI/Views/ProfileView.axaml`
- `src/HD2Mimnt.UI/ViewModels/ProfileViewModel.cs`

未修改：

- `App.axaml` 和全局主题资源
- Library 页面
- Profile/Mod 数据模型、SQLite 结构、部署和清理逻辑
- Application 服务的既有业务语义
- `ProfileView.axaml.cs`（本页不需要 code-behind 逻辑）

## 2. 页面结构

- 顶部保留紧凑的 `Profiles` 标题、说明、新建入口。
- 左侧为 Profile 工作区导航，包含搜索、Profile 列表、当前标识和创建、复制、删除、设为当前操作。
- 右侧为选中 Profile 的工作区，包含 Profile 信息、Profile Mod 列表、从 Mod Library 添加入口和从 Profile 移除入口。
- 未选择 Profile、无 Profile、搜索无结果、加载失败、Profile Mod 加载中和 Profile 无 Mod 都有独立状态。
- Compare 保留为右侧工作区的次级状态，通过“比较”进入、通过“关闭比较”退出，不作为默认主页内容。

## 3. 业务语义

- Library 中的 Mod 通过现有 `ModService` 读取，只在“从 Mod Library 添加”选择器中出现。
- 添加使用现有 `ProfileService.AddModToProfileAsync`，只建立当前 Profile 与 Library Mod 的关系。
- “从 Profile 移除”调用现有 `ProfileService.RemoveModFromProfileAsync`，不会删除 Library Mod。
- 启用状态通过现有 `SetModEnabledAsync` 保存，并使用单向 `CheckBox` 绑定，避免控件双向写入后再次反转。
- 删除 Profile 仍由现有 ProfileService 和确认流程处理，确认文案明确说明不会删除模组库中的 Mod。
- `SelectedProfile` 与 `IsActive` 保持分离：用户可以查看非当前 Profile，当前工作 Profile 仍由现有激活命令决定。

## 4. Profile Options 边界

当前 Core/Application 数据模型只有 Mod 的 manifest 选项，没有 Profile 级选项存储、读取和保存接口。本次没有新增数据库字段、临时 ViewModel 状态或伪造 Configure 命令，因此页面只表达“启用状态和后续配置属于当前 Profile”的语义，没有把 Library Mod 的全局设置误显示为 Profile Options。

后续要实现真正的 Profile-specific Options，需要先在业务层定义 Profile Mod 选项的持久化边界，再由页面接入；这不属于本阶段的 UI 最小改动范围。

## 5. 视觉与响应式约束

- 左栏使用 `260px` 起始宽度并限制在 `320px` 内，右侧使用剩余空间。
- 列表使用扁平分隔行，避免把每个 Profile 或 Mod 做成多层卡片。
- 未新增渐变、发光、阴影或 `CornerRadius=999`。
- 操作按钮使用既有 Button Classes；移除动作使用明确的“从 Profile 移除”文案。
- Profile 和 Mod 列表使用虚拟化面板，避免大数据量下创建全部行。

## 6. 验证

已执行：

- `dotnet build HD2Mimnt.slnx --no-restore /m:1`
  - 结果：成功，0 个错误，9 个既有警告。
- `dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --filter "FullyQualifiedName~ProfileViewModelTests" --verbosity normal`
  - 结果：2/2 通过。
- `dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --verbosity normal`
  - 结果：479/487 通过，8 项 `ImageCacheServiceTests` 因既有 Avalonia Headless 跨线程初始化异常失败；失败堆栈位于 `ImageCacheServiceTests` 的静态 Headless 初始化，不涉及本次 Profiles 文件。

尚未在本次命令行验证中完成桌面人工回归，因此窗口缩放、鼠标选择、键盘焦点和真实数据下的视觉验收仍需在 Avalonia 应用中完成。自动化验证通过不等同于人工视觉验收完成。

## 7. 未完成项目

1. Profile-specific Options 的业务模型和持久化能力尚不存在，页面没有虚构该能力。
2. 需要在桌面应用中人工验证不同窗口宽度、添加/移除、切换当前 Profile、Compare 进出和错误状态呈现。

## 8. 外部参考

- [Avalonia Grid 布局文档](https://docs.avaloniaui.net/docs/basics/user-interface/controls/layout)
- [Avalonia ListBox 文档](https://docs.avaloniaui.net/docs/reference/controls/listbox)
- [Avalonia 数据绑定文档](https://docs.avaloniaui.net/docs/basics/data/data-binding)

## Phase 2.1 修正

### 修正内容

1. **Empty State 显示问题**：将 `SelectedProfile`、Profile Mod 加载中、内容、空列表和错误状态拆成显式 ViewModel 属性，并让对应 XAML 状态互斥。Selected Profile 存在且 Mod 已加载时只显示 Mod 列表，不再显示“选择一个 Profile”。
2. **Current 截断问题**：Profile Row 为 Current 保留稳定的 `52px` 状态列，名称和描述使用剩余空间并继续截断，避免 Current 文本被内容列挤压。
3. **重复 Current 信息**：保留列表行的“当前”和 Profile Header 的“当前工作 Profile”，移除工作区底部重复显示完整 Profile 名称的 Current 文本；全局状态栏保持不变。
4. **错误状态**：增加 Profile Mod 加载错误状态和独立重试命令，避免加载失败时误显示空列表。

### 业务与文件边界

- 未修改 `SelectedProfile` 与 `IsActive` 的业务语义。
- 修改了 `ProfileViewModel.cs`，仅增加页面状态表达、重试和状态通知。
- 未修改 Code-behind、Profile/Mod 模型、数据库、服务、全局主题或其他页面。
- 未实现 Profile-specific Options，也未增加新的业务功能。

### 人工验收

- 已启动 Avalonia 应用并进入 Profiles 页面。
- 默认窗口尺寸：已确认存在 Profile 时不显示“选择一个 Profile”，有 Mod 时只显示 Mod 列表，Current 文本完整可见，工作区底部不再重复显示 `Current: Profile`。
- 较宽窗口（最大化）：已确认左右工作区保持结构，Profile 行 Current、Mod 名称、启用和移除操作没有明显裁切或重叠。
- 当前测试数据只有一个带 Mod 的 Profile，未在本次验收中新增或删除用户 Profile，因此切换 Profile、无 Mod Profile 和无 Selected Profile 三个场景未能用真实数据复现。

### Phase 2.1 验证结果

- `dotnet build HD2Mimnt.slnx --no-restore /m:1`
  - 结果：成功，0 个错误；仅有仓库现存分析器和平台兼容性警告。
- `dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --verbosity normal`
  - 结果：执行后仍为 479/487 通过，8 项 `ImageCacheServiceTests` 因既有 Avalonia Headless 跨线程初始化异常失败；未扩大范围修复。
- 本次最终重跑全量测试和 Profiles 定向测试时，Windows 应用程序控制策略拦截了测试程序集加载（`0x800711C7`），因此未形成新的测试执行结果；代码构建已独立通过。
- `git diff --stat` 和 `git diff --check`
  - 结果：已检查；本阶段代码修改仅涉及 Profiles View、ProfileViewModel 和本报告，工作区另有用户预先存在的文档归档/删除变更。
