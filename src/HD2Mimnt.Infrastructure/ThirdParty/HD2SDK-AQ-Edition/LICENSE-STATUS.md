# HD2SDK-AQ-Edition 材质数据来源及许可状态

本文件是许可状态记录，不是许可证，也不授予上游内容的使用权。

- 来源：https://github.com/Estecsky/HD2SDK-AQ-Edition
- 使用范围：模型预览的材质着色分类。`ModelPreviewMaterialClassifier` 中的父材质 ID 指纹取自上游 `materials/` 目录的材质模板（泛光、流光、玻璃、advanced、alpha clip 等），用于近似判断预览着色方式。
- 校验日期：2026-11-19。
- 当日上游根目录未列出许可证；`main/LICENSE`、`main/LICENSE.txt`、`main/LICENSE.md` 均返回 404，README 也未声明许可。
- 因此没有可附上的上游许可证全文，不能将其标为 MIT、Apache 或其他许可证。
- 本项目未引入 SDK 源代码，也未内置或随包分发 `materials/`、`stingray/` 或其它上游资源；仅记录从材质模板中提取的父材质 ID 与着色分类对应关系。

若上游后续明确授权，再更新本记录并附原始许可证。
