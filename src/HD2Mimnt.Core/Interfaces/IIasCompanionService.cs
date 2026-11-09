using HD2Mimnt.Core.Hd2IAS;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// HD2IAS armor 配套生成服务：调用上游签名 Python 生成器，把当前部署中的 IAS 模组
/// 重新生成为 Runtime\&lt;随机ID&gt;\armor 下的完整配套（rig/catalog/profiles/scene-catalog 等），
/// 并以与上游兼容的事务格式落盘。上游系统未安装 armor 组件时无法生成。
/// </summary>
public interface IIasCompanionService
{
    /// <summary>读取配套环境状态；未配置 IAS 位置或无 Runtime 目录时 RuntimeResolved=false。</summary>
    Task<IasCompanionStatus> GetStatusAsync(CancellationToken token = default);

    /// <summary>
    /// 按当前部署记录中的 IAS 模组重新生成 armor 配套。需要上游 armor 组件已部署；
    /// 生成阶段可取消，事务落盘阶段不可取消。同时清理旧版直写部署残留的文件。
    /// gamePath 为空时自动解析当前生效的游戏目录。
    /// </summary>
    Task<IasCompanionResult> RegenerateAsync(string? gamePath,
        IProgress<string>? progress = null, CancellationToken token = default);
}
