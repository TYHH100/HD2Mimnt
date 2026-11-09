using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>自动拆包 Lua 处理记录的持久化存储。实现必须线程安全，可从后台线程并发调用。</summary>
public interface ILuaExtractionStore
{
    /// <summary>按 ModId 整行覆盖写入。</summary>
    Task UpsertAsync(LuaExtractionResult result, CancellationToken cancellationToken = default);

    /// <summary>读取全部记录；实现应顺带清理模组已删除的孤儿行。</summary>
    Task<IReadOnlyList<LuaExtractionResult>> GetAllAsync(CancellationToken cancellationToken = default);
}
