# 潜潜音效修复软件源码归属

- 原作者主页：https://space.bilibili.com/157681854
- 来源：用户于 2026-10-21 提供的「潜潜音效修复软件」v1.0 目录内 `源码/Program.cs`。
- 原始使用说明及声明全文：[使用说明.original.txt](使用说明.original.txt)。
- 用户要求直接集成源码并保留原声明及来源。原始文件未附许可证；本记录不授予上游许可，不将这些代码自动归入仓库的许可证，也不冒称 MIT 或其他开源许可。后续取得作者许可证时应附上原文。

`SoundFix/SoundBankRepair.cs` 改编其 Wwise HIRC、DIDX、DATA 重建、媒体 ID 合并及 mem_size 修正算法。管理器重新实现模组库扫描、后台取消、逐模组备份事务和界面；游戏参考复用已有 DSAR 读取器，替代固定 Mapping.cs 名称映射。包更新仅追加已修复音库并更新相应 TOC 定位，保留其他资源、stream/GPU 伴随文件与原始包头。
