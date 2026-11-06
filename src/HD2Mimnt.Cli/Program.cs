using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Application.Cli;

namespace HD2Mimnt.Cli;

/// <summary>
/// HD2Mimnt 命令行入口。管理器 UI 在运行时把命令转发给它，在 UI 进程内复用同一份状态；
/// 未运行时由 <see cref="HeadlessHost"/> 在 CLI 进程内直接执行，不要求先打开管理器。
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions ResultOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions PacketOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 单次管道探测超时：管理器未运行时这是每条命令的固定等待（见 TryConnectAsync 的 remarks）。
    private static readonly TimeSpan s_ConnectProbeTimeout = TimeSpan.FromMilliseconds(300);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var json = false;
        var yes = false;
        int? timeoutSeconds = null;
        var tokens = ParseTokens(args, out json, out yes, out timeoutSeconds);
        var catalog = CliCommandCatalog.Build();
        if (tokens.Count == 0 || tokens[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            var helpText = tokens.Count >= 2
                ? CliCommandCatalog.CommandHelpText(JoinTokens(tokens))
                : catalog.BuildHelpText();
            return Print(json, new CliPipeResult(0, helpText, null, false));
        }

        // launch 在本地处理：管理器已在运行则直接确认，否则拉起并等待就绪。
        if (tokens[0].Equals("launch", StringComparison.OrdinalIgnoreCase))
        {
            await using var probe = await TryConnectAsync(s_ConnectProbeTimeout);
            if (probe != null) return Print(json, new CliPipeResult(0, "管理器已在运行，CLI 管道就绪。", null, false));
            return await LaunchAsync(json);
        }

        var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? 600);
        var packet = new CliPipePacket(tokens, json, yes);

        // 管理器在运行时仍交给它执行，保持与图形界面同一份内存状态；没运行则直接在本进程执行。
        await using var client = await TryConnectAsync(s_ConnectProbeTimeout);
        if (client != null)
        {
            var result = await SendOverPipeAsync(client, packet, timeout);
            return Print(json, result ?? new CliPipeResult(1,
                "与运行中的管理器通信中断，命令可能已执行。请先确认结果，避免重复执行破坏性命令；也可关闭管理器后重试以直接在本进程执行。",
                null, false));
        }

        return Print(json, await RunHeadlessAsync(tokens, json, yes, timeout));
    }

    /// <summary>尝试连接管理器管道；未运行或监听尚未就绪时返回 null。</summary>
    /// <remarks>
    /// 管理器未运行时，.NET 的 ConnectAsync 连接不存在的管道不会立即失败，而是等满超时才抛
    /// OperationCanceledException（实测 .NET 10 行为）。超时取值必须保守：这是无 UI 环境下
    /// 每条 CLI 命令的固定开销，3 次 × 2 秒的旧取值会让每条命令白等约 6.5 秒。
    /// 监听实例重建是毫秒级窗口，300 毫秒 × 3 次足够覆盖。
    /// </remarks>
    private static async Task<NamedPipeClientStream?> TryConnectAsync(TimeSpan connectTimeout)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var client = new NamedPipeClientStream(".", CliProtocol.GetPipeName(), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                using var cts = new CancellationTokenSource(connectTimeout);
                await client.ConnectAsync(cts.Token);
                return client;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                client.Dispose();
                // 管理器两条连接之间会重建监听实例，短暂重试避免误判为未运行。
                if (attempt < 2) await Task.Delay(200);
            }
        }
        return null;
    }

    /// <summary>在已连接的管道上执行一次请求；连接中断返回 null，等待超时返回明确的失败结果。</summary>
    private static async Task<CliPipeResult?> SendOverPipeAsync(NamedPipeClientStream client, CliPipePacket packet, TimeSpan timeout)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(packet, PacketOptions);
            if (payload.Length > 64 * 1024)
                return new CliPipeResult(2, "请求超出管道上限，请缩小参数规模。", null, false);

            using var timeoutCts = new CancellationTokenSource(timeout);
            var size = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length);
            await client.WriteAsync(size, timeoutCts.Token);
            await client.WriteAsync(payload, timeoutCts.Token);

            var responseSize = new byte[4];
            await client.ReadExactlyAsync(responseSize, timeoutCts.Token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(responseSize);
            if (length <= 0 || length > CliProtocol.MaxResponseBytes)
                return new CliPipeResult(1, "管理器返回了无效响应。", null, false);
            var response = new byte[length];
            await client.ReadExactlyAsync(response, timeoutCts.Token);
            return JsonSerializer.Deserialize<CliPipeResult>(response, ResultOptions);
        }
        catch (OperationCanceledException)
        {
            return new CliPipeResult(1, "等待管理器响应超时，命令可能仍在执行。可用 --timeout 加大等待时间。", null, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>在 CLI 进程内直接执行命令（管理器未运行时的默认路径）。</summary>
    private static async Task<CliPipeResult> RunHeadlessAsync(
        IReadOnlyList<string> tokens, bool json, bool yes, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var response = await HeadlessHost.ExecuteAsync(tokens, json, yes, cts.Token);
            return new CliPipeResult(response.ExitCode, response.Message, response.Data, response.RequiresYes);
        }
        catch (OperationCanceledException)
        {
            return new CliPipeResult(1, $"命令执行超时（{timeout.TotalSeconds:F0} 秒），已中止。", null, false);
        }
        catch (Exception ex)
        {
            return new CliPipeResult(1, "命令执行失败：" + ex.Message, null, false);
        }
    }

    /// <summary>分离全局选项与命令 token；--timeout 非法时抛用法异常。</summary>
    private static List<string> ParseTokens(string[] args, out bool json, out bool yes, out int? timeoutSeconds)
    {
        json = false;
        yes = false;
        timeoutSeconds = null;
        var tokens = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (token == "--json") { json = true; continue; }
            if (token == "--yes" || token == "-y") { yes = true; continue; }
            if (token == "--timeout")
            {
                if (index + 1 >= args.Length || !int.TryParse(args[++index], out var parsed) || parsed <= 0)
                    throw new ArgumentException("--timeout 需要正整数秒数。");
                timeoutSeconds = parsed;
                continue;
            }
            tokens.Add(token);
        }
        return tokens;
    }

    private static string JoinTokens(List<string> tokens) => string.Join(' ', tokens.Skip(1));

    /// <summary>启动管理器 UI 并等待 CLI 管道就绪（上限 60 秒）。</summary>
    private static async Task<int> LaunchAsync(bool json)
    {
        var uiExe = UiLauncher.Find();
        if (uiExe == null)
            return Print(json, new CliPipeResult(1,
                "未找到 HD2Mimnt.UI.exe。请把 CLI 与管理器放在同一目录，或用环境变量 HD2MIMNT_UI_EXE 指定主程序路径。", null, false));
        if (!UiLauncher.TryStart(uiExe))
            return Print(json, new CliPipeResult(1, "启动管理器失败。", null, false));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cts.Token);
                await using var client = new NamedPipeClientStream(".", CliProtocol.GetPipeName(), PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(TimeSpan.FromSeconds(1), cts.Token);
                // 管道就绪即可认为服务在运行；启动成功的确认交给后续命令。
                return Print(json, new CliPipeResult(0, "管理器已启动，CLI 管道就绪。", null, false));
    }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                // 还在启动中，继续轮询。
            }
        }
        return Print(json, new CliPipeResult(1, "管理器启动超时（60 秒内 CLI 管道未就绪）。", null, false));
    }

    private static int Print(bool json, CliPipeResult result)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, PacketOptions));
        }
        else
        {
            if (result.ExitCode == 2) Console.Error.WriteLine(result.Message);
            else Console.WriteLine(result.Message);
        }
        return result.ExitCode;
    }
}
