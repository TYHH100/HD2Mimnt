# 模组创建

顶部“模组创建”（稳定导航 Tag=6，Ctrl+7）提供新档案（V1）和旧档案（Legacy）的 `manifest.json` 图形编辑和完整 7z／ZIP 导出。它编辑模组包清单，与“配置文件”中的模组组合分开。页面切换保留草稿；新建、打开另一份清单及退出时检查未保存内容。“设置 → 入口自定义”支持隐藏该入口。

## 使用

页面顶部可切换“新档案（V1）／旧档案（Legacy）”，默认新档案。旧档案在选项页只显示目录路径列表，“添加选项”打开程序内文件夹勾选树，确认后加入各个目录，选中已有行可删除。首次切到旧档案会取当前 V1 选项的 Include 目录（排除根目录 `.`），之后两种草稿独立保留；来回切换不会删除 V1 子选项。预览、保存和导出跟随当前格式，打开已有清单自动选中对应格式。新建恢复默认 V1。

“压缩格式”可选 7z 或 ZIP，默认 **7z＋极限压缩**。压缩档位为“不压缩、快速、标准、高压缩、极限压缩”。7z 随程序附带工具，无需另外安装；使用 LZMA2 固实压缩，档位越高，通常体积越小，耗时及内存用量也越高。ZIP 仍使用标准 Deflate，高压缩和极限均对应 .NET `SmallestSize`。这些设置只影响导出包，不写入模组清单。

| 档位 | 7z 等级 | LZMA2 字典 |
| --- | --- | --- |
| 不压缩 | 0 | 不使用 |
| 快速 | 1 | 16 MiB |
| 标准 | 5 | 32 MiB |
| 高压缩 | 7 | 64 MiB |
| 极限压缩 | 9 | 128 MiB |

7z 使用 2 个压缩线程；字典大小不等于完整进程内存用量。保存窗口的扩展名跟随所选压缩格式。

1. 选择或粘贴包含补丁、图片和变体目录的模组源目录，自动以文件夹名填入标题，并取根目录第一张图片作为封面；手动输入停止片刻、回车或离开输入框后识别。选项为空时按目录生成：一级目录作为选项、下一级作为子选项，名称及图片分别取所属目录名和直属第一张图片，`Models` / `Model` 下的目录提升为选项。根目录有补丁时增加包含 `.` 的“基础文件”选项，保留根部文件映射。已手动填写的标题、图片及现有选项保留；打开已有清单不重新生成。作者、版本和说明可继续编辑。“作者网站”为选填，支持 A/N 网之外的 HTTP/HTTPS 主页；导入后可从模组库、配置页右键“打开作者主页”，留空则隐藏该项。
2. 在“选项与子选项”添加可独立启用的选项、互斥子选项、说明、预览图和 Include 目录。“选择目录”在程序内显示简洁的文件夹勾选树，可展开、多选、取消原有勾选；确认后替换当前 Include，取消不修改草稿。新选项／子选项名称及图片为空时，也会按首个勾选目录补齐。也可每行输入一个相对目录；`.` 表示源目录本身。拖动左侧选项行或子选项名称左侧的手柄排序，蓝色插入线指示落点；按住时可用滚轮滚动，松开提交，Escape 取消。子选项只在所属选项内排序。图片及目录必须位于源目录内。
3. 在“JSON 预览”分别查看 `manifest.json` 和 `hd2mimnt.json`。“保存清单与扩展”将两份文件保存于源目录根部；“打包导出模组”按选择的格式及档位将生成的两份 JSON、源目录全部文件及空目录打包，输出位置必须在源目录之外。
4. 压缩包内清单位于根部，可通过现有模组导入流程导入。打包不修改源目录及其旧清单；输出先写同目录临时文件，成功后发布，取消或失败删除本次临时文件并保留已有输出。7z 只在当前临时存储位置暂存两份 JSON 和文件列表，直接读取源文件压缩；取消时终止并等待压缩进程退出，再清理暂存目录。

## 清单行为与边界

- 压缩包保存对话框默认使用当前模组名称（加 `.7z` 或 `.zip`），非法文件名字符替换为下划线，空名称回退为 `mod`。
- “管理器预览”与配置文件中的选项弹窗复用同一控件：独立勾选 V1 选项、展开子选项并单选，Legacy 使用互斥目录列表；封面、选项图和子项图从源目录加载。预览交互只修改预览状态，不改草稿、清单或部署配置。进入标签页或点击“刷新预览”读取当前草稿。
- 在管理器预览中点击“导出长截图（展开全部）”保存 PNG，默认文件名为模组名称。导出完整内容（含封面、说明和全部子项），使用当前主题及预览选择；草稿修改后重新生成预览。导出使用独立控件展开全部子项并关闭过渡动画，不受屏幕滚动区域高度限制，也不改变屏幕中的折叠状态。PNG 在后台编码和保存，取消或失败清理临时文件。

- `manifest.json` 只保留旧版 V1 标准字段：根级 `Version`、`Guid`、`Name`、`Description`、`IconPath`、`Options`、`NexusData`；选项级 `Name`、`Description`、`Image`、`Include`、`SubOptions`；子选项级 `Name`、`Description`、`Image`、`Include`；`NexusData` 只含 `ModId`、`Version`。不向清单添加 HD2Mimnt 字段或扩展文件引用。
- `Version: 1` 是清单格式，作者 `Author`、作者网站 `AuthorUrl`、模组版本 `ModVersion`、默认启用 `DefaultEnabled`、分类和标签等自定义内容存入同目录的 **`hd2mimnt.json`**。新建生成稳定 GUID，修改和导出保留它。
- 勾选“指定包含目录”但留空生成 `Include: []`，表示明确无文件；取消勾选省略 Include。父分组可只依赖子选项映射，叶选项缺失映射可能触发全量部署回退。Include 仅包含指定目录直属补丁。
- 默认启用支持 true、false 和未声明三态，单独记录在扩展 JSON；未声明沿用现有管理器默认值。其他管理器只读取标准清单时使用它们自己的默认行为。
- 打开时兼容旧清单内嵌的自定义字段；保存或导出时把根级、选项、子选项及 NexusData 中的非标准字段转移到独立 JSON，源文件仅在执行保存时改动。删除选项时不会将它的扩展字段错配给下一项。
- 独立文件包含 `SchemaVersion: 1`、与清单对应的 `ManifestGuid` 和 `Extensions`。`Extensions` 保存原字段名及嵌套选项数组；选项中的 `Name` 只用于校验对应项，不覆盖标准名称。打开和导入合并两份文件；扩展不能覆盖标准字段或文件映射。扩展损坏、版本不支持或 GUID 不匹配时，创建页报错以免覆盖数据，普通导入则使用标准清单。
- 排序后实时预览、保存清单及导出 ZIP 使用同一集合顺序，保留选项对象和扩展元数据；拖动预览期间不改变草稿，取消或原位松开不会产生未保存修改。切换选项、编辑标签或页面以及捕获丢失时取消拖动。
- Legacy 清单省略 `Version`，`Options` 为相对目录的字符串数组，沿用管理器原有互斥选项行为；作者、模组版本等扩展仍存入 `hd2mimnt.json`。Legacy 不输出 V1 的选项对象或子选项。两种压缩格式均拒绝源目录中的链接文件或链接目录，避免跨目录读取或循环遍历。
- 导出使用后台操作并支持取消；关闭窗口等待清理。页面使用 FluentAvalonia 组件和三语资源；教程打开时通过导航命令 CanExecute 禁止快捷切页。

两份文件先写入临时文件，再发布；保存清单失败会恢复此前的扩展文件。打包总是使用当前草稿生成两份 JSON，跳过源目录内的同名旧文件，避免重复项；打包不修改旧文件。

普通导入仅转换仍内嵌扩展的库副本，保留原压缩包／源目录；已经符合标准的包保持原字节。共享清单写入器、开发测试模组、迁入和反向迁回同样使用独立文件。部署和清理排除 `hd2mimnt.json`，加载器依赖检测继续读取其中的兼容声明。来源绑定仍使用库根目录下的 `.hd2mimnt-metadata`，与随包分发的扩展文件分开。

验证覆盖双文件往返、旧内嵌字段转移、未知字段、空目录、路径越界拒绝、文件占用回滚、操作开始前及打包中取消，并通过实际 UI 输入验证导航、选项编辑、三语／深浅主题／960×600 布局和 ZIP 再导入；不代表其他管理器实际运行或游戏兼容性验证。

## 参考

- [7-Zip 官方下载](https://www.7-zip.org/download.html)、[7z 格式](https://www.7-zip.org/7z.html)；随包工具的[原版许可证](../../src/HD2Mimnt.Infrastructure/Tools/7zip/License.txt)及[来源说明](../../src/HD2Mimnt.Infrastructure/Tools/7zip/NOTICE.md)。
- [旧版创建页面源码](https://github.com/TYHH100/Helldivers2ModManager/blob/test/src/Helldivers2ModManager/ViewModels/CreatePageViewModel.cs)：参考源目录、元数据和选项编辑功能，文件操作接入当前 Application 层。
- [旧版 V1 清单标准](https://github.com/TYHH100/Helldivers2ModManager/blob/test/src/Helldivers2ModManager/Models/V1ModManifest.cs)、[选项字段](https://github.com/TYHH100/Helldivers2ModManager/blob/test/src/Helldivers2ModManager/Models/ModOption.cs)：标准字段边界依据。
- [.NET ZipArchive](https://learn.microsoft.com/dotnet/api/system.io.compression.ziparchive)
- [Avalonia 文件选择器](https://docs.avaloniaui.net/docs/concepts/services/storage-provider)
- [Avalonia RenderTargetBitmap 源码](https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Base/Media/Imaging/RenderTargetBitmap.cs)、[Expander 过渡动画实现](https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Avalonia.Controls/Expander.cs)
- [Avalonia 样式选择器](https://docs.avaloniaui.net/docs/reference/styles/style-selector-syntax)
- [Avalonia 指针输入与捕获](https://docs.avaloniaui.net/docs/concepts/input/pointer)
