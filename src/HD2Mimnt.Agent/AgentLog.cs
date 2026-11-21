namespace HD2Mimnt.Agent;

/// <summary>
/// Agent 轻量文件日志：daemon 无窗口无控制台，故障排查只能靠日志文件。
/// 容器构建完成后用真实 AppData 路径初始化；之前的早期失败记到系统临时目录兜底。
/// </summary>
public static class AgentLog
{
    private static readonly object Gate = new();
    private static string? _filePath;

    /// <summary>用已解析的应用数据目录初始化日志文件（Logs/agent.log）。</summary>
    public static void Initialize(string appDataPath)
    {
        lock (Gate)
        {
            try
            {
                var dir = Path.Combine(appDataPath, "Logs");
                Directory.CreateDirectory(dir);
                _filePath = Path.Combine(dir, "agent.log");
            }
            catch
            {
                _filePath = Path.Combine(Path.GetTempPath(), "HD2Mimnt-agent.log");
            }
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var path = _filePath ?? Path.Combine(Path.GetTempPath(), "HD2Mimnt-agent.log");
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [PID {Environment.ProcessId}] {message}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败不影响服务本身。
            }
        }
    }

    public static void Write(string message, Exception exception)
        => Write(message + " :: " + exception.GetType().Name + ": " + exception.Message
            + Environment.NewLine + "    at " + (exception.StackTrace ?? "(无堆栈)").Replace(Environment.NewLine, Environment.NewLine + "    at "));
}
