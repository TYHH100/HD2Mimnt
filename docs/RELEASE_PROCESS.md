# 构建与发布流程

仓库采用双分支模型：`master` 只保存稳定版本，日常开发全部在 `dev` 进行；发布以标签触发 GitHub Actions 自动构建。

## 分支模型

- `master`：稳定版本分支。只接收来自 `dev` 的压缩发布提交与版本标签，不在此直接开发。
- `dev`：日常开发主线。所有功能、修复和测试提交都在此进行，保留完整提交历史。
- `master` 与 `dev` 历史相互独立（`master` 是初始化时压缩出的单提交谱系）；`dev` 上的工作全部确认稳定后，把其内容压缩为 `master` 上的一个发布提交并打标签，然后 `dev` 继续下一轮开发。

## 版本号规范

- 代码中的版本号唯一来源是根目录 `Directory.Build.props` 的 `<Version>`。程序集元数据、状态栏版本徽章与关于页文案都由该值派生，发版时只改这一处并打对应标签；CI 会用标签通过 `-p:Version` 覆盖构建版本。
- 标签格式：`V<主>.<次>.<补>-<阶段>`，阶段关键词如 `alpha`、`beta`、`rc`；正式版去掉后缀，如 `V1.0.0`。
- 压缩合并产生的提交信息与标签使用同一版本号文本（不带 `V` 前缀），如 `0.1.0-alpha`。
- 示例：`V0.1.0-alpha` → `V0.2.0-beta` → `V1.0.0`。

## 发布流程（每次发布）

1. 在 `dev` 上完成开发，本地通过 `dotnet build HD2Mimnt.slnx --no-restore` 与相关测试（UI 测试在本地验证，CI 不跑）。
2. 把 `dev` 的当前内容压缩为 `master` 上的发布提交并打标签。两边历史独立，`git merge --squash` 会报 unrelated histories，用 `read-tree` 直接把 `dev` 的完整内容读入暂存区（不动 `master` 的提交谱系）：

   ```bash
   git checkout master
   git read-tree -u --reset dev
   git commit -m "0.2.0-beta"
   git tag -a V0.2.0-beta --cleanup=verbatim -F release-notes.md   # 标签注释正文就是 Release 更新说明
   git push origin master V0.2.0-beta
   ```

   `release-notes.md` 是临时文件：首行写版本号（可省略），空一行后写更新内容（Markdown 可用）；打完标签即可删除，不要提交进仓库。注意 `git tag` 默认会丢掉以 `#` 开头的行，Markdown 标题需要配合 `--cleanup=verbatim` 才能保留（见[注意事项](#注意事项)）。

3. 推送标签后 GitHub Actions 自动构建并创建 GitHub Release，产物为主包 `HD2Mimnt-<版本>-win-x64.zip`（不含 `.pdb`）与调试符号包 `HD2Mimnt-<版本>-win-x64-symbols.zip`。
4. 回到 `dev` 继续下一轮开发：`git checkout dev`。

## CI 发布工作流

工作流文件：[.github/workflows/release.yml](../.github/workflows/release.yml)。

- 触发：推送 `V*.*.*` 或 `V*.*.*-*` 形式的标签。工作流同时接受小写 `v` 前缀以兼容旧脚本，版本号推导会去掉 `v`/`V`；仓库标签规范仍使用大写 `V`。
- 步骤：发布 UI 主程序（自带还原与依赖构建）→ 校验发布布局（四个入口文件齐全、不包含 `deps.json`、托管依赖在 `lib/`、原生库在根目录）→ 主包与符号包分离打包 → 创建 Release。布局校验不通过时流水线直接失败，防止发布属性回退成单文件或把原生库误收进 `lib/`。
- Release 更新说明取自标签注释：首行若只是版本号会被去掉，其余正文原样写入 Release；标签没有任何正文时回退到 `--generate-notes` 按提交自动生成。本地可用 `git tag -l -n99 <标签>` 核对。
- 流水线是浅克隆，本地标签可能退化成轻量引用而读不到注解，因此这一步按 GitHub API 读取标签注解对象，取不到才退回本地 `git tag`；注解缺失时仍回退自动生成。
- 更新说明写错时无需重打标签：`gh release edit <标签> --notes-file <文件>` 即可修正已发布的 Release。
- 发布流水线不运行测试；测试（单元、UI、性能、稳定性）在开发阶段于本地执行。
- 主包不含 `.pdb`；调试符号单独打包为 `HD2Mimnt-<版本>-win-x64-symbols.zip`。
- 发布配置与 `src/HD2Mimnt.UI/Properties/PublishProfiles/Release.pubxml` 一致：win-x64、框架依赖、非单文件。四个入口 exe（`HD2Mimnt.UI`、`HD2Mimnt.Cli`、`HD2Mimnt.PluginHost`、`HD2Mimnt.Worker`）及其入口 dll、各自的 `runtimeconfig.json` 由项目引用输出到根目录，其余托管程序集由 UI 工程的 `MoveManagedAssembliesToLib` 目标收进 `lib/`；根目录只留入口文件、原生库与内容目录。四个入口项目的 csproj 都设置了 `GenerateDependencyFile=false`，发布产物不包含 `deps.json`——托管依赖由 `lib/` 的 `LibAssemblyResolver` 解析，宿主缺 deps 时按应用目录探测（2026-11 实测 UI/Cli 无 deps 均可运行）。`runtimeconfig.json` 不能省略：hostfxr 按 `<exe>.runtimeconfig.json` 固定文件名读取，缺失会直接报错。不要给发布命令加 `-p:PublishSingleFile=true`。
- 产物为框架依赖发布，运行需安装 [.NET Desktop Runtime 10.0 x64](https://dotnet.microsoft.com/download/dotnet/10.0)。

## 注意事项

- 紧急修复可直接在 `master` 上进行，打补丁版本标签发布，随后在 `dev` 上普通 `git merge master` 同步（会带来 `master` 的发布提交，不影响 `dev` 自身历史）。
- 发布提交后 `master` 与 `dev` 内容一致；`dev` 保留完整历史，`master` 只有各次发布提交。不要把 `master` 反向合并回 `dev` 的开发线。
- 发布提交的内容等于 `dev` 当前快照与 `master` 上一发布提交的差异；`dev` 上一次发布之后的新增工作就是下次发布的全部内容。
- 建议在 GitHub 仓库设置中对 `master` 启用分支保护（禁止 force push），历史覆盖只允许在初始化时进行一次。
- 标签注释默认按提交信息规则清理：以 `#` 开头的行会被整行丢弃。写发布说明时用 `git tag -a --cleanup=verbatim -F <文件>`；用 `-m "标题" -m "正文"` 时不要用 `#` 标题。
