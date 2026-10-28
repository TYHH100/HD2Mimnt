# 兼容性检查参考来源

本模块参考 TYHH100 的 Helldivers2ModManager 中 `Services/VersionCheck/VersionCheckService.cs`、`VersionCheckRepairService.cs`、`VersionCheckShared.cs`、`VersionCheckAssistedRepairService.cs` 和 `GameUnitReferenceReader.cs` 的格式研究。`CompatibilityResourceTransforms.cs` 选择性适配其已知材质、LOD 和 Legacy Layout 转换算法，保留本文件及 Apache 2.0 全文。另复制并适配以下模型预览核心；未引入旧管理器运行程序集或 WPF UI。

2026-10-18 修改说明：检查、完整 Unit 定位缓存、调用内 DSAR 解码缓存、伴随来源验证、批量编排、事务发布及回滚由 HD2Mimnt 重新实现。转换增加精确 schema 匹配、对齐和结果复检；自动策略逐 Unit 决策，保留版本已匹配的作者 LOD，拒绝未知新版本降级。旧管理器作为格式及已知规则参考，不沿用扫描和缓存流程。

游戏参考 Unit 的内部结束位置采用旧版的范围语义，接受 TOC 条目内结束记录之后的尾部数据；不要求内部结束位置恰好等于整个条目的尺寸。仍校验结束位置下界、读取范围、LOD 范围及参考歧义，并保留现有索引与按需随机读取流程。

- 项目：https://github.com/TYHH100/Helldivers2ModManager
- 原项目许可证：[Apache License 2.0](LICENSE.txt)。保留原许可证全文；未引入旧管理器运行程序集或 UI。
- 本地参考路径：`D:/TYHH10-git/Helldivers2ModManager/`。
- 本地仓库 HEAD：`65448e7e89ca8a19f1b8864d48758cd5e96cfdd1`（读取的文件以本地工作区为准）。

旧管理器注释还列出 [hd2-repatcher](https://github.com/RaidingForPants/hd2-repatcher) 和 [HD2SDK-CommunityEdition](https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition) 作为补丁格式研究来源。本次未复制这两个项目的源文件或发布物。


前端接入仅参考 Components/VersionCheckDetailOverlay.* 的检查、预览与备份交互语义，使用当前 Avalonia 布局及 Application 门面重新编写；未复制 WPF 视图或运行程序集。

## 模型预览核心迁移

复制原项目 `Services/Parsing/PatchResourceInspectionService.cs` 至 `HD2Mimnt.Infrastructure/ModelPreview/`，复制 `Models/PatchResourceInspection.cs` 及 `Models/ModelPreview/*.cs` 至 `HD2Mimnt.Core/ModelPreview/`。包括数组／System.Numerics 模型、section／材质变体、骨架／蒙皮、动画解析、朝向与体型选择；沿用本目录 [Apache 2.0 许可证](LICENSE.txt)。

修改范围：替换命名空间并公开跨程序集契约，移除旧注册特性及依赖；解析器实现 `IModelPreviewReader`。索引后先确定最终生效 Unit，材质布局及第二遍 GPU 解码跳过被覆盖版本；保留纹理与动画资源的覆盖顺序，取消异常继续传播。读路径使用当前 `PathGuard`，PNG 同时限制载荷字节与像素、校验 IHDR。未复制许可不明的动画内嵌／外部名称表，显示名保留 ID／State 回退，不依赖 WPF 资源或旧服务。

纹理解码引用本地已缓存的 BCnEncoder.Net 2.3.0（不复制其源码）。NuGet 包只包含 `MIT OR Unlicense` 声明，没有独立许可证正文；提取声明并选用 Unlicense，正文来源为 [NuGet 官方许可页](https://licenses.nuget.org/Unlicense)，保存在 [BCnEncoder/LICENSE](../BCnEncoder/LICENSE)。
