namespace HD2Mimnt.Core.Interfaces;

/// <summary>校验完整映像路径后的游戏进程信息；StartTicks 用于识别 PID 复用。</summary>
public sealed record GameProcessInfo(int ProcessId, string ImagePath, long StartTimeUtcTicks);

/// <summary>按游戏目录定位真正的游戏 EXE，排除启动器、反作弊服务和同名程序。</summary>
public interface IGameProcessLocator
{
    GameProcessInfo? FindGameProcess(string gameDirectory);
}