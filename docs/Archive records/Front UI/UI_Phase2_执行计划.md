# HD2Mimnt UI - 阶段 2 执行计划

**创建时间**: 2026-10-03  
**阶段**: 阶段 2 - 模组库核心页面  
**优先级**: P0  
**预计时间**: 1-2 天

---

## 一、阶段目标

完成最重要的模组管理界面，实现：
- 虚拟化模组列表（支持 10,000+ 条数据）
- 搜索与筛选
- 排序
- 单选和多选
- 批量操作
- 模组详情面板

---

## 二、前置条件检查

### 2.1 已完成阶段
- ✅ 阶段 0: UI 与后端审查完成
- ✅ 阶段 1: 整体 UI 框架完成

### 2.2 依赖服务确认
需要确认以下服务可用：
- [ ] ModService - 模组增删改查
- [ ] SearchService - 全文搜索
- [ ] ImageCacheService - 图片缓存
- [ ] ImageLoaderService - 图片加载

### 2.3 现有 ViewModel 确认
- [ ] LibraryViewModel - 是否已有基础实现
- [ ] 需要哪些扩展属性和命令

---

## 三、工作内容分解

### 任务 2.1: 审查现有模组服务 ⏱ 30 分钟
**目标**: 确认后端接口完整性

**检查项**:
1. ModService 的查询接口
   - GetAllModsAsync()
   - GetModByIdAsync()
   - SearchModsAsync()
   - FilterModsAsync()
2. 模组实体包含的字段
   - Id, Name, Version, Author, Description
   - Category, Tags, Icon/Thumbnail
   - CreatedAt, UpdatedAt
   - IsEnabled, IsDeployed
3. 分页支持
4. 排序支持
5. 筛选条件支持

**交付物**: 服务接口清单文档

### 任务 2.2: 设计 ModListViewModel ⏱ 1 小时
**目标**: 设计完整的模组列表 ViewModel

**属性**:
```csharp
// 数据源
ObservableCollection<ModItemViewModel> Mods
ObservableCollection<ModItemViewModel> FilteredMods

// 搜索
string SearchText
bool IsSearching

// 筛选
ModFilterType CurrentFilter (All/Enabled/Disabled/Deployed/NotDeployed)
string SelectedCategory

// 排序
ModSortMode CurrentSortMode (NameAsc/NameDesc/DateAdded/Category)

// 选择
ObservableCollection<ModItemViewModel> SelectedMods
bool IsAllSelected
int SelectedCount

// 统计
int TotalModCount
int FilteredModCount
int EnabledCount
int DeployedCount

// 状态
bool IsLoading
bool HasError
string ErrorMessage
bool IsEmpty
```

**命令**:
```csharp
// 搜索和筛选
SearchCommand
ClearSearchCommand
FilterCommand
SortCommand

// 选择
SelectAllCommand
ClearSelectionCommand

// 批量操作
BatchEnableCommand
BatchDisableCommand
BatchDeleteCommand
BatchDeployCommand

// 单个模组操作
EnableModCommand
DisableModCommand
DeleteModCommand
OpenModDetailCommand

// 页面操作
RefreshCommand
ImportModCommand
```

**交付物**: ModListViewModel.cs 框架代码

### 任务 2.3: 实现 ModItemViewModel ⏱ 30 分钟
**目标**: 单个模组项的 ViewModel

**属性**:
```csharp
Guid Id
string Name
string Version
string Author
string Category
string Description
string IconPath
Bitmap? Icon
bool IsEnabled
bool IsDeployed
bool IsSelected
DateTime CreatedAt
```

**方法**:
```csharp
async Task LoadIconAsync()
void UpdateFromEntity(Mod mod)
```

**交付物**: ModItemViewModel.cs

### 任务 2.4: 实现模组列表 AXAML ⏱ 2-3 小时
**目标**: 完整的模组库页面布局

**文件**: `src/HD2Mimnt.UI/Views/LibraryView.axaml`

**布局结构**:
```
┌─ 页面标题栏 ────────────────────────────────────┐
│ 模组库                      添加模组  更多操作   │
├─ 统计筛选栏 ────────────────────────────────────┤
│ 全部(1284) 已启用(156) 未启用(1128) 已部署(89) │
├─ 搜索工具栏 ────────────────────────────────────┤
│ 🔍 搜索...    [分类▾]  [状态▾]  [排序▾]       │
├─ 列表标题栏 ────────────────────────────────────┤
│ □ 全选   共 1,284 个模组                        │
├─ 模组列表（虚拟化）──────────────────────────────┤
│ □ [图] 高清护甲材质   v1.2.0   已启用 已部署  ⋮ │
│ □ [图] 武器模型替换   v2.0.1   已启用 未部署  ⋮ │
│ ...                                             │
├─ 批量操作栏（条件显示）──────────────────────────┤
│ 已选择 3 个模组           启用  禁用  删除  部署 │
└─────────────────────────────────────────────────┘
```

**关键控件**:
1. 统计筛选按钮组（RadioButton）
2. 搜索框（带清除按钮）
3. 筛选下拉菜单
4. 虚拟化 ListBox/DataGrid
5. 列表项模板
6. 批量操作栏

**交付物**: LibraryView.axaml

### 任务 2.5: 实现虚拟化列表 ⏱ 2 小时
**目标**: 支持 10,000+ 模组的高性能列表

**技术方案**:
- 使用 Avalonia 的 VirtualizingStackPanel
- ListBox 启用虚拟化
- 或使用 DataGrid 的内置虚拟化

**AXAML 配置**:
```xml
<ListBox ItemsSource="{Binding FilteredMods}"
         SelectedItems="{Binding SelectedMods}"
         SelectionMode="Multiple"
         VirtualizationMode="Recycling"
         ScrollViewer.HorizontalScrollBarVisibility="Disabled">
    <ListBox.ItemsPanel>
        <ItemsPanelTemplate>
            <VirtualizingStackPanel/>
        </ItemsPanelTemplate>
    </ListBox.ItemsPanel>
    <ListBox.ItemTemplate>
        <DataTemplate>
            <!-- 列表项内容 -->
        </DataTemplate>
    </ListBox.ItemTemplate>
</ListBox>
```

**性能要求**:
- 启动加载时间 < 1 秒
- 滚动流畅（60 FPS）
- 内存占用合理（< 500MB）

**交付物**: 虚拟化列表实现

### 任务 2.6: 实现模组列表项模板 ⏱ 1 小时
**目标**: 设计单个模组列表项

**布局**:
```
┌────────────────────────────────────────────────┐
│ □  [图标]  高清护甲材质包                      │
│             v1.2.0  |  护甲  |  作者名          │
│             ✓ 已启用  ● 已部署                ⋮│
└────────────────────────────────────────────────┘
```

**元素**:
- CheckBox（选择）
- Image（图标，48x48）
- TextBlock（模组名称，粗体）
- StackPanel（版本、分类、作者）
- 状态指示器（Enabled/Deployed）
- 更多菜单按钮

**交付物**: ModListItem DataTemplate

### 任务 2.7: 实现搜索功能 ⏱ 1 小时
**目标**: 实时搜索，防抖优化

**实现要点**:
```csharp
private CancellationTokenSource? _searchCts;
private const int SearchDebounceMs = 300;

partial void OnSearchTextChanged(string value)
{
    _searchCts?.Cancel();
    _searchCts = new CancellationTokenSource();
    
    _ = Task.Delay(SearchDebounceMs, _searchCts.Token)
        .ContinueWith(async _ => 
        {
            if (!_searchCts.Token.IsCancellationRequested)
            {
                await PerformSearchAsync(value);
            }
        }, TaskScheduler.Default);
}

private async Task PerformSearchAsync(string query)
{
    IsSearching = true;
    try
    {
        var results = await _searchService.SearchModsAsync(query);
        FilteredMods = new ObservableCollection<ModItemViewModel>(
            results.Select(m => new ModItemViewModel(m))
        );
    }
    finally
    {
        IsSearching = false;
    }
}
```

**交付物**: 搜索逻辑实现

### 任务 2.8: 实现筛选功能 ⏱ 1 小时
**目标**: 多条件筛选，可组合

**筛选条件**:
```csharp
public enum ModFilterType
{
    All,
    Enabled,
    Disabled,
    Deployed,
    NotDeployed,
    HasConflicts
}
```

**实现**:
```csharp
[RelayCommand]
private async Task FilterAsync(ModFilterType filterType)
{
    CurrentFilter = filterType;
    await ApplyFiltersAsync();
}

private async Task ApplyFiltersAsync()
{
    var filtered = AllMods.AsEnumerable();
    
    // 应用状态筛选
    filtered = CurrentFilter switch
    {
        ModFilterType.Enabled => filtered.Where(m => m.IsEnabled),
        ModFilterType.Disabled => filtered.Where(m => !m.IsEnabled),
        ModFilterType.Deployed => filtered.Where(m => m.IsDeployed),
        ModFilterType.NotDeployed => filtered.Where(m => !m.IsDeployed),
        _ => filtered
    };
    
    // 应用分类筛选
    if (!string.IsNullOrEmpty(SelectedCategory))
    {
        filtered = filtered.Where(m => m.Category == SelectedCategory);
    }
    
    // 应用搜索
    if (!string.IsNullOrEmpty(SearchText))
    {
        filtered = filtered.Where(m => 
            m.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }
    
    FilteredMods = new ObservableCollection<ModItemViewModel>(filtered);
    FilteredModCount = FilteredMods.Count;
}
```

**交付物**: 筛选逻辑实现

### 任务 2.9: 实现排序功能 ⏱ 30 分钟
**目标**: 多种排序方式

**排序模式**:
```csharp
public enum ModSortMode
{
    NameAscending,
    NameDescending,
    DateAddedNewest,
    DateAddedOldest,
    Category
}
```

**实现**:
```csharp
[RelayCommand]
private void Sort(ModSortMode sortMode)
{
    CurrentSortMode = sortMode;
    
    var sorted = CurrentSortMode switch
    {
        ModSortMode.NameAscending => FilteredMods.OrderBy(m => m.Name),
        ModSortMode.NameDescending => FilteredMods.OrderByDescending(m => m.Name),
        ModSortMode.DateAddedNewest => FilteredMods.OrderByDescending(m => m.CreatedAt),
        ModSortMode.DateAddedOldest => FilteredMods.OrderBy(m => m.CreatedAt),
        ModSortMode.Category => FilteredMods.OrderBy(m => m.Category).ThenBy(m => m.Name),
        _ => FilteredMods.AsEnumerable()
    };
    
    FilteredMods = new ObservableCollection<ModItemViewModel>(sorted);
}
```

**交付物**: 排序逻辑实现

### 任务 2.10: 实现批量操作 ⏱ 1 小时
**目标**: 多选和批量处理

**批量命令**:
```csharp
[RelayCommand(CanExecute = nameof(HasSelection))]
private async Task BatchEnableAsync()
{
    foreach (var mod in SelectedMods)
    {
        await _modService.SetModEnabledAsync(mod.Id, true);
        mod.IsEnabled = true;
    }
    await UpdateStatisticsAsync();
}

[RelayCommand(CanExecute = nameof(HasSelection))]
private async Task BatchDisableAsync()
{
    foreach (var mod in SelectedMods)
    {
        await _modService.SetModEnabledAsync(mod.Id, false);
        mod.IsEnabled = false;
    }
    await UpdateStatisticsAsync();
}

[RelayCommand(CanExecute = nameof(HasSelection))]
private async Task BatchDeleteAsync()
{
    var result = await _dialogService.ConfirmAsync(
        $"确定要删除选中的 {SelectedCount} 个模组吗？",
        "删除确认");
    
    if (result)
    {
        foreach (var mod in SelectedMods.ToList())
        {
            await _modService.DeleteModAsync(mod.Id);
            AllMods.Remove(mod);
            FilteredMods.Remove(mod);
        }
        ClearSelection();
    }
}

private bool HasSelection() => SelectedCount > 0;
```

**UI**:
```xml
<Border IsVisible="{Binding SelectedCount, Converter={StaticResource GreaterThanZeroConverter}}"
       Background="#161D2B"
       Padding="16,12"
       BorderBrush="#1E2636"
       BorderThickness="0,1,0,0">
    <Grid ColumnDefinitions="*,Auto">
        <TextBlock Grid.Column="0"
                  Text="{Binding SelectedCount, StringFormat='已选择 {0} 个模组'}"
                  Foreground="#E0E0E0"
                  VerticalAlignment="Center"/>
        <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="8">
            <Button Content="启用" Command="{Binding BatchEnableCommand}" Classes="primary"/>
            <Button Content="禁用" Command="{Binding BatchDisableCommand}" Classes="secondary"/>
            <Button Content="删除" Command="{Binding BatchDeleteCommand}" Classes="secondary"/>
            <Button Content="部署" Command="{Binding BatchDeployCommand}" Classes="primary"/>
        </StackPanel>
    </Grid>
</Border>
```

**交付物**: 批量操作实现

### 任务 2.11: 实现模组详情面板 ⏱ 2 小时
**目标**: 侧边滑出的详情面板

**文件**: `src/HD2Mimnt.UI/Controls/ModDetailPanel.axaml`

**布局**:
```
┌─ 模组详情 ─────────────────────── ✕ ┐
│                                      │
│ [大图标]                             │
│                                      │
│ 高清护甲材质包                       │
│ v1.2.0  |  护甲分类                  │
│ 作者: ModAuthor                      │
│ 添加时间: 2026-10-01                 │
│                                      │
│ ──────────────────────────────────  │
│                                      │
│ 描述：                               │
│ 这是一个高清护甲材质替换模组...      │
│                                      │
│ ──────────────────────────────────  │
│                                      │
│ 文件数量: 128                        │
│ 大小: 256 MB                         │
│ 路径: C:\Mods\...                    │
│                                      │
│ ──────────────────────────────────  │
│                                      │
│ [启用] [部署] [删除]                 │
│                                      │
└──────────────────────────────────────┘
```

**交付物**: ModDetailPanel.axaml + ViewModel

### 任务 2.12: 实现空状态和错误状态 ⏱ 30 分钟
**目标**: 友好的空状态提示

**空状态**:
```xml
<StackPanel IsVisible="{Binding IsEmpty}"
           HorizontalAlignment="Center"
           VerticalAlignment="Center"
           Spacing="16">
    <TextBlock Text="📦"
              FontSize="64"
              HorizontalAlignment="Center"
              Opacity="0.3"/>
    <TextBlock Text="还没有模组"
              FontSize="18"
              Foreground="#9CA3AF"
              HorizontalAlignment="Center"/>
    <TextBlock Text="点击"添加模组"导入你的第一个模组"
              FontSize="14"
              Foreground="#6B7280"
              HorizontalAlignment="Center"/>
    <Button Content="添加模组"
           Command="{Binding ImportModCommand}"
           Classes="primary"/>
</StackPanel>
```

**错误状态**:
```xml
<StackPanel IsVisible="{Binding HasError}"
           HorizontalAlignment="Center"
           VerticalAlignment="Center"
           Spacing="16">
    <TextBlock Text="⚠"
              FontSize="64"
              HorizontalAlignment="Center"
              Foreground="#EF4444"
              Opacity="0.5"/>
    <TextBlock Text="加载失败"
              FontSize="18"
              Foreground="#9CA3AF"
              HorizontalAlignment="Center"/>
    <TextBlock Text="{Binding ErrorMessage}"
              FontSize="14"
              Foreground="#6B7280"
              HorizontalAlignment="Center"
              TextWrapping="Wrap"
              MaxWidth="400"/>
    <Button Content="重试"
           Command="{Binding RefreshCommand}"
           Classes="primary"/>
</StackPanel>
```

**交付物**: 空状态和错误状态实现

### 任务 2.13: 性能测试和优化 ⏱ 2 小时
**目标**: 验证性能目标

**测试场景**:
1. 加载 10,000 条模组数据
2. 快速滚动列表
3. 连续搜索
4. 批量选择 1,000 个模组
5. 批量操作 1,000 个模组

**性能指标**:
- 启动加载: < 2 秒
- 搜索响应: < 100 ms
- 滚动帧率: 60 FPS
- 内存占用: < 500 MB
- CPU 空闲: ≈ 0%

**优化点**:
- 图标异步加载
- 图标缓存（LRU）
- 搜索防抖
- 筛选延迟执行
- 虚拟化正确性

**交付物**: 性能测试报告

---

## 四、验收标准

### 4.1 功能验收
- [ ] 模组列表正常显示
- [ ] 虚拟化列表支持 10,000+ 条数据
- [ ] 搜索功能正常工作
- [ ] 筛选功能正常工作
- [ ] 排序功能正常工作
- [ ] 单选和多选正常工作
- [ ] 批量操作正常工作
- [ ] 模组详情面板正常显示
- [ ] 空状态正常显示
- [ ] 错误状态正常显示

### 4.2 性能验收
- [ ] 支持 10,000+ 条模组数据
- [ ] 搜索与筛选不阻塞 UI
- [ ] 滚动不会创建全部列表项
- [ ] 批量启用和禁用不会导致全量刷新
- [ ] 列表状态与后端数据保持一致
- [ ] 大规模数据下没有明显的内存持续增长

### 4.3 代码质量验收
- [ ] 无编译错误
- [ ] 无运行时异常
- [ ] MVVM 架构正确
- [ ] 事件订阅有取消订阅
- [ ] 异步方法正确使用 async/await

---

## 五、风险评估

| 风险 | 等级 | 缓解措施 |
|-----|------|---------|
| 虚拟化实现复杂 | 🟡 中 | 使用 Avalonia 内置虚拟化 |
| 10,000+ 数据性能问题 | 🟡 中 | 分页加载 + 虚拟化 + 缓存 |
| 图标加载影响性能 | 🟡 中 | 异步加载 + LRU 缓存 |
| 批量操作耗时 | 🟡 中 | 后台任务 + 进度反馈 |
| 搜索频繁触发 | 🟢 低 | 防抖机制 (300ms) |

---

## 六、时间估算

| 任务 | 预计时间 |
|-----|---------|
| 2.1 审查服务 | 0.5h |
| 2.2 设计 ViewModel | 1h |
| 2.3 实现 ModItemViewModel | 0.5h |
| 2.4 实现列表 AXAML | 3h |
| 2.5 实现虚拟化 | 2h |
| 2.6 实现列表项模板 | 1h |
| 2.7 实现搜索 | 1h |
| 2.8 实现筛选 | 1h |
| 2.9 实现排序 | 0.5h |
| 2.10 实现批量操作 | 1h |
| 2.11 实现详情面板 | 2h |
| 2.12 实现空/错误状态 | 0.5h |
| 2.13 性能测试优化 | 2h |
| **总计** | **16 小时** |

预计工作日: 2 天

---

## 七、下一步

完成阶段 2 后，进入阶段 3 - 配置文件页面。

---

**状态**: 📋 计划已制定，等待执行  
**开始时间**: 2026-10-03
