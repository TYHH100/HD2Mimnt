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
   git tag -a V0.2.0-beta -m "0.2.0-beta"
   git push origin master V0.2.0-beta
   ```

3. 推送标签后 GitHub Actions 自动构建并创建 GitHub Release，产物为主包 `HD2Mimnt-<版本>-win-x64.zip`（不含 `.pdb`）与调试符号包 `HD2Mimnt-<版本>-win-x64-symbols.zip`。
4. 回到 `dev` 继续下一轮开发：`git checkout dev`。

## CI 发布工作流

工作流文件：[.github/workflows/release.yml](../.github/workflows/release.yml)。

- 触发：推送 `V*.*.*` 或 `V*.*.*-*` 形式的标签。
- 步骤：发布 UI 主程序（自带还原与依赖构建）→ 主包与符号包分离打包 → 创建 Release。
- 发布流水线不运行测试；测试（单元、UI、性能、稳定性）在开发阶段于本地执行。
- 主包不含 `.pdb`；调试符号单独打包为 `HD2Mimnt-<版本>-win-x64-symbols.zip`，配合 `minidump-stackwalk` 排查崩溃转储。
- 发布配置与 `src/HD2Mimnt.UI/Properties/PublishProfiles/Release.pubxml` 一致：win-x64、框架依赖、单文件；`PluginHost` / `Worker` 子进程 exe 与 `minidump-stackwalk` 由 UI 工程的 `PublishChildProcessHosts` 目标随包输出。
- 产物为框架依赖发布，运行需安装 [.NET Desktop Runtime 10.0 x64](https://dotnet.microsoft.com/download/dotnet/10.0)。

## 注意事项

- 紧急修复可直接在 `master` 上进行，打补丁版本标签发布，随后在 `dev` 上普通 `git merge master` 同步（会带来 `master` 的发布提交，不影响 `dev` 自身历史）。
- 发布提交后 `master` 与 `dev` 内容一致；`dev` 保留完整历史，`master` 只有各次发布提交。不要把 `master` 反向合并回 `dev` 的开发线。
- 发布提交的内容等于 `dev` 当前快照与 `master` 上一发布提交的差异；`dev` 上一次发布之后的新增工作就是下次发布的全部内容。
- 建议在 GitHub 仓库设置中对 `master` 启用分支保护（禁止 force push），历史覆盖只允许在初始化时进行一次。
