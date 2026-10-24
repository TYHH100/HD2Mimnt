namespace HD2Mimnt.Core.Entities;

/// <summary>库内保留的一份完整内容快照，不创建额外模组或配置成员。</summary>
public sealed record LocalModVersion(Guid Id, DateTime SavedAt, Mod Content);
