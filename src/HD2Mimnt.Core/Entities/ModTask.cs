using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Task 是所有后台工作的统一出口
/// 包括：Import、Scan、Validation、Analysis、Deployment、Cleanup、Download、Update、Delete 等
/// </summary>
/// <remarks>属性变更通知用于任务中心原地刷新（不重建列表项，进度条不跳变）；任务执行体持有的实例没有界面订阅者，跨线程赋值不受影响。</remarks>
public class ModTask : INotifyPropertyChanged
{
    private Guid _id;
    private string _name = string.Empty;
    private string _type = string.Empty;
    private TaskStatus _status;
    private double _progress;
    private string _stage = string.Empty;
    private string _currentItem = string.Empty;
    private string _message = string.Empty;
    private DateTime _startTime;
    private DateTime? _endTime;
    private List<string> _errors = new();
    private string _result = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get => _id; set => Set(ref _id, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public TaskStatus Status { get => _status; set => Set(ref _status, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string Stage { get => _stage; set => Set(ref _stage, value); }
    public string CurrentItem { get => _currentItem; set => Set(ref _currentItem, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
    public DateTime StartTime { get => _startTime; set => Set(ref _startTime, value); }
    public DateTime? EndTime { get => _endTime; set => Set(ref _endTime, value); }
    public List<string> Errors { get => _errors; set => Set(ref _errors, value); }
    public string Result { get => _result; set => Set(ref _result, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum TaskStatus
{
    Waiting,
    Running,
    Completed,
    PartiallyFailed,
    Failed,
    Cancelled,
    ForceTerminated
}
