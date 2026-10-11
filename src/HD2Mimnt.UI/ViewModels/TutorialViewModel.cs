using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>应用内功能引导的步骤状态。</summary>
public partial class TutorialViewModel : ObservableObject
{
    public sealed record TutorialStep(string Title, string Description, int PageIndex, string TargetKey,
        string? Category = null);

    public IReadOnlyList<TutorialStep> Steps { get; } =
    [
        new("先确认游戏目录", "选择 Helldivers 2 安装根目录（包含 bin、data、tools），也可以使用自动识别。验证后点击“保存设置”，模组会部署到游戏的 data 子目录。", 4, "GamePathInput", "游戏"),
        new("选择部署方式", "复制会生成独立文件；硬链接要求模组库与游戏位于同一磁盘卷；符号链接通常需要管理员权限。选择适合你的方式，保存后应用于下一次部署。", 4, "DeploymentMethodInput", "部署"),
        new("把模组导入模组库", "“添加模组”用于导入模组压缩包。也可以把文件拖入窗口批量导入。模组库保存模组资产，导入本身不会修改游戏文件。", 1, "ImportModButton"),
        new("查找与选择模组", "搜索可匹配名称、作者、描述，也支持 tag:标签。单击聚焦；Ctrl 点击增减选择，Shift 选择范围，Ctrl+A 全选。选中后可使用批量操作加入配置文件。", 1, "SearchBox"),
        new("创建模组组合", "配置文件保存一套独立的模组组合和选项。输入名称后新建；默认配置固定在第一位。右键已有配置可以复制、重命名或设为当前。", 0, "CreateProfilePanel"),
        new("加入配置文件", "从模组库选好模组后，也可以在这里打开“添加模组”，把库中的模组加入正在查看的配置文件。加入配置文件不等于部署到游戏。", 0, "AddProfileModButton"),
        new("启用、选项与顺序", "卡片开关决定是否参与部署。“含选项”的模组可打开选项按钮调整变体；版本号前的数字是位置。拖动排序，或选中后右键输入目标位置。Lua Mods 会特别标记，Lua 依赖与加载器位于固定分组。配置中暂无模组时，添加后即可看到卡片。", 0, "ProfileTutorialCard"),
        new("部署前检查冲突", "检查当前配置中已启用模组的文件覆盖关系。结果需要结合部署方向判断；修改模组、选项或顺序后应重新检查。教程只介绍按钮，不会自动执行扫描。", 2, "ScanConflictsButton"),
        new("部署当前配置", "将正在查看的配置中已启用模组部署到游戏，使用设置中保存的部署方式。列表默认从上到下部署，靠后的模组覆盖同名资源；反向部署时顺序相反。教程不会执行部署。", 0, "DeployCurrentProfileButton"),
        new("清理已部署文件", "移除管理器追踪的已部署游戏文件，模组库中的原始资产仍保留。清理用于撤下当前部署；删除库里的模组是另一个操作。", 0, "CleanupDeploymentButton"),
        new("启动 HD2", "完成部署后可通过 Steam 启动 Helldivers 2，保留 Steam 的启动选项。这个按钮也可以独立启动游戏，教程不会替你启动。", 0, "LaunchHd2Button"),
        new("查看任务进度", "任务中心集中显示导入、部署、扫描等任务的进度和结果，支持取消可取消的任务。教程结束后，你可以从这个入口打开任务中心。", 0, "TaskCenterButton"),
        new("查看通知", "通知保存操作结果和错误提示，横幅消失后仍可在历史中查看。遇到失败时先查看提示，再决定是否重试。", 0, "NotificationsButton"),
        new("备份管理数据", "在工具页创建数据库和设置备份。恢复前会要求确认；这里介绍的是管理器数据备份，不是游戏安装文件的完整备份。", 3, "CreateBackupButton", "备份与恢复"),
    ];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _currentStepIndex;

    public TutorialStep CurrentStep => Steps[Math.Clamp(CurrentStepIndex, 0, Steps.Count - 1)];
    public bool HasPrevious => CurrentStepIndex > 0;
    public bool HasNext => CurrentStepIndex < Steps.Count - 1;
    public string StepCounter => $"{CurrentStepIndex + 1} / {Steps.Count}";
    public string NextLabel => HasNext ? "下一步" : "完成";

    [RelayCommand]
    private void Start() { CurrentStepIndex = 0; IsOpen = true; }

    [RelayCommand]
    private void Next()
    {
        if (!IsOpen) return;
        if (HasNext) CurrentStepIndex++;
        else IsOpen = false;
    }

    [RelayCommand]
    private void Previous()
    {
        if (!IsOpen) return;
        if (HasPrevious) CurrentStepIndex--;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    partial void OnCurrentStepIndexChanged(int value) => NotifyStepProperties();

    private void NotifyStepProperties()
    {
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(StepCounter));
        OnPropertyChanged(nameof(NextLabel));
    }
}
