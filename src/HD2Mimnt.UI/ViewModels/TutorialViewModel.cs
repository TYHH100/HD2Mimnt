using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>应用内功能引导的步骤状态。内置步骤已移除，之后由插件向 <see cref=”Steps”/> 注入步骤。</summary>
public partial class TutorialViewModel : ObservableObject
{
    public sealed record TutorialStep(string Title, string Description, int PageIndex, string TargetKey,
        string? Category = null);

    public IReadOnlyList<TutorialStep> Steps { get; set; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _currentStepIndex;

    public TutorialStep? CurrentStep => Steps.Count == 0 ? null : Steps[Math.Clamp(CurrentStepIndex, 0, Steps.Count - 1)];
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
