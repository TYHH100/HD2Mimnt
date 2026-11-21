using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Application.Cli;

/// <summary>task 命令域：任务中心的查询、取消、强杀、清理与等待，供 AI 后台提交后轮询。</summary>
internal static partial class CliHandlers
{
    internal static class Tasks
    {
        public static async Task<CliCommandResponse> ListAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var limitValue = args.Option("--limit");
            if (args.UnknownFlags() is { Count: > 0 } unknown)
                return CliCommandResponse.Usage("未知选项: " + string.Join(' ', unknown));
            var limit = int.TryParse(limitValue, out var parsedLimit) && parsedLimit > 0 ? parsedLimit : 50;

            var taskService = context.Get<TaskService>();
            var running = (await taskService.GetRunningTasksAsync(ct)).ToList();
            var history = (await taskService.GetHistoryAsync(limit, ct)).ToList();
            var known = new HashSet<Guid>(running.Select(task => task.Id));
            var finished = history.Where(task => !known.Contains(task.Id)).ToList();

            var data = new
            {
                running = running.Select(task => Format(task)).ToList(),
                finished = finished.Select(task => Format(task)).ToList()
            };
            var lines = new List<string>();
            if (running.Count > 0)
            {
                lines.Add("运行中:");
                lines.AddRange(running.Select(task => $"  ▶ {task.Name} [{task.Type}] {task.Stage} {task.Progress:F0}%  {task.Id}"));
            }
            if (finished.Count > 0)
            {
                lines.Add("已结束（最近 " + finished.Count + " 条）:");
                lines.AddRange(finished.Select(task =>
                    $"  {StatusMark(task.Status)} {task.Name} [{task.Status}] {task.StartTime:MM-dd HH:mm:ss}  {task.Id}"));
            }
            if (lines.Count == 0) return CliCommandResponse.Ok("当前没有任务记录。", data);
            return CliCommandResponse.Ok(string.Join("\n", lines), data);
        }

        public static async Task<CliCommandResponse> ShowAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1 || !Guid.TryParse(positional[0], out var taskId))
                return CliCommandResponse.Usage("用法: task show <任务ID>（task list 获取 ID）");
            var task = await context.Get<TaskService>().GetTaskAsync(taskId, ct)
                ?? throw new KeyNotFoundException($"找不到任务 {taskId}。");
            return CliCommandResponse.Ok(Describe(task), Format(task));
        }

        public static async Task<CliCommandResponse> CancelAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1 || !Guid.TryParse(positional[0], out var taskId))
                return CliCommandResponse.Usage("用法: task cancel <任务ID>");
            await context.Get<TaskService>().CancelTaskAsync(taskId, ct);
            return CliCommandResponse.Ok($"已请求取消任务 {taskId}；用 task wait {taskId} 观察终态。");
        }

        public static async Task<CliCommandResponse> KillAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1 || !Guid.TryParse(positional[0], out var taskId))
                return CliCommandResponse.Usage("用法: task kill <任务ID>");
            await context.Get<TaskService>().ForceTerminateTaskAsync(taskId, ct);
            return CliCommandResponse.Ok($"已强制终止任务 {taskId}。");
        }

        public static async Task<CliCommandResponse> ClearAsync(CliCommandContext context, CancellationToken ct)
        {
            var deleted = await context.Get<TaskService>().DeleteFinishedTasksAsync(ct);
            return CliCommandResponse.Ok($"已删除 {deleted} 条已结束的任务记录。", new { deleted });
        }

        public static async Task<CliCommandResponse> WaitAsync(CliCommandContext context, CancellationToken ct)
        {
            var args = new CliArgs(context.Request.Arguments);
            var positional = args.Positionals();
            if (positional.Count != 1 || !Guid.TryParse(positional[0], out var taskId))
                return CliCommandResponse.Usage("用法: task wait <任务ID> [--timeout 秒]");
            if (args.UnknownFlags() is { Count: > 0 } unknown)
                return CliCommandResponse.Usage("未知选项: " + string.Join(' ', unknown));
            var timeoutValue = args.Option("--timeout");
            var timeout = int.TryParse(timeoutValue, out var parsedTimeout) && parsedTimeout > 0 ? parsedTimeout : 600;

            var taskService = context.Get<TaskService>();
            var task = await taskService.GetTaskAsync(taskId, ct)
                ?? throw new KeyNotFoundException($"找不到任务 {taskId}。");
            if (task.EndTime == null)
            {
                using var waitCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(timeout));
                try
                {
                    await taskService.WaitForTaskAsync(taskId, waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return CliCommandResponse.Fail($"等待超时（{timeout} 秒），任务仍在运行；可用 task show {taskId} 查看进度。");
                }
            }
            task = await taskService.GetTaskAsync(taskId, ct) ?? task;
            var response = task.Status is HD2Mimnt.Core.Entities.TaskStatus.Completed or HD2Mimnt.Core.Entities.TaskStatus.PartiallyFailed
                ? CliCommandResponse.Ok(Describe(task), Format(task))
                : CliCommandResponse.Fail(Describe(task), Format(task));
            return response;
        }

        private static object Format(ModTask task) => new
        {
            id = task.Id,
            name = task.Name,
            type = task.Type,
            status = task.Status.ToString(),
            progress = task.Progress,
            stage = task.Stage,
            currentItem = task.CurrentItem,
            message = task.Message,
            startTime = task.StartTime,
            endTime = task.EndTime,
            errors = task.Errors,
            result = task.Result
        };

        private static string Describe(ModTask task)
        {
            var description = $"{task.Name} [{task.Status}] 进度 {task.Progress:F0}%（{task.Stage}）";
            if (!string.IsNullOrEmpty(task.CurrentItem)) description += "\n当前: " + task.CurrentItem;
            if (!string.IsNullOrEmpty(task.Result)) description += "\n结果: " + task.Result;
            if (!string.IsNullOrEmpty(task.Message)) description += "\n消息: " + task.Message;
            if (task.Errors is { Count: > 0 }) description += "\n错误:\n  " + string.Join("\n  ", task.Errors.Take(5));
            return description;
        }

        private static string StatusMark(HD2Mimnt.Core.Entities.TaskStatus status) => status switch
        {
            HD2Mimnt.Core.Entities.TaskStatus.Completed => "✓",
            HD2Mimnt.Core.Entities.TaskStatus.PartiallyFailed => "◑",
            HD2Mimnt.Core.Entities.TaskStatus.Failed => "✗",
            HD2Mimnt.Core.Entities.TaskStatus.Cancelled => "⊘",
            HD2Mimnt.Core.Entities.TaskStatus.ForceTerminated => "⚡",
            _ => "·"
        };
    }
}
