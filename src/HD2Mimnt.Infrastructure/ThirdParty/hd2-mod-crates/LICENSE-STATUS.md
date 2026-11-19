# hd2-mod-crates 换甲实现来源及许可状态

本文件是许可状态记录，不是许可证，也不授予上游内容的使用权。

- 来源：https://github.com/eigeen/hd2-mod-crates
- 核对提交：`440211e39eb62448054ada31e101aeeeee3d7796`（`main`，2026-09-09）。
- 上游根目录未列 LICENSE 文件，GitHub 也未识别许可证；但工作区 `Cargo.toml` 声明 `license = "MIT OR Apache-2.0"`，各 crate 通过 `license.workspace = true` 继承。
- 因此本项目标注为 `MIT OR Apache-2.0（上游未附全文）`，不能冒充已附许可证全文。

使用范围（`src/HD2Mimnt.Infrastructure/ArmorSwap`）：

- 参考上游 Rust 实现移植的算法：来源识别、非 Unit 资源按归档序号重映射、Unit 头引用/材质 ID/贴图 ID 改写、空网格清洗与补齐、补丁写出流程。
- 未逐行复制上游 Rust 源码；C# 代码为本项目独立编写，并与现有 `IasArchive` / `IasGameData` / `IasUnit` 复用。
- 内嵌数据表来源与署名见 [`ArmorSwap/Data/NOTICE.md`](../../../ArmorSwap/Data/NOTICE.md)：`archivehashes.json` 来自社区 HD2SDK hashlists，护甲/头盔映射由 B 站 @大紫 提供。

分发要求：若随发布物分发移植实现或数据表，保留上述署名，并附 MIT 与 Apache-2.0 许可证文本。

若上游后续补充 LICENSE 文件，再更新本记录并附原始许可证全文。
