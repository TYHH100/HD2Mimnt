# HD2Mimnt Phase 3.0 Library 专项审计

审计日期：2026-10-04  
审计性质：READ ONLY AUDIT  
审计结论范围：只记录当前实现，不进入 Phase 3 实施，不提出页面设计方案。

## 1. 审计范围

本次检查的文件和调用链如下：

- `src/HD2Mimnt.UI/Views/LibraryView.axaml`
- `src/HD2Mimnt.UI/Views/LibraryView.axaml.cs`
- `src/HD2Mimnt.UI/ViewModels/LibraryViewModel.cs`
- `src/HD2Mimnt.UI/ViewModels/ModItemViewModel.cs`
- `src/HD2Mimnt.UI/Controls/ModDetailPanel.axaml`、`.axaml.cs`
- `src/HD2Mimnt.Application/Services/ModService.cs`
- `src/HD2Mimnt.Application/Services/ProfileService.cs`
- `src/HD2Mimnt.Application/Services/SearchService.cs`
- `src/HD2Mimnt.Application/Services/DeploymentService.cs`
- `src/HD2Mimnt.Core/Entities/Mod.cs`、`Profile.cs`
- `src/HD2Mimnt.Infrastructure/Repositories/ModRepository.cs`、`ProfileRepository.cs`、`DeploymentRepository.cs`
- `src/HD2Mimnt.Infrastructure/Data/HD2MimntDbContext.cs`
- `tests/HD2Mimnt.Tests/ViewModels/LibraryViewModelSelectionTests.cs`
- `benchmarks/HD2Mimnt.Benchmarks/SearchBenchmarks.cs`
- `docs/Archive records/Back-end architecture/PHASE20_BENCHMARK_RESULTS.md`

本次没有修改任何业务代码、XAML、服务、模型、数据库、主题、导航或测试。唯一新增文件为本审计报告。

## 2. Library 当前信息架构

Library 页面是全局 Mod Repository 的展示入口。`LibraryViewModel.LoadModsAsync()` 通过 `ModService.GetAllModsAsync()` 读取全部 Library Mod，再创建 `_allMods` 和 `FilteredMods`。

页面从上到下的当前结构为：

1. 页面标题栏：标题、添加模组、部署方式、部署、清理、取消和更多菜单。
2. 统计筛选栏：全部、已启用、未启用、已部署、未部署、清除筛选。
3. 搜索工具栏：搜索框、分类 ComboBox、排序 ComboBox。
4. 列表头：全选、反选、当前筛选结果数量。
5. Mod 列表：ListBox DataTemplate。
6. 选择后批量操作栏：启用、禁用、删除。
7. 详情面板 Overlay：打开后覆盖整个 Library 内容区域的右侧面板。
8. 部署遮罩：部署或清理期间覆盖 Library 内容。

MainWindow 另有全局顶栏和底部状态栏。Library 页面自身不拥有 Profile 选择器；它通过 `ProfileService.GetActiveProfileAsync()`读取当前 Active Profile。

## 3. LibraryView XAML 结构

### 根节点

- Root：`UserControl`，`x:DataType=vm:LibraryViewModel`。
- 设计尺寸：`d:DesignWidth=1200`、`d:DesignHeight=800`。
- 事件：`KeyDown="OnKeyDown"`。
- 根布局：`Panel` 包含 `Grid` 和两个覆盖层。
- 主 Grid：`RowDefinitions="Auto,Auto,Auto,Auto,*,Auto"`。
- 主 Grid 没有显式 Width、Height、MinWidth、MaxWidth。
- 根节点没有显式 Margin 或 Padding。

### Page Header

- Grid 行：0。
- 外层 Border：`Padding="24,20"`、`Background=Surface2Brush`、底部 1px Border。
- 内部 Grid：`ColumnDefinitions="*,Auto"`。
- 标题：`模组库`，FontSize 24，SemiBold。
- 操作数量：最多同时显示添加模组、部署方法、部署、清理、取消、更多六类控件；取消仅在 `IsDeploying` 时显示。
- 添加模组通过 code-behind `OnImportClicked`打开文件选择器。
- 部署方法 ComboBox 宽度 120，绑定 `DeploymentMethods` 和 `CurrentDeploymentMethod`。
- 部署绑定 `DeployCommand`，清理绑定 `CleanupCommand`，取消绑定 `CancelDeploymentCommand`。
- 更多按钮使用 Unicode `⋮`，Flyout 内含刷新模组库、清除筛选、清空选择。
- Primary/Secondary/Icon 样式来自 App.axaml 全局样式。

### Filter 区域

- Grid 行：1。
- 外层 Border：`Padding="24,16"`、`Background=Surface1Brush`。
- 内部为横向 StackPanel，Spacing 12。
- 五个 RadioButton 均使用 `Classes="filter-button"`、GroupName `FilterGroup`、`FilterCommand`。
- 参数和值：`All`、`Enabled`、`Disabled`、`Deployed`、`NotDeployed`。
- 文本分别绑定 `TotalModCount`、`EnabledCount`、`DisabledCount`、`DeployedCount`、`NotDeployedCount`。
- `IsChecked` 使用 `EqualConverter` 比较 `CurrentFilter`。
- 清除筛选是 Secondary Button，绑定 `ClearFiltersCommand`。
- 这些 RadioButton 在当前实现中承担筛选控制，同时通过 checked 状态表达当前筛选；不是导航项，也不是独立的状态只读指示器。
- 全局样式提供 1px 边框、`Padding=16,8`、4px CornerRadius，选中态使用 SelectedSurface 和 Accent 边框。

### Search 区域

- Grid 行：2。
- 外层 Border：`Padding="24,12"`、`Background=Surface0Brush`、底部 1px Border。
- 内部 Grid：`ColumnDefinitions="*,Auto,Auto"`。
- 搜索输入外层 Border：Surface2、BorderStrong、1px 边框、CornerRadius 6、Padding 12,8。
- 搜索图标：TextBlock Unicode/Emoji `🔍`，FontSize 16。
- TextBox：`x:Name=SearchBox`，绑定 `SearchText`，Placeholder 为 `搜索模组...`，背景透明、无边框、Padding 0。
- 清除搜索按钮：Unicode `✕`，24x24，绑定 `ClearSearchCommand`，仅在 SearchText 非空时可见。
- 分类 ComboBox：`AvailableCategories`、`SelectedCategory`，MinWidth 120，左 Margin 12，Placeholder `分类`。
- 排序 ComboBox：`SortModes`、`CurrentSortMode`，MinWidth 140，左 Margin 12。

### List Header

- Grid 行：3。
- 外层 Border：`Padding="24,12"`、`Background=Surface1Brush`、底部 1px Border。
- 三列：`Auto,Auto,*`。
- CheckBox：绑定 `IsAllSelected` 和 `SelectAllCommand`，显示 `全选`。
- 反选按钮：绑定 `InvertSelectionCommand`。
- 数量文本：绑定 `FilteredModCount`，显示 `共 {0:N0} 个模组`。

### List 区域

- Grid 行：4，占据剩余空间。
- 外层 Border：`Background=Surface0Brush`。
- 同一个 Grid 内并列放置 Empty、Search/Filter Empty、Error、Loading 和 ListBox 状态。
- ListBox 可见性绑定 `HasFilteredResults`，数据源是 `FilteredMods`。
- ListBox 设置 `SelectionMode=Multiple`、`SelectedItem=FocusedMod`、`SelectedItems=SelectedMods`、`SelectionChanged=OnSelectionChanged`。
- ListBox 自定义 ItemsPanel 为 `VirtualizingStackPanel`，未声明 `VirtualizationMode=Recycling`。
- 没有外层 ScrollViewer；ListBox 自身保留内部滚动容器。

### Batch Operation Bar

- Grid 行：5。
- `SelectedCount > 0` 时可见。
- Surface3、顶部 1px Border、Padding 24,12。
- 显示已选择数量以及启用、禁用、删除三个命令。
- 部署和清理不在这个选择操作栏内。

## 4. LibraryViewModel 结构

### 数据集合

- `_allMods`：`List<ModItemViewModel>`，全量内存源。
- `FilteredMods`：`ObservableCollection<ModItemViewModel>`，当前列表集合。
- `SelectedMods`：`ObservableCollection<ModItemViewModel>`，独立的全局选择集合。
- `AvailableCategories`：所有 `ModCategory` 枚举值。

### 状态

- 搜索：`SearchText`、`IsSearching`。
- 筛选：`CurrentFilter`、`SelectedCategory`。
- 排序：`CurrentSortMode`。
- 统计：总数、启用数、未启用数、已部署数、未部署数、筛选结果数、选中数。
- 页面状态：`IsLoading`、`HasError`、`ErrorMessage`、`IsEmpty`、`HasFilteredResults`、`HasNoResults`。
- 详情：`FocusedMod`、`SelectedModDetail`、`IsDetailPanelOpen`。
- 部署：`CurrentDeploymentMethod`、`IsDeploying`、`DeploymentMessage`。
- Active Profile：私有字段 `_currentProfile`。

### 初始化和刷新

`InitializeAsync()`先调用 `LoadModsAsync()`，之后调用 `SearchService.BuildIndexAsync()`。`LoadModsAsync()`顺序为：

1. 读取 Active Profile。
2. 读取全部 Mod 并 `ToList()`。
3. 对每个实体创建 `ModItemViewModel`。
4. 根据 Active Profile 的 `ProfileMod.Enabled` 和 `Order`设置行状态。
5. 读取最新部署会话，建立 ModId 集合并设置 `IsDeployed`。
6. 应用搜索、筛选、排序。
7. 计算统计。
8. 异步加载前 50 个筛选结果的图标。

`RefreshProfileStateAsync()`只刷新 Active Profile、每个 Mod 的 Enabled/Order、筛选和统计，不重新读取部署状态或图标。

## 5. Library / Profile 业务语义

当前代码层的关系为：

```text
Mods 表 / ModRepository.GetAllAsync()
        ↓
LibraryViewModel._allMods
        ↓ 读取 Active Profile.Mods
ProfileMods(ProfileId, ModId, Enabled, Order)
        ↓
ModItemViewModel.IsEnabled / Order
        ↓
Library 行状态和 Enabled/Disabled 过滤
```

Library 本身表示用户已经导入并保存的全局 Mod 资产。`ProfileMod`是 Profile 对 Library Mod 的引用，不是 Library Mod 实体本身的字段。

当前 UI 没有显示 Profile 名称、Profile 使用数量、Profile 引用与非引用的独立标记。Library 行中的 `已启用` 实际依赖 Active Profile，因此容易被理解成 Library 全局状态。

审计结论：Library 与 Profile 的数据关系在代码中存在，但当前视觉表达为“不清晰”。

## 6. Mod Row 字段结构

每条 Row 是 ListBox DataTemplate 内的单个 Border，不是独立 View。

### 左侧

- CheckBox：`IsChecked={Binding IsSelected}`，无显式宽高，右 Margin 12。
- Icon 容器：48x48，CornerRadius 6，Surface3，ClipToBounds，右 Margin 16。
- Image：绑定 `Icon`，UniformToFill，仅在 Icon 非空时可见。
- fallback：Unicode/Emoji `📦`，FontSize 24，Icon 为空时显示。

### 主信息

- Name：绑定 `Name`，FontSize 15、SemiBold、Primary Text。
- Version：绑定 `Version`，FontSize 12。
- Category：绑定 `CategoryDisplay`，FontSize 12。
- Author：绑定 `Author`，FontSize 12。
- 版本、分类、作者由两个 `|` TextBlock 分隔，均没有显式 TextWrapping 或截断策略。
- Description、Tags、Path、Hash、时间字段不在 Row 中，仅在详情面板中显示。

### 状态

- Enabled：`IsVisible={Binding IsEnabled}`，绿色 Border、CornerRadius 4、Padding 8,2，文本 `✓ 已启用`。
- Deployed：`IsVisible={Binding IsDeployed}`，蓝色 Border、CornerRadius 4、Padding 8,2，文本 `● 已部署`。
- Row 中没有单独的 `未启用`、`未部署`标签；未显示通常表示 false。

### 右侧

- Size：绑定 `SizeFormatted`，FontSize 13，TextMuted，水平 Margin 16。
- More：Unicode `⋮`，Icon Button，绑定菜单命令。
- 菜单：启用/禁用、查看详情、删除。
- Row Border：Surface2、底部 1px Border、Margin 0、Padding 由 `ModItemPadding`决定。
- Row 没有显式 Height；Comfortable 为上下各 12，Compact 为上下各 6。

## 7. Enabled 语义

调用链：

```text
LibraryViewModel.LoadModsAsync / RefreshProfileStateAsync
    → ProfileService.GetActiveProfileAsync()
    → Profile.Mods[ModId].Enabled
    → ModItemViewModel.IsEnabled
    → Row 已启用标签、EnabledCount、DisabledCount、Enabled/Disabled Filter
```

`ModItemViewModel.IsEnabled`是列表 ViewModel 字段，不是 `Mod`实体字段。对于 Active Profile 中不存在的 Mod，它保持 false；因此当前 UI 无法区分“未加入 Active Profile”和“已加入但在 Active Profile 中禁用”。

单个启用/禁用调用 `ProfileService.SetModEnabledAsync()`。批量启用/禁用调用 `ProfileService.SetModsEnabledAsync()`；批量启用会把尚未存在于 ProfileMods 的 Mod 加入当前 Profile，批量禁用不会添加不存在的引用。

结论：当前 `Enabled` 是 Active Profile 状态，不是 Library 全局状态，也不是 Mod 实体状态。

## 8. Deployed 语义

调用链：

```text
LibraryViewModel.LoadModsAsync
    → DeploymentService.GetLatestDeploymentsAsync()
    → DeploymentRepository.GetLatestSessionIdAsync()
       (按 DeploymentRecords.StartTime DESC 取一个最新 SessionId)
    → GetBySessionIdAsync()
    → 过滤 Completed / PartiallyFailed
    → 取 ModId HashSet
    → ModItemViewModel.IsDeployed
```

该状态有以下确定语义：

- 不是实时扫描游戏目录。
- 不是单个 Mod 当前文件是否存在的判断。
- 不是由 `Mod.Status`派生。
- 不是明确限定 Active Profile 的状态；最新 Session 的记录包含 ProfileId，但 Library 只按 ModId 建集合。
- `PartiallyFailed` 也会被标记为已部署。
- 没有直接在当前页面显示部署会话、ProfileId、文件数量或失败文件。
- Clean 后页面重新加载时依赖数据库中剩余的最新会话记录；代码没有单独的文件系统复核。

结论：`已部署`表示“最新部署会话中存在 Completed 或 PartiallyFailed 的该 Mod 记录”，不是全局可靠的当前文件系统状态。

## 9. Search

### View 到 ViewModel

- TextBox 双向绑定 `SearchText`。
- `OnSearchTextChanged`取消上一枚 CancellationTokenSource，创建新的 CTS。
- 固定防抖时间：300ms。
- 防抖后调用 `SearchAsync`。
- generation 计数用于丢弃过期结果。

### ViewModel 到 SearchService

有搜索文本时，ViewModel 调用 `SearchService.SearchAsync()`，取结果 ModId 建 HashSet，再对 `_allMods`进行二次筛选、状态筛选、分类筛选和排序。

无搜索文本时，直接调用 `ApplyFiltersAsync()`，对 `_allMods`本地执行搜索条件、状态筛选、分类筛选、排序。

### SearchService 实际行为

- 索引包含 exact name、name prefix、normalized name、tag 和 name fuzzy 索引。
- `SearchFuzzy`仅扫描 Mod.Name。
- `SearchAsync`没有直接用 Description、Author 或 Tags 进行普通文本匹配；`tag:`才走标签索引。
- Library 本地空文本筛选路径包含 Name、Description、Author，但非空文本路径先由 SearchService 按名称得到 ModId，再做本地筛选。因此 Description/Author 命中但 Name 不命中的 Mod 不会进入最终结果。
- `SearchService.BuildIndexAsync()`在 `_indexLock`内通过 `GetAllAsync(...).Result`同步等待异步仓储调用。

结论：输入有 300ms 防抖和旧请求取消；每次有效输入仍会创建新的结果集合。普通搜索实际以名称索引为入口，Description/Author 的可检索语义与本地空文本路径不一致。

## 10. Filter

支持的状态筛选：

- All
- Enabled
- Disabled
- Deployed
- NotDeployed

另有分类筛选 `SelectedCategory`。

`ApplyFiltersAsync()`的实际顺序为：

```text
_allMods
  → SearchText 本地条件（有文本时）
  → ApplyFilter(CurrentFilter, SelectedCategory)
  → ApplySort(CurrentSortMode)
  → ToList()
  → new ObservableCollection<ModItemViewModel>
```

有非空搜索文本时，顺序为：

```text
SearchService.SearchAsync(SearchText)
  → 按结果 ModId 过滤 _allMods
  → 状态过滤
  → 分类过滤
  → 排序
  → 新建 ObservableCollection
```

分类变化和排序变化由属性 partial method 异步触发 `ApplyFiltersAsync()`；分类命令本身也会调用一次，存在同一变化触发重复计算的路径。

统计栏的 Total/Enabled/Disabled/Deployed/NotDeployed 基于 `_allMods`全量集合；列表数量基于 `FilteredMods`。两者不是同一个范围。

## 11. Sort

实际 `ModSortMode`只有五项：

- NameAscending：名称升序
- NameDescending：名称降序
- DateAddedNewest：ImportedAt 降序
- DateAddedOldest：ImportedAt 升序
- Category：Category 后按 Name 升序

排序通过 `OrderBy` / `OrderByDescending`创建新的延迟序列，随后 `ToList()`并替换整个 `FilteredMods`。因此排序会重新生成列表集合并触发 ListBox 数据更新，不是原地调整可见行。

## 12. Multi-selection

选择状态有三层同步：

1. Row CheckBox 修改 `ModItemViewModel.IsSelected`。
2. `OnModPropertyChanged`把行加入或移出 `SelectedMods`。
3. ListBox 的 `SelectedItems` 和 `SelectionChanged`通过 code-behind `SyncListSelection()`再次同步。

支持：

- 单行 CheckBox。
- ListBox Multiple selection。
- 全选当前 `FilteredMods`。
- 反选当前 `FilteredMods`。
- 清空全部选择。
- Ctrl+A 触发全选。
- 已选数量和批量操作栏。

`SelectAll`、`InvertSelection`均按当前 FilteredMods 遍历；筛选外已选项保留。全选和反选会为每个行设置 `IsSelected`，并通过 ObservableCollection/PropertyChanged 同步选择集合。100/1000/8000 条目下均为 O(n)遍历，且会产生大量逐项状态通知；代码没有批量通知或选择集合替换。

## 13. Batch Operations

### 批量启用

```text
SelectedMods.ToList()
  → ProfileService.SetModsEnabledAsync(profileId, modIds, true)
  → foreach 设置 ModItemViewModel.IsEnabled
  → UpdateStatisticsAsync()
```

ProfileService 对新加入的 ModId 一次读取并一次更新 Profile；ViewModel 状态更新仍按行逐项执行。没有逐 Mod 调用 SetModEnabledAsync。

### 批量禁用

与批量启用相同，参数为 false；不存在于 Profile 的 Mod 不会被添加。

### 批量删除

```text
SelectedMods.ToList()
  → foreach
      await DeleteModDeploymentRecordsAsync(mod.Id)
      await ModService.DeleteModAsync(mod.Id)
      从选择集合、_allMods、FilteredMods 移除
      从 SearchService 索引移除
  → ClearSelection()
  → UpdateStatisticsAsync()
```

删除为串行 foreach/await，没有并行任务、批量删除 API 或逐项进度条。中途异常时，之前项目可能已经完成删除，后续项目不会继续。

## 14. Deploy / Clean

### Deploy

按钮位于页面标题栏，绑定 `DeployCommand`。命令执行范围不是 SelectedMods，而是：

1. 要求存在 Active Profile。
2. 要求 GameDetectionService 有游戏路径。
3. 二次确认。
4. `DeploymentService.DeployProfileAsync(_currentProfile.Id, ...)`。
5. DeploymentService 先清理最近部署会话。
6. 读取 Profile 中 `Enabled=true` 的全部 ProfileMods，按 Order 排序。
7. 对每个启用 ProfileMod 串行读取实体、枚举文件、逐文件部署、写入 DeploymentRecord。

当前页面的选择状态不会改变 Deploy 的目标范围。

### Clean

按钮绑定 `CleanupCommand`。它调用 `PreDeploymentCleanupAsync()`，目标是全局最新部署 Session 的文件和记录，不读取 SelectedMods，也不以当前 Active Profile Id 作为过滤条件。

### 范围结论

页面标题栏处于 Library 语境，用户同时可以进行行选择，但 Deploy/Clean 实际作用范围分别是 Active Profile 全部启用 Mod 和数据库中最新部署 Session。UI 没有在按钮旁明确写出这两个作用范围。

## 15. Delete

单个和批量删除都先调用 `DeploymentService.DeleteModDeploymentRecordsAsync(mod.Id)`，再调用 `ModService.DeleteModAsync(mod.Id)`。

`ModService.DeleteModAsync()`实际执行：

1. 读取 Mod。
2. 删除 `mod.Path` 对应的物理目录。
3. 删除缩略图。
4. 删除 Mods 表行。

数据库 `ProfileMods.ModId`存在 `FOREIGN KEY ... ON DELETE CASCADE`，因此启用 SQLite 外键时，删除 Library Mod 会删除所有 Profile 对该 Mod 的引用。ViewModel 没有读取或显示“被多少 Profile 使用”，确认文案只说“此操作会删除模组文件”。

结论：Library Delete 是实体、物理文件、缩略图、部署记录和 ProfileMod 引用的联动删除，不是仅移除某个 Profile 引用。

## 16. Add Mod

调用链：

```text
LibraryView.OnImportClicked
  → StorageProvider.OpenFilePickerAsync(AllowMultiple=true)
  → 过滤本地路径
  → LibraryViewModel.ImportPathsAsync
  → 每个路径串行调用 ModService.ImportModAsync
  → ModImportService 导入和解析
  → ModService 生成缩略图并更新 Mod
  → LoadModsAsync 刷新 Library
```

文件选择器支持 `*.zip` 和所有文件。导入过程没有调用 `ProfileService.AddModToProfileAsync`，因此导入只进入 Library，不自动加入 Active Profile。

ImportPathsAsync支持 CancellationToken，但多个来源是串行导入；异常会显示通知，取消会显示警告。UI loading 状态在导入期间使用 `IsLoading`。

## 17. Mod Detail Panel

- 详情面板在 LibraryView 的 Grid 上方，以 `Grid.Row=0 Grid.RowSpan=6 ZIndex=100`显示。
- 背景为半透明黑色 Border，点击背景调用 `CloseDetailPanelCommand`。
- `ModDetailPanel`右对齐，固定 `Width=400`，不随主内容宽度变化。
- 面板内部根 Border 使用硬编码深色背景、1px 左边框、Padding 24。
- 内部 ScrollViewer 包裹 StackPanel。
- 打开方式：行 More 菜单的“查看详情”、Enter 打开 FocusedMod 或 SelectedMods 第一项。
- 打开时复用现有 `ModItemViewModel`作为 DataContext，不重新读取实体。
- 详情内显示 Icon、Name、Version、Enabled/Deployed 状态、Author、Category、Description、Tags、Path、Size、Hash、ImportedAt、ModifiedAt 和操作按钮。
- Icon 由列表的 `IconBitmap`共享；面板自身不触发新的图片加载。
- 关闭时 `IsDetailPanelOpen=false`、`SelectedModDetail=null`。已创建在 ModItemViewModel 上的命令引用没有显式清空。
- 删除详情中的 Mod 后调用 `CloseDetailPanel()`。
- 面板 section 使用 CornerRadius 8、Padding 16、Margin bottom 12；Icon 容器 120x120、CornerRadius 6。

## 18. Loading / Empty / Error

### Loading

`IsLoading`显示加载中 StackPanel。其与 ListBox 的 `HasFilteredResults`不是互斥状态；重新加载开始时没有先清空 `FilteredMods`或明确把 `HasFilteredResults`设为 false，因此旧列表可在加载状态切换期间继续满足可见条件。

### Empty

`IsEmpty`只在 LoadModsAsync 成功完成后根据 `_allMods.Count == 0`设置。文本为“还没有模组”，并提供添加模组入口。

### Search/Filter Empty

`HasNoResults`在 `UpdateResultState()`中当 `_allMods`非空、无错误、非加载且 `FilteredModCount == 0`时为 true。文本为“没有匹配的模组”，按钮只绑定 `ClearSearchCommand`；如果仅由分类或状态筛选产生空结果，该按钮不会清除全部筛选条件。

### Error

`HasError`显示加载失败、ErrorMessage 和重试按钮。重试绑定 `RefreshCommand`。异常路径没有在 catch 中重置 `HasFilteredResults`、`IsEmpty`或 `FilteredMods`。

### Import Error

导入异常通过 NotificationService 显示“导入失败”，没有单独的 Library Import Error 页面状态。

### Deployment Error

部署/清理异常通过 NotificationService 显示，部署遮罩关闭；页面随后不一定保留失败细节，依赖通知和部署记录状态。

结论：空库、搜索/筛选无结果、加载错误、导入错误和部署错误存在不同代码路径，但加载、错误与旧列表可见性的互斥关系无法由 XAML 结构保证。

## 19. Virtualization

当前实际控件为 `ListBox`，ItemsPanel 为 `VirtualizingStackPanel`。没有 DataGrid、ItemsRepeater 或 ItemsControl 包装列表，也没有外层 ScrollViewer 嵌套 ListBox。

已确认的事实：

- ListBox 使用虚拟化面板声明。
- 没有显式 `VirtualizationMode=Recycling`。
- Row 没有固定 Height，实际高度由 Icon 48、Comfortable/Compact Padding、文本内容和边框共同测量。
- Name、Version、Category、Author 没有显式 TextWrapping；正常单行时高度近似稳定。
- Detail Panel 内部 ScrollViewer 与主 ListBox 分属不同 Overlay，主列表没有被它包裹。
- 没有仓库中的 Library 运行时可视容器计数或 8000 条目 UI 基准。

结论：代码结构支持虚拟化，但回收模式、动态测量成本和大规模运行时行为没有被本仓库验证。当前可靠性结论为“需要进一步验证”，不能仅因出现 `VirtualizingStackPanel`判定性能可靠。

## 20. 100 / 1000 / 8000+ 数据规模风险

### 100 条

- 初始化会读取 100 个实体、创建 100 个 ViewModel，并对全量状态统计。
- 搜索有 300ms 防抖，列表集合仍会在结果变化时整体替换。
- 全选/反选逐项通知通常可用，但不存在批量通知。

### 1000 条

- `_allMods`、`FilteredMods`、`SelectedMods`和 SearchService 索引同时保留多份引用/索引结构。
- 每次筛选/排序都会创建新 List 和新 ObservableCollection。
- 批量删除串行执行，图标加载最多先加载 50 项。
- 已有 Search Benchmark 有 10,000 条种子数据，但没有 LibraryViewModel 的 1000 条桌面列表基准。

### 5000/8000/10000 条

- 初始读取、实体到 ViewModel 创建、Profile 状态赋值、部署状态扫描、统计和首次筛选均以全量列表为基础。
- `ApplyFiltersAsync()`把 LINQ 结果 `ToList()`，随后新建 ObservableCollection；每次搜索/筛选/排序都会重新发送集合替换通知。
- 全选、反选、清空选择触发逐项 `IsSelected`变化和 SelectedMods 集合操作。
- SearchService 的模糊名称搜索扫描全量 `_allMods`；prefix 结果收集和优先级去重也会创建临时集合。
- ListBox 理论上只生成可见行，但没有回收模式或实际容器计数验证。
- 当前仓库没有 5000、8000、10000 条 Library UI 启动、筛选、选择、滚动、详情或部署基准。

结论：8000+ 条目存在明确的内存和 UI 刷新风险；是否达到“完全无法使用”没有运行时证据，问题等级为 P1 性能风险而不是 P0。

## 21. Image / Icon Loading

### 列表

- LoadModsAsync 完成筛选后启动未等待的 `LoadIconsAsync()`。
- `LoadIconsAsync()`仅遍历 `FilteredMods.Take(50)`，按顺序调用 `ModItemViewModel.LoadIconAsync()`。
- 当前列表不会一次性为全部 8000 个 Mod 创建 Bitmap。

### ModItemViewModel

- `ThumbnailPath`来自 Mod 实体。
- 无 ThumbnailPath 或文件不存在时保持 null，使用 Emoji fallback。
- 图片创建在 `Task.Run`中执行 `new Bitmap(ThumbnailPath)`。
- Icon Bitmap 由 ViewModel 持有，没有使用 `ImageCacheService`或显式 DecodeSize。
- CancellationToken 传给 Task.Run，但 `_ = LoadIconsAsync()`本身没有页面级取消令牌。
- `Icon`已有时或 `IsLoadingIcon`为 true 时直接返回。

### Detail Panel

详情面板绑定同一个 `IconBitmap`，不额外读取文件。

结论：首屏最多尝试加载 50 张图片，减轻了初次 8000 项的 Bitmap 压力；没有共享图片缓存、释放生命周期或可视区域滚动加载证据，向后滚动到未加载行不会由列表可视化事件触发新的图片加载。

## 22. Memory Risk

主要内存持有者：

- `_allMods`中的所有 ModItemViewModel 和底层 Mod 引用。
- `FilteredMods`中的当前结果引用。
- `SelectedMods`中的选择引用。
- SearchService 的 `_allMods`实体列表、exact/prefix/tag/normalized 多套索引。
- 最多 50 个已加载 Icon Bitmap，且无统一 ImageCacheService。
- Search/Filter/Sort 过程中的临时 HashSet、List、LINQ 枚举结果。

全量 Mod 元数据会在多个服务/UI集合中保持引用。当前没有 Library 专用内存基线，也没有 8000+ 条目的 GC 或 Bitmap 生命周期验证。

已有 Image Benchmark 仅覆盖单张、10 张、50 张图片，不代表 Library 8000 项滚动场景。

## 23. UI Refresh Risk

- `FilteredMods = new ObservableCollection<ModItemViewModel>(filtered)`会替换集合实例。
- 每次搜索、状态筛选、分类筛选、排序都可能重新执行全量 LINQ 并替换集合。
- `SelectAll`/`InvertSelection`为每行设置属性；`OnModPropertyChanged`又维护 SelectedMods。
- ListBox SelectionChanged 和 CheckBox 状态有双向同步路径。
- LoadModsAsync 会 ClearSelection、解绑/重新绑定所有 `_allMods`的 PropertyChanged，再创建新 ViewModel 列表。
- `OnSelectedCategoryChanged`和 `FilterByCategoryAsync`可能对同一变化分别启动 ApplyFiltersAsync。
- generation 只防止旧筛选结果覆盖新结果，不减少已经发生的全量计算和临时分配。

结论：100 条内刷新路径清晰；1000+ 条时集合替换和逐项选择通知构成可观察 UI 刷新风险；8000+ 条时风险明确。

## 24. Keyboard / Focus

`LibraryView.axaml.cs`当前处理：

- Ctrl+F：聚焦 SearchBox 并全选文本。
- Ctrl+A：执行 SelectAllCommand。
- Delete：执行 BatchDeleteCommand。
- Enter：打开 FocusedMod 或首个 SelectedMod 的详情。
- Escape：详情打开时关闭详情，否则清空选择。

未在 LibraryView code-behind 中专门处理 Home、End、PageUp、PageDown、Up、Down；这些按键交给 ListBox 默认键盘导航。

ListBox 使用 `FocusedMod`作为 SelectedItem，CheckBox 和 More Button 是行内独立焦点控件。没有显式 TabIndex、AutomationProperties 或自定义焦点样式；全局样式对 Button、TextBox、ComboBox、RadioButton提供 Focus 边框，CheckBox/ListBox 沿用控件默认焦点表现。

删除快捷键在非 TextBox 来源下全局触发，确认对话由命令处理。

## 25. Responsive Layout

### 主窗口约束

- MainWindow：Width 1280、Height 800、MinWidth 960、MinHeight 600。
- LibraryView 设计尺寸 1200x800，无自身最小宽度。
- MainWindow 顶部导航和底部状态栏占据固定 Auto 行，Library 内容占中间剩余空间。

### Library 横向布局

- 标题栏内部为 `*,Auto`，操作区内容不折行。
- 搜索栏为 `*,Auto,Auto`，搜索框占剩余宽度；分类和排序各有 MinWidth。
- Row 使用 `Auto,Auto,*,Auto,Auto`，主信息列占剩余宽度；作者、分类、名称无截断或 TextWrapping 处理。
- 详情面板固定 400px，右侧覆盖主区域。

在 1280 宽时可正常分配主信息列；当窗口接近 960 MinWidth、长名称/作者和多个状态同时出现时，主信息列可用空间下降，XAML 没有显式省略或多行布局规则。1600 或更宽窗口可由 `*`列利用剩余宽度，但顶部操作区仍按内容宽度排列。

## 26. Global Visual System Compliance

### Surface

- Surface0：搜索工具栏、列表主体。
- Surface1：筛选栏、列表头。
- Surface2：标题栏、Row、详情面板内部分组语义。
- Surface3：Icon 背景、批量操作栏。
- 详情面板和状态标签另有局部硬编码颜色。

### Button

- Primary：添加、部署、重试、空状态添加。
- Secondary：清理、取消、反选、清除筛选、清除搜索、批量禁用/删除。
- Icon：搜索清除、更多、详情关闭。
- Filter：五个 RadioButton。
- More：标题栏和 Row 的 Unicode `⋮`。

### Typography

- Page Title：24px，高于 Phase 1 记录的 PageTitleFontSize 22 资源，使用局部值。
- Mod Name：15px SemiBold。
- Metadata：12px。
- Status：11px Medium。
- Size：13px。
- Detail Panel 使用 20px、18px、16px、14px、12px 多级文本。

### 视觉偏差记录

- 页面和详情使用多层 Border，Row 本身是一个带背景和底边的内容容器。
- Row 状态标签仍有独立背景和 CornerRadius 4。
- 搜索、Icon、状态和空状态使用 Emoji/Unicode。
- 局部使用硬编码颜色，如 `#22C55E`、`#38BDF8`、`#EF4444`、`#0F0F0F`、`#2A2A2A`。

## 27. Card / Surface 使用

Library 主列表不是每条一个独立 Card 控件，但每条 DataTemplate 外层是一个 Surface2 Border，带底部边框。页面区域还包括标题 Border、筛选 Border、搜索 Border、列表头 Border、批量栏 Border。

详情面板内部使用多个 `Border.section`，结构为：

```text
Overlay Grid
  └── ModDetailPanel 根 Border
      └── ScrollViewer
          └── 多个 section Border
```

因此主列表是“行级 Surface + 分隔线”，详情面板是明显的分组 Card 化结构；没有发现三层以上主列表 Border 嵌套。

## 28. CornerRadius

LibraryView.axaml：

- 搜索外框：6。
- Mod Icon：6。
- Enabled/Deployed 状态：4。
- 没有 999。
- 其它页面区域 Border 未显式 CornerRadius。

ModDetailPanel.axaml：

- section：8。
- Icon 容器：6。
- 状态标签：4。
- Tags：4。
- 根 Border 无 CornerRadius。

Library 页面没有 12 或 999 圆角。整体值与 Phase 1 普通控件 4px、Overlay/内容容器较小圆角的规则部分一致；详情 section 的 8px 高于全局 Overlay 6px 记录。

## 29. Animation

LibraryView.axaml、LibraryView.axaml.cs、ModDetailPanel.axaml 中没有 `Animation`、`Transition`、`Transitions` 或 `RenderTransform`声明。

存在的动态视觉仅来自：

- 全局 Button/RadioButton/ListBoxItem pointerover/checked/selected 样式。
- IsVisible 状态切换。
- 部署遮罩和详情 Overlay 的直接显示隐藏。
- Loading 使用不确定 ProgressBar，但 Library Loading 状态自身使用 Emoji `⏳`，没有动画声明。

没有发现页面级 Hover、Panel open/close 或 Selection 动画。

## 30. 当前页面垂直空间分配

XAML 没有给四个顶部区域写固定 Height，实际高度由内容、Padding 和全局控件 MinHeight 测量。按当前声明可以得到以下可复核的近似值：

| 区域 | 声明 Padding | 内容基准 | 近似高度 |
|---|---:|---:|---:|
| Page Header | 上下 20 | Button 最小高度约 32 | 约 72 |
| Filter Bar | 上下 16 | Filter/Button 控件约 32 | 约 64 |
| Search Toolbar | 上下 12 | TextBox/ComboBox 约 32~36 | 约 56~60 |
| List Header | 上下 12 | CheckBox/Button 约 32 | 约 56 |
| **进入第一条 Row 前合计** |  |  | **约 248~252** |

该估算只包含 LibraryView 页面自身，不包含 MainWindow 顶部导航、底部状态栏或窗口边框。MainWindow 的 1280x800 中间内容还会被全局顶栏和底部状态栏继续压缩。

在 1280x800 页面高度中，Library 自身列表可用高度约为 548~552px，批量栏显示时还会减少约 56px；在 1280x832 中约为 580~584px，批量栏显示时同样减少约 56px。实际值仍受字体、主题和运行时控件模板影响。

## 31. 当前列表信息密度

### Row 高度

Row 没有显式 Height。Comfortable 模式中 Icon 48px 加上下 Padding 24px，且内部 StackPanel 三行内容通常低于 Icon 高度，因此 Row 约为 72px 加底部 1px 边框。Compact 模式约为 60px 加底部边框。

### 一屏可见行数

以页面自身 1280x800、顶部约 248~252px 为基础估算：

- Comfortable：约 7 行，批量栏显示时约 6 行。
- Compact：约 9 行，批量栏显示时约 8 行。

若以整个 MainWindow 1280x800计算，需再扣除顶部导航和底部状态栏，实际可见行数更少。长名称和作者没有换行，但可能侵占主信息列宽。

## 32. 问题清单

### P0 — 阻塞问题

- 当前没有证据表明存在 P0 级“错误删除全部用户文件以外范围”或“部署调用错误 Profile”的确定性故障。Delete 的物理删除行为与确认文案一致，ProfileMods 具有数据库级级联关系。

### P1 — 高优先级

1. **Library/Profile 状态语义容易误解**：Row 的 `已启用`来自 Active Profile，但页面没有显示 Profile 名称或“未加入 Profile”状态；Library 全局 Mod 与当前 Profile 引用在视觉上没有区分。
2. **Deploy/Clean 作用范围与选择上下文不清晰**：Deploy 忽略 SelectedMods，部署 Active Profile 的全部启用 Mod；Clean 清理数据库中最新 Session，未按 SelectedMods 或 Active Profile 限定。
3. **8000+ 条目缺少可靠运行时验证且存在全量刷新风险**：初始化、筛选、排序、集合替换、全选/反选、搜索临时集合均以全量数据为基础；没有 Library UI 规模基准。
4. **非空搜索的字段语义不一致**：SearchService 普通搜索入口主要使用名称索引，Library 本地筛选虽然包含 Description/Author，但无法让仅命中 Description/Author 的 Mod 进入结果。
5. **Deployed 不是当前 Profile 或文件系统实时状态**：只读取全局最新 Session，并把 PartiallyFailed 记为已部署；状态标签可能与当前 Active Profile 或磁盘现状不一致。

### P2 — UI / UX

1. 顶部四层控制区域约占 Library 页面 248~252px，1280x800 下列表首屏高度有限。
2. Row、详情 section 和多层页面 Border 形成明显 Surface/Card 层级；详情面板分组较多。
3. Empty、Search/Filter Empty、Error、Loading 使用同一 Grid 并列可见性，状态互斥依赖 ViewModel 时序，加载/错误期间旧列表可保持可见。
4. Search/Filter Empty 只清除搜索，不区分清除分类或状态筛选。
5. Row 的 Name/Author/Category 没有显式截断或换行，窄窗口和长文本下列宽压力较高。
6. 状态、Fallback、搜索和更多操作使用 Emoji/Unicode，图标资源没有统一到 Geometry/SVG。
7. 详情面板固定 400px，属于覆盖层，不显示 Profile 使用关系。

### P3 — 细节

1. Page Header 标题局部使用 24px，与全局 PageTitleFontSize 22 记录不一致。
2. 详情 section CornerRadius 8，高于全局 Overlay 6 的记录。
3. 搜索外框使用 CornerRadius 6，Row Icon 使用 CornerRadius 6，状态标签使用硬编码 4。
4. 局部颜色未全部通过 DynamicResource 表达。
5. ListBox 没有在 XAML 中显式声明 Recycling 模式。
6. 空状态和错误图标仍使用 Emoji/Unicode。

## 33. 必须保留的现有功能

1. Library 作为全局 Mod Repository 的全量读取、导入、删除、详情查看和文件夹打开能力。
2. Active Profile 对 Mod 的 Enabled/Order 状态同步，以及批量启用/禁用通过 ProfileService 的批量持久化。
3. 搜索、状态筛选、分类筛选、排序、全选、反选、多选和已选项批量操作。
4. Active Profile 全部启用 Mod 的 Deploy 流程、最近 Session 的 Clean 流程、取消和进度通知。
5. Mod 删除时的物理文件、缩略图、部署记录和数据库实体联动生命周期。
6. 详情面板复用当前 ModItemViewModel 的状态和图标数据。

## 34. 后续 Phase 3 实施时不能破坏的业务语义

- Library Mod 仍然是全局资产；不能被等同为当前 Profile 的 Mod 列表。
- `ProfileMod.Enabled`仍然是 Profile 范围的启用状态，不能改写成 Mod 实体全局字段。
- Active Profile 中不存在的 Mod 与存在但禁用的 Mod 是两个数据事实，当前 UI 虽未区分，底层关系不能混为一谈。
- Deploy 必须继续以 Profile 的启用 Mod 和 Order 为业务输入，不能把 Library 全部 Mod 当作部署集合。
- Clean 必须继续遵守 DeploymentService 的 Session、DeploymentFile 和文件清理记录链。
- Delete Mod 的物理文件、缩略图、部署记录和 ProfileMods 级联语义必须保持一致。
- Import Mod 当前只进入 Library，不自动写入 Active Profile。
- 批量启用新 Mod 时，ProfileService 的批量加入/更新语义不能退化成 UI 逐条伪批量。
- SelectedMods 是 UI 选择状态，不能被当作 Deploy 的隐含业务输入，除非调用链和页面文案同时改变并经过单独验收。

## 35. 测试 / Benchmark 现状

### 已有测试

- `LibraryViewModelSelectionTests`覆盖 CheckBox/List selection 同步、筛选范围内全选、筛选范围内反选，共 3 个测试场景。
- 测试构造 LibraryViewModel 时使用 null 依赖，未覆盖 Load、Search、Filter、Sort、Deploy、Clean、Delete、Import、Detail Panel 或大规模集合。
- ProfileService 测试覆盖 Add/Remove/SetEnabled 和批量更新语义。
- DeploymentService 测试覆盖部署/清理服务链，但不是 Library 页面交互测试。

### 已有 Benchmark

- `SearchBenchmarks`在 GlobalSetup 中创建 10,000 条 Mod，覆盖 SearchService 的名称、作者、标签、分类、空搜索和无结果等方法。
- `PHASE20_BENCHMARK_RESULTS.md`记录 Search 场景和 Image 场景基线。
- 报告同时明确记载 Library 性能测试“未实现（架构不匹配）”。
- 现有 10,000 条数据是 SearchService/仓储基准，不是 LibraryViewModel、Avalonia ListBox、Icon 滚动或选择批量操作基准。
- 没有发现 100、1000、5000、8000、10000 条 Library UI 专用测试或运行时容器计数。

### 本次验证边界

本阶段按执行文档只读审计，没有新增测试代码、没有生成规模数据、没有运行会改变数据库或用户 Mod 文件的操作。报告中的 1280x800/832 高度和 8000+ 结论来自 XAML/调用链/已有基准的静态证据，未宣称桌面运行时性能已通过验收。

## 36. 审计结论

### 当前 Library UI 是否可以直接进入重构？

**否。** 当前业务调用链已可追踪，但 Enabled、Deployed、Deploy/Clean 范围和 Library/Profile 关系存在视觉语义缺口；8000+ 条目没有 Library UI 专用基线。

### 当前虚拟化是否可靠？

**需要进一步验证。** XAML 使用 `ListBox + VirtualizingStackPanel`且没有外层 ScrollViewer 嵌套，但没有 Recycling 声明、运行时容器验证或大规模 UI 基准。

### 当前 8000+ Mod 是否存在明确风险？

**是。** 全量内存源、全量筛选/排序集合替换、逐项选择通知、SearchService 索引和无缓存 Bitmap 生命周期共同构成明确风险；当前没有证据表明必然完全不可用。

### Library / Profile 关系是否清晰？

**不清晰。** Library 读取全局 Mod，但 Row 的 Enabled 来自 Active Profile，页面没有显示 Profile 名称、引用数量或“未加入当前 Profile”的区别。

### Deploy / Clean 作用范围是否清晰？

**不清晰。** Deploy 实际作用于 Active Profile 全部启用 Mod，Clean 实际作用于最新部署 Session；SelectedMods 不参与，页面当前上下文没有直接说明。

### 当前 UI 最大的三个问题

1. Library 全局资产与 Active Profile 状态在同一 Row 中混合展示，Enabled/Deployed 语义容易被误读。
2. Deploy/Clean 与行选择并列存在，但真实作用范围不同且没有在操作入口明确表达。
3. 8000+ 条目的全量 ViewModel/筛选/集合替换/选择刷新路径缺少专用验证，并存在明确的 UI 与内存风险。

### Phase 3 重构必须保留的三个核心能力

1. Library 全局 Mod 资产管理与 Import/Delete/Detail 生命周期。
2. Active Profile 的 ProfileMod Enabled/Order 语义及批量 ProfileService 持久化。
3. Profile 范围的 Deploy、Deployment Session 记录和 Clean 文件追踪链。

## 外部参考

- [Avalonia ListBox](https://docs.avaloniaui.net/docs/reference/controls/listbox)
- [Avalonia Virtualization](https://docs.avaloniaui.net/docs/concepts/controls/virtualization)
- [Avalonia Data Binding](https://docs.avaloniaui.net/docs/basics/data/data-binding)
- [Avalonia Styles](https://docs.avaloniaui.net/docs/basics/user-interface/styling)

---

本报告完成后停止在 Phase 3.0 审计阶段，不进入 Phase 3 实施。
