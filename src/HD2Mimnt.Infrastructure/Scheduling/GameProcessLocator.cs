using System.Diagnostics;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Interop;

namespace HD2Mimnt.Infrastructure.Scheduling;

/// <summary>
/// 只按完整映像路径匹配游戏主程序；不使用 Process.MainModule，避免权限异常与误判。
/// </summary>
public sealed class GameProcessLocator : IGameProcessLocator
{
    public GameProcessInfo? FindGameProcess(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
            return null;

        string expected;
        try
        {
            expected = Path.GetFullPath(Path.Combine(gameDirectory, "bin", "helldivers2.exe"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("helldivers2");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id <= 4)
                        continue;
                    if (!ProcessNativeApi.TryGetImagePath(process.Id, out var imagePath, out _) || imagePath is null)
                        continue;

                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(imagePath);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        continue;
                    }

                    if (!string.Equals(fullPath, expected, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return new GameProcessInfo(process.Id, fullPath, TryGetStartTimeUtcTicks(process.Id));
                }
                finally
                {
                    process.Dispose();
                }
            }

            return null;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static long TryGetStartTimeUtcTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }
}