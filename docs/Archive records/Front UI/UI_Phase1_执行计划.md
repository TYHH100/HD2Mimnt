# HD2Mimnt UI 开发 - 阶段 1 执行计划

**阶段名称**: 整体 UI 框架  
**优先级**: P0  
**预计工期**: 3-5 天  
**目标**: 建立完整的主窗口和导航体系

---

## 一、阶段目标

建立完整的主窗口框架，为后续页面开发提供稳定的基础。

### 核心交付物

1. ✅ 完善的主窗口布局
2. ✅ 完整的底部状态栏
3. ✅ 全局任务中心入口
4. ✅ 全局通知系统入口
5. ✅ 游戏状态显示
6. ✅ 统一主题和基础样式
7. ✅ 页面生命周期管理
8. ✅ 缺失的核心服务

---

## 二、详细工作项

### 2.1 创建缺失的核心服务

#### Task 1.1: GameDetectionService ⭐

**文件**: `src/HD2Mimnt.Application/Services/GameDetectionService.cs`

**功能需求**：
```csharp
public class GameDetectionService
{
    // 游戏信息
    public GameInfo? CurrentGameInfo { get; private set; }
    
    // 事件
    public event EventHandler<GameStatusChangedEventArgs>? GameStatusChanged;
    
    // 方法
    Task<GameInfo?> DetectGameAsync(CancellationToken cancellationToken = default);
    Task<bool> ValidateGameDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task<string?> GetGameVersionAsync(string gamePath, CancellationToken cancellationToken = default);
    
    // 自动检测（仅在启动时或用户触发时调用，不要轮询）
    Task AutoDetectAsync();
}

public class GameInfo
{
    public string Path { get; set; }
    public string? Version { get; set; }
    public GameStatus Status { get; set; }
    public DateTime LastChecked { get; set; }
}

public enum GameStatus
{
    NotFound,
    Found,
    Invalid,
    Checking
}
```

**实现要点**：
- ❌ **禁止**使用定时器轮询
- ✅ 仅在启动时检测一次
- ✅ 提供手动刷新方法
- ✅ 使用文件系统监视器监控游戏目录变化（可选）
- ✅ 检测常见安装位置（Steam、Epic、自定义）

**检测路径优先级**：
1. 用户设置的自定义路径
2. Steam 默认路径
3. Epic Games 默认路径
4. 注册表查找

#### Task 1.2: SettingsService ⭐

**文件**: `src/HD2Mimnt.Application/Services/SettingsService.cs`

**功能需求**：
```csharp
public class SettingsService
{
    // 获取设置
    Task<T> GetSettingAsync<T>(string key, T defaultValue);
    
    // 保存设置
    Task SetSettingAsync<T>(string key, T value);
    
    // 批量获取
    Task<Dictionary<string, object>> GetAllSettingsAsync();
    
    // 重置
    Task ResetToDefaultAsync();
    Task ResetCategoryAsync(string category);
    
    // 事件
    event EventHandler<SettingChangedEventArgs>? SettingChanged;
}
```

**设置分类**：
```csharp
public static class SettingKeys
{
    // 游戏
    public const string GamePath = "Game.Path";
    public const string GameAutoDetect = "Game.AutoDetect";
    
    // 部署
    public const string DefaultDeploymentMethod = "Deployment.DefaultMethod";
    public const string DeploymentCheckBeforeDeploy = "Deployment.CheckBefore";
    
    // UI
    public const string Theme = "UI.Theme";
    public const string ListDensity = "UI.ListDensity";
    public const string ShowThumbnails = "UI.ShowThumbnails";
    public const string DefaultSortMode = "UI.DefaultSortMode";
    
    // 性能
    public const string ImageCacheSize = "Performance.ImageCacheSize";
    public const string EnableAnimations = "Performance.EnableAnimations";
}
```

**存储方式**：
- 使用 JSON 文件存储
- 路径：`%LocalAppData%\HD2Mimnt\settings.json`
- 支持类型安全的读写

#### Task 1.3: NotificationService ⭐

**文件**: `src/HD2Mimnt.Application/Services/NotificationService.cs`

**功能需求**：
```csharp
public class NotificationService
{
    // 显示通知
    void ShowSuccess(string message, string? title = null, int durationMs = 3000);
    void ShowInfo(string message, string? title = null, int durationMs = 3000);
    void ShowWarning(string message, string? title = null, int durationMs = 5000);
    void ShowError(string message, string? title = null, int durationMs = 0); // 0 = 手动关闭
    
    // 带操作的通知
    void ShowWithAction(NotificationLevel level, string message, 
                        string actionText, Action action);
    
    // 查询通知
    IEnumerable<Notification> GetRecentNotifications(int limit = 50);
    int GetUnreadCount();
    
    // 管理
    void MarkAsRead(Guid notificationId);
    void Clear();
    
    // 事件
    event EventHandler<Notification>? NotificationAdded;
    event EventHandler<Guid>? NotificationRemoved;
}

public class Notification
{
    public Guid Id { get; set; }
    public NotificationLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Title { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsRead { get; set; }
    public string? ActionText { get; set; }
    public Action? Action { get; set; }
}

public enum NotificationLevel
{
    Success,
    Info,
    Warning,
    Error
}
```

**实现要点**：
- 内存队列，最多保留 100 条
- 自动去重（相同消息在 5 秒内只显示一次）
- 支持自动消失和手动关闭
- 错误通知默认需要手动关闭

### 2.2 注册服务到 DI 容器

**文件**: `src/HD2Mimnt.UI/App.axaml.cs`

**修改 ConfigureServices 方法**：
```csharp
private void ConfigureServices(IServiceCollection services)
{
    // ... 现有代码 ...

    // 部署相关（补充缺失的注册）
    services.AddSingleton<IDeploymentRepository, DeploymentRepository>();
    
    // 注册三种部署方法
    services.AddSingleton<IDeploymentMethod, CopyDeploymentMethod>();
    services.AddSingleton<IDeploymentMethod, SymbolicLinkDeploymentMethod>();
    services.AddSingleton<IDeploymentMethod, HardLinkDeploymentMethod>();
    
    services.AddSingleton(sp => new DeploymentService(
        sp.GetRequiredService<IProfileRepository>(),
        sp.GetRequiredService<IModRepository>(),
        sp.GetRequiredService<IDeploymentRepository>(),
        sp.GetServices<IDeploymentMethod>(),
        sp.GetService<ILogger<DeploymentService>>()));

    // 新增服务
    services.AddSingleton(sp => new GameDetectionService(
        sp.GetRequiredService<SettingsService>(),
        sp.GetService<ILogger<GameDetectionService>>()));
        
    services.AddSingleton(sp => new SettingsService(
        appDataPath,
        sp.GetService<ILogger<SettingsService>>()));
        
    services.AddSingleton<NotificationService>();

    // 更新 MainWindowViewModel 以注入新服务
    services.AddTransient(sp => new MainWindowViewModel(
        sp.GetRequiredService<LibraryViewModel>(),
        sp.GetRequiredService<ProfileViewModel>(),
        sp.GetRequiredService<GameDetectionService>(),
        sp.GetRequiredService<NotificationService>(),
        sp.GetRequiredService<TaskService>()));
}
```

### 2.3 扩展 MainWindowViewModel

**文件**: `src/HD2Mimnt.UI/ViewModels/MainWindowViewModel.cs`

**新增属性**：
```csharp
public partial class MainWindowViewModel : ObservableObject
{
    private readonly GameDetectionService _gameDetectionService;
    private readonly NotificationService _notificationService;
    private readonly TaskService _taskService;

    // 游戏状态
    [ObservableProperty]
    private string _gameStatus = "Detecting...";
    
    [ObservableProperty]
    private bool _gameDetected = false;
    
    [ObservableProperty]
    private string _gamePath = string.Empty;

    // 当前配置文件
    [ObservableProperty]
    private string _currentProfileName = "No Profile";
    
    // 部署状态
    [ObservableProperty]
    private string _deploymentStatus = "Not Deployed";
    
    [ObservableProperty]
    private bool _isDeployed = false;

    // 任务状态
    [ObservableProperty]
    private int _runningTaskCount = 0;
    
    [ObservableProperty]
    private string _taskStatusText = "Idle";

    // 通知状态
    [ObservableProperty]
    private int _unreadNotificationCount = 0;

    // 构造函数
    public MainWindowViewModel(
        LibraryViewModel libraryViewModel,
        ProfileViewModel profileViewModel,
        GameDetectionService gameDetectionService,
        NotificationService notificationService,
        TaskService taskService)
    {
        // ... 现有代码 ...
        
        _gameDetectionService = gameDetectionService;
        _notificationService = notificationService;
        _taskService = taskService;
        
        // 订阅事件
        SubscribeToEvents();
    }

    private void SubscribeToEvents()
    {
        _gameDetectionService.GameStatusChanged += OnGameStatusChanged;
        _notificationService.NotificationAdded += OnNotificationAdded;
        _profileViewModel.ActiveProfileChanged += OnActiveProfileChanged;
    }

    public override void Dispose()
    {
        _gameDetectionService.GameStatusChanged -= OnGameStatusChanged;
        _notificationService.NotificationAdded -= OnNotificationAdded;
        _profileViewModel.ActiveProfileChanged -= OnActiveProfileChanged;
        base.Dispose();
    }

    // 命令
    [RelayCommand]
    private void OpenTaskCenter()
    {
        // TODO: 打开任务中心
    }

    [RelayCommand]
    private void OpenNotifications()
    {
        // TODO: 打开通知面板
    }

    [RelayCommand]
    private async Task RefreshGameStatusAsync()
    {
        await _gameDetectionService.AutoDetectAsync();
    }
}
```

### 2.4 完善主窗口 AXAML

**文件**: `src/HD2Mimnt.UI/MainWindow.axaml`

**新布局结构**：
```xml
<Window ...>
    <Grid RowDefinitions="Auto,*,Auto">
        
        <!-- 顶部导航栏 -->
        <Border Grid.Row="0" ...>
            <Grid ColumnDefinitions="Auto,*,Auto">
                <!-- 左侧：Logo + 标题 -->
                <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="12">
                    <!-- Logo 图标 -->
                    <Border Width="32" Height="32" 
                            Background="#38BDF8" 
                            CornerRadius="8">
                        <TextBlock Text="HD2" 
                                   FontSize="12" 
                                   FontWeight="Bold"
                                   Foreground="White"
                                   HorizontalAlignment="Center"
                                   VerticalAlignment="Center"/>
                    </Border>
                    <TextBlock Text="HD2Mimnt" ... />
                </StackPanel>

                <!-- 中央：导航按钮 -->
                <StackPanel Grid.Column="1" ...>
                    <!-- 使用 RadioButton 实现互斥选中 -->
                    <RadioButton Content="Library" 
                                GroupName="Navigation"
                                IsChecked="{Binding SelectedTabIndex, 
                                           Converter={StaticResource EqualConverter}, 
                                           ConverterParameter=0}"
                                Command="{Binding NavigateToLibraryCommand}"
                                Classes="nav-button"/>
                    <!-- 其他导航按钮 -->
                </StackPanel>

                <!-- 右侧：游戏状态 + 任务中心 + 通知 -->
                <StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="12">
                    <!-- 游戏状态 -->
                    <Button Classes="icon-button" 
                            ToolTip.Tip="{Binding GameStatus}"
                            Command="{Binding RefreshGameStatusCommand}">
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <Ellipse Width="8" Height="8" 
                                    Fill="{Binding GameDetected, 
                                           Converter={StaticResource BoolToColorConverter}}"/>
                            <TextBlock Text="{Binding GameStatus}" FontSize="12"/>
                        </StackPanel>
                    </Button>
                    
                    <!-- 任务中心 -->
                    <Button Classes="icon-button" 
                            Command="{Binding OpenTaskCenterCommand}"
                            ToolTip.Tip="Task Center">
                        <Panel>
                            <TextBlock Text="⚙" FontSize="18"/>
                            <!-- 运行中任务数量徽章 -->
                            <Border Width="16" Height="16"
                                    Background="#EF4444"
                                    CornerRadius="8"
                                    IsVisible="{Binding RunningTaskCount, 
                                               Converter={StaticResource GreaterThanZeroConverter}}"
                                    HorizontalAlignment="Right"
                                    VerticalAlignment="Top"
                                    Margin="0,-4,-4,0">
                                <TextBlock Text="{Binding RunningTaskCount}"
                                          FontSize="10"
                                          Foreground="White"
                                          HorizontalAlignment="Center"
                                          VerticalAlignment="Center"/>
                            </Border>
                        </Panel>
                    </Button>
                    
                    <!-- 通知 -->
                    <Button Classes="icon-button" 
                            Command="{Binding OpenNotificationsCommand}"
                            ToolTip.Tip="Notifications">
                        <Panel>
                            <TextBlock Text="🔔" FontSize="18"/>
                            <!-- 未读通知数量徽章 -->
                            <Border Width="16" Height="16"
                                    Background="#EF4444"
                                    CornerRadius="8"
                                    IsVisible="{Binding UnreadNotificationCount, 
                                               Converter={StaticResource GreaterThanZeroConverter}}"
                                    HorizontalAlignment="Right"
                                    VerticalAlignment="Top"
                                    Margin="0,-4,-4,0">
                                <TextBlock Text="{Binding UnreadNotificationCount}"
                                          FontSize="10"
                                          Foreground="White"
                                          HorizontalAlignment="Center"
                                          VerticalAlignment="Center"/>
                            </Border>
                        </Panel>
                    </Button>
                </StackPanel>
            </Grid>
        </Border>

        <!-- 主内容区域 -->
        <Border Grid.Row="1" ...>
            <!-- 现有 TabControl -->
        </Border>

        <!-- 底部状态栏 (新增) -->
        <Border Grid.Row="2" 
                Background="#0F131C" 
                BorderBrush="#1E2636" 
                BorderThickness="0,1,0,0" 
                Padding="16,8">
            <Grid ColumnDefinitions="Auto,*,Auto">
                <!-- 左侧：当前配置 -->
                <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="8">
                    <TextBlock Text="Profile:" 
                              Foreground="#6B7280" 
                              FontSize="12"/>
                    <TextBlock Text="{Binding CurrentProfileName}" 
                              Foreground="#E0E0E0" 
                              FontSize="12"
                              FontWeight="Medium"/>
                </StackPanel>

                <!-- 中间：部署状态 -->
                <StackPanel Grid.Column="1" 
                           Orientation="Horizontal" 
                           Spacing="8"
                           HorizontalAlignment="Center">
                    <TextBlock Text="Deployment:" 
                              Foreground="#6B7280" 
                              FontSize="12"/>
                    <Ellipse Width="6" Height="6" 
                            Fill="{Binding IsDeployed, 
                                   Converter={StaticResource BoolToColorConverter}}"/>
                    <TextBlock Text="{Binding DeploymentStatus}" 
                              Foreground="#E0E0E0" 
                              FontSize="12"/>
                </StackPanel>

                <!-- 右侧：任务状态 -->
                <StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="8">
                    <TextBlock Text="Status:" 
                              Foreground="#6B7280" 
                              FontSize="12"/>
                    <TextBlock Text="{Binding TaskStatusText}" 
                              Foreground="#E0E0E0" 
                              FontSize="12"/>
                </StackPanel>
            </Grid>
        </Border>
    </Grid>
</Window>
```

### 2.5 扩展应用样式

**文件**: `src/HD2Mimnt.UI/App.axaml`

**新增样式**：
```xml
<Application.Styles>
    <!-- 现有样式 ... -->

    <!-- 图标按钮 -->
    <Style Selector="Button.icon-button">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Foreground" Value="#9CA3AF"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Padding" Value="8"/>
        <Setter Property="CornerRadius" Value="6"/>
        <Setter Property="Cursor" Value="Hand"/>
    </Style>

    <Style Selector="Button.icon-button:pointerover">
        <Setter Property="Background" Value="#161D2B"/>
        <Setter Property="Foreground" Value="#E0E0E0"/>
    </Style>

    <!-- 状态徽章 -->
    <Style Selector="Border.badge">
        <Setter Property="Background" Value="#EF4444"/>
        <Setter Property="CornerRadius" Value="999"/>
        <Setter Property="MinWidth" Value="18"/>
        <Setter Property="MinHeight" Value="18"/>
        <Setter Property="Padding" Value="4,2"/>
    </Style>

    <!-- 状态指示器 -->
    <Style Selector="Ellipse.status-indicator">
        <Setter Property="Width" Value="8"/>
        <Setter Property="Height" Value="8"/>
    </Style>

    <!-- RadioButton 作为导航按钮 -->
    <Style Selector="RadioButton.nav-button">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Foreground" Value="#9CA3AF"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Padding" Value="16,8"/>
        <Setter Property="FontSize" Value="14"/>
        <Setter Property="FontWeight" Value="Medium"/>
        <Setter Property="CornerRadius" Value="6"/>
        <Setter Property="Cursor" Value="Hand"/>
    </Style>

    <Style Selector="RadioButton.nav-button:pointerover">
        <Setter Property="Background" Value="#161D2B"/>
        <Setter Property="Foreground" Value="#E0E0E0"/>
    </Style>

    <Style Selector="RadioButton.nav-button:checked">
        <Setter Property="Background" Value="#38BDF8"/>
        <Setter Property="Foreground" Value="#FFFFFF"/>
    </Style>
</Application.Styles>
```

### 2.6 创建值转换器

**文件**: `src/HD2Mimnt.UI/Converters/Converters.cs`

```csharp
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace HD2Mimnt.UI.Converters;

public class BoolToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isTrue)
        {
            return isTrue 
                ? new SolidColorBrush(Color.Parse("#10B981")) // Green
                : new SolidColorBrush(Color.Parse("#6B7280")); // Gray
        }
        return new SolidColorBrush(Color.Parse("#6B7280"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class GreaterThanZeroConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int count)
        {
            return count > 0;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class EqualConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value != null && parameter != null)
        {
            return value.ToString() == parameter.ToString();
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
```

**注册转换器** (在 App.axaml):
```xml
<Application.Resources>
    <converters:BoolToColorConverter x:Key="BoolToColorConverter"/>
    <converters:GreaterThanZeroConverter x:Key="GreaterThanZeroConverter"/>
    <converters:EqualConverter x:Key="EqualConverter"/>
</Application.Resources>
```

### 2.7 创建通知 UI 组件

**文件**: `src/HD2Mimnt.UI/Controls/NotificationPanel.axaml`

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="HD2Mimnt.UI.Controls.NotificationPanel">
    <Border Background="#0F131C"
            BorderBrush="#374151"
            BorderThickness="1"
            CornerRadius="8"
            Padding="16"
            Width="360"
            MaxHeight="600">
        <Grid RowDefinitions="Auto,*,Auto">
            <!-- 标题 -->
            <Grid Grid.Row="0" ColumnDefinitions="*,Auto" Margin="0,0,0,12">
                <TextBlock Grid.Column="0" 
                          Text="Notifications" 
                          FontSize="16" 
                          FontWeight="SemiBold"
                          Foreground="#E0E0E0"/>
                <Button Grid.Column="1" 
                        Content="Clear All" 
                        Classes="secondary"
                        Command="{Binding ClearAllCommand}"/>
            </Grid>

            <!-- 通知列表 -->
            <ScrollViewer Grid.Row="1">
                <ItemsControl ItemsSource="{Binding Notifications}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <Border Background="#161D2B"
                                    BorderBrush="#374151"
                                    BorderThickness="1"
                                    CornerRadius="6"
                                    Padding="12"
                                    Margin="0,0,0,8">
                                <Grid ColumnDefinitions="Auto,*,Auto">
                                    <!-- 图标 -->
                                    <Border Grid.Column="0"
                                            Width="32"
                                            Height="32"
                                            CornerRadius="16"
                                            Background="{Binding Level, 
                                                        Converter={StaticResource LevelToColorConverter}}"
                                            VerticalAlignment="Top">
                                        <TextBlock Text="{Binding Level, 
                                                         Converter={StaticResource LevelToIconConverter}}"
                                                  FontSize="16"
                                                  Foreground="White"
                                                  HorizontalAlignment="Center"
                                                  VerticalAlignment="Center"/>
                                    </Border>

                                    <!-- 内容 -->
                                    <StackPanel Grid.Column="1" Margin="12,0">
                                        <TextBlock Text="{Binding Title}"
                                                  FontSize="14"
                                                  FontWeight="Medium"
                                                  Foreground="#E0E0E0"
                                                  IsVisible="{Binding Title, 
                                                             Converter={StaticResource NotNullConverter}}"/>
                                        <TextBlock Text="{Binding Message}"
                                                  FontSize="12"
                                                  Foreground="#9CA3AF"
                                                  TextWrapping="Wrap"/>
                                        <TextBlock Text="{Binding Timestamp, 
                                                         Converter={StaticResource TimeAgoConverter}}"
                                                  FontSize="10"
                                                  Foreground="#6B7280"
                                                  Margin="0,4,0,0"/>
                                    </StackPanel>

                                    <!-- 关闭按钮 -->
                                    <Button Grid.Column="2"
                                            Content="×"
                                            Classes="icon-button"
                                            Command="{Binding $parent[ItemsControl].((vm:MainWindowViewModel)DataContext).RemoveNotificationCommand}"
                                            CommandParameter="{Binding Id}"/>
                                </Grid>
                            </Border>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </ScrollViewer>

            <!-- 空状态 -->
            <StackPanel Grid.Row="1"
                       IsVisible="{Binding !Notifications.Count}"
                       HorizontalAlignment="Center"
                       VerticalAlignment="Center"
                       Spacing="8">
                <TextBlock Text="No notifications"
                          FontSize="14"
                          Foreground="#6B7280"
                          HorizontalAlignment="Center"/>
            </StackPanel>
        </Grid>
    </Border>
</UserControl>
```

---

## 三、验收标准

### 3.1 功能验收

- [ ] 主窗口可以正常启动
- [ ] 四个一级导航可以正常切换
- [ ] 导航按钮具有正确的选中状态
- [ ] 底部状态栏正常显示
- [ ] 游戏状态可以检测并显示
- [ ] 任务中心入口正常显示
- [ ] 通知入口正常显示
- [ ] 页面切换不会重复初始化全局状态
- [ ] 窗口缩放正常
- [ ] 高 DPI 下布局正常

### 3.2 服务验收

- [ ] GameDetectionService 可以检测游戏
- [ ] SettingsService 可以保存和读取设置
- [ ] NotificationService 可以显示通知
- [ ] 所有服务正确注册到 DI 容器
- [ ] DeploymentService 可以正常创建

### 3.3 性能验收

- [ ] 页面切换响应时间 < 100ms
- [ ] 游戏检测不阻塞 UI
- [ ] 通知显示不阻塞 UI
- [ ] 无明显内存泄漏

### 3.4 代码质量验收

- [ ] 无编译错误
- [ ] 无运行时异常
- [ ] ViewModel 不直接访问 UI 控件
- [ ] 所有异步方法使用 async/await
- [ ] 事件订阅有对应的取消订阅

---

## 四、测试计划

### 4.1 手动测试用例

#### TC1: 窗口启动
1. 启动应用
2. 验证主窗口显示
3. 验证底部状态栏显示
4. 验证游戏检测开始

#### TC2: 导航切换
1. 点击每个导航按钮
2. 验证选中状态正确
3. 验证页面内容切换
4. 验证状态栏更新

#### TC3: 游戏检测
1. 设置有效的游戏路径
2. 点击刷新游戏状态
3. 验证状态更新为 "Found"
4. 验证路径显示正确

#### TC4: 通知系统
1. 触发成功通知
2. 触发错误通知
3. 验证通知显示
4. 验证自动消失（成功通知）
5. 验证手动关闭（错误通知）

### 4.2 集成测试

```csharp
[Fact]
public async Task MainWindow_ShouldInitializeAllServices()
{
    // Arrange
    var services = CreateServiceProvider();
    
    // Act
    var viewModel = services.GetRequiredService<MainWindowViewModel>();
    await viewModel.InitializeAsync();
    
    // Assert
    Assert.NotNull(viewModel);
    Assert.NotNull(viewModel.GameDetectionService);
    Assert.NotNull(viewModel.NotificationService);
    Assert.NotNull(viewModel.TaskService);
}
```

---

## 五、已知风险和缓解措施

| 风险 | 等级 | 缓解措施 |
|-----|------|---------|
| 游戏检测路径不准确 | 🟡 中 | 提供手动设置入口 |
| 通知过多造成干扰 | 🟢 低 | 自动去重和限制显示数量 |
| 状态栏更新频率过高 | 🟢 低 | 使用防抖机制 |
| DI 注册顺序问题 | 🟡 中 | 严格按照依赖顺序注册 |

---

## 六、完成标志

✅ **阶段 1 完成条件**：

1. 所有 3 个核心服务已创建并测试通过
2. 主窗口布局完整（包含底部状态栏）
3. 导航系统正常工作
4. 游戏状态可以检测和显示
5. 通知系统可以正常工作
6. 任务中心入口已创建（功能在阶段 5 实现）
7. 所有样式统一且符合设计规范
8. 无编译错误和运行时异常
9. 手动测试用例全部通过

---

**预计完成时间**: 3-5 天  
**下一阶段**: 阶段 2 - 模组库核心页面