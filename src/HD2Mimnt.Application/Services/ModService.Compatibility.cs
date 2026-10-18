using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Application.Services;

public partial class ModService
{
    /// <summary>与导入、更新、版本切换共用写入闸门；修复后使内容缓存失效并通知库刷新。</summary>
    internal async Task<CompatibilityRepairResult> MutateCompatibilityAsync(Guid id,
        Func<string, Task<CompatibilityRepairResult>> mutation, CancellationToken token)
    {
        await _mutationGate.WaitAsync(token);
        try
        {
            var mod = await _modRepository.GetByIdAsync(id, token)
                ?? throw new KeyNotFoundException($"模组 {id} 不存在。");
            var result = await mutation(mod.Path);
            if (!result.Changed) return result;
            mod.Hash = string.Empty;
            mod.BslRole = null;
            mod.BslDetectionVersion = 0;
            mod.ResourceTypeIds = null;
            mod.ModifiedAt = DateTime.UtcNow;
            // 文件提交完成后必须同步缓存，即使用户在此时请求取消。
            await _modRepository.UpdateAsync(mod, CancellationToken.None);
            RaiseModsChanged();
            return result;
        }
        finally { _mutationGate.Release(); }
    }
}
