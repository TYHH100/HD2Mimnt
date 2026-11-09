# 离线模组接口

HD2Mimnt 自动提供本机 UTF-8 JSON 文件，供模组及工具读取模组库、配置状态和实际部署文件。无网络、无 HTTP 服务、无数据库直连要求；管理器关闭后最后一个快照仍可读取。接口只读，不接受通过修改 JSON 反向操作管理器，不包含凭据、下载地址或第三方脚本内容。

## 固定入口

```text
正式版：%LOCALAPPDATA%\HD2Mimnt\interop\index.json
        %LOCALAPPDATA%\HD2Mimnt\interop\v1\runtime.json
开发版：%LOCALAPPDATA%\HD2Mimnt.dev\interop\index.json
        %LOCALAPPDATA%\HD2Mimnt.dev\interop\v1\runtime.json
```

测试/自定义启动通过 `HD2MIMNT_DATA_PATH` 指定的定位目录使用相同 `interop` 子目录。应用数据和模组库搬到其它盘后，接口仍留在固定定位目录；`storage.appDataPath`、`storage.modsPath` 和各模组 `path` 指向实际生效位置。首次成功初始化后生成接口，不要求用户先部署一次。

`index.json` 的 `format` 固定为 `hd2mimnt-interop-index`，入口 `schemaVersion` 固定为 `1`，包含：

| 字段 | 含义 |
| --- | --- |
| `snapshotId` / `generatedUtc` | 最近发布标识与 UTC 时间；跨文件比较时以实际读取的 runtime 标识为准 |
| `latestMajorVersion` | 管理器当前提供的最高接口主版本 |
| `defaultMajorVersion` | 保底主版本 `1` |
| `versions` | `{majorVersion, minorVersion, runtimePath}` 数组，路径是本机绝对路径 |

## 版本保底与自动适配

- 管理器软件版本 `managerVersion` 与接口版本无关。升级管理器不会要求模组更新。
- `v1/runtime.json` 的字段名称、类型和含义固定；可添加字段，小版本增加时旧消费者忽略未知字段。
- 破坏性变化必须新增 `v2` 等独立目录和投影适配器。发布器始终注册并更新 v1，不能覆盖或删除 v1。旧模组可以永久读取固定 v1 路径，不需要理解新版。
- 支持多个版本的消费者，从 `index.versions` 选择**自己支持**的最高主版本；没有匹配项时使用已知的 v1。不得直接打开最高版本后假设能理解它。
- 缺少入口、入口损坏或目标格式不受支持时，可重试固定 v1 路径；仍不可读时报告不可用，不能推断为空库或“全部未部署”。
- 未来格式的自动适配由管理器输出旧格式完成，不猜测任意未知格式。新增版本时必须保留旧适配器和兼容测试。

离线读取示例：[offline-interop-reader.lua](../examples/offline-interop-reader.lua)。它接收调用方现有的 JSON 解码函数，不依赖网络或特定加载器。

## v1 快照

`runtime.json` 的 `format` 为 `hd2mimnt-runtime`，`schemaVersion: 1`，`schemaMinorVersion: 0`。所有状态在一个完整 JSON 内发布，数组即使为空也保留；可未知字段使用 `null`。ID 使用 GUID 字符串，时间使用 ISO 8601 UTC，大小单位字节，索引从 0 开始。JSON 对象成员顺序没有含义。

机器可读契约：[hd2mimnt-runtime-v1.schema.json](../references/hd2mimnt-runtime-v1.schema.json)。它允许附加未知字段，方便旧消费者继续读取兼容的小版本更新。

| 字段 | 内容 |
| --- | --- |
| `snapshotId`, `generatedUtc`, `managerVersion` | 发布标识、时间、管理器程序集版本 |
| `storage` | 当前应用数据与模组库根目录 |
| `activeProfileId` | 激活配置 ID；可能与上次实际部署的配置不同 |
| `deploymentOrder` | 当前部署方向 `TopToBottom` / `BottomToTop` |
| `library` | **全部受管理模组**，包括未加入配置、未启用、未部署的模组 |
| `profiles` | 全部配置的成员、启用状态、顺序和当前选项 |
| `deployments` | **当前生效游戏安装**的当前部署状态（每模组一条）；其它安装和未归属旧会话不导出 |

### 模组库 `library[]`

- `id` 是管理器稳定身份；`manifestGuid` 是作者清单 GUID，缺失时为 null，二者不能混用。
- `name`, `version`, `author`, `description`, `path`, `thumbnailPath`, `size`, `hash`, `role`, `category`, `workflow`, `status`, `tags`, `containsScripts`, `modifiedUtc` 描述库中模组；空哈希表示尚未计算，不为导出额外计算哈希。
- `resourceTypeIds` 为 16 位小写十六进制**字符串**数组，避免 Lua/JavaScript 数值精度丢失；null 表示未扫描。
- `legacyOptions` 是旧格式互斥选项名称；`options[]` 描述 V1 选项：`name`, `description`, `defaultEnabled`, `include`, `image`, `subOptions`。子选项有 `name`, `description`, `include`, `image`。`include: null` 表示未声明映射，`[]` 表示合法的无文件选项。
- `defaultOptions`、配置成员 `options` 和部署 `options` 均为 `{legacySelectedIndex, optionToggles, optionSelections}`；按对应定义的 0 起索引解释，无关分支为 null。
- `files[]` 列出模组根目录下各变体的合法补丁三元组文件 `{relativePath, size}`。相对路径用 `/`，结合 `path` 定位。只做目录/文件元数据枚举，不读取整个补丁，不跟随目录链接。完整库清单含未选中的变体；不能将它直接当部署文件集。
- `filesComplete` / `filesError` 表示库文件枚举是否完整；目录缺失或权限失败时保留模组元数据，明确报告不完整。

### 配置 `profiles[]`

`id`, `name`, `isActive`, `mods[]`。成员字段是 `modId`, `enabled`, `order`, `options`。`deploymentSequence` 为当前启用成员预计部署顺序的 ID 数组，沿用加载器策略与部署方向；缺文件/无补丁的成员可能在实际部署时跳过。显示分隔符不作为模组输出。

### 部署 `deployments[]`

`id`, `sessionId`, `profileId`, `modId`, `status`, `method`, `startedUtc`, `endedUtc`, `order`, `modVersion`, `options`, `isComplete`, `files`, `missingTargets`。

- 部署记录表示当前安装状态，按当前 `SettingsService.GetEffectiveGamePath()` 过滤；`GamePath` 本身不进入 v1 JSON。同一模组在官方安装与 HellForge 可能各有一条当前状态，切换来源后重新发布快照，不能把两个安装的文件混在一起。
- 记录保存最近一次对账时的模组版本、归一化选项与部署顺序；之后修改配置不会改写这些值，直到再次部署或模组更新触发自动增量部署。旧记录没有相关快照时 `order`, `modVersion`, `options` 为 null，**不使用当前库或配置补造过去状态**。
- `files[]` 只含发布时实际存在的受追踪目标：`sourcePath`, `targetPath`, `method`, `isPlaceholder`。文件名重编号后以 `targetPath` 为准；占位文件 `sourcePath: null`, `isPlaceholder: true`。2026-10-25 起部署不再生成 0 字节占位（游戏加载占位会崩溃），新记录中 `isPlaceholder` 恒为 false；旧记录中的占位条目按原样保留投影。
- 预登记但未写入、已被外部删除或清理的目标放入 `missingTargets`，不冒充已部署文件。
- 旧版未归属记录（没有 `GamePath`）在启动迁移时按目标路径归属并合并；无法归属的记录不进入离线快照。迁移只改数据库归属，不删除磁盘文件。
- `isComplete` 仅表示记录状态为 `Completed` 且受追踪目标全部存在，不证明补丁内容完整、游戏兼容或游戏已载入。旧失败/取消残留记录在迁移归属后仍按原状态投影；当前对账失败不会伪造完成记录。
- 库中启用状态、当前配置选项与实际部署是不同事实。不要按 `enabled` 判断游戏正在使用该模组。导出也无法判断运行中的游戏是否重启加载了新部署。

## 更新、读取与边界

启动、导入/更新/删除、元数据与缩略图变动、配置变动、部署/清理、备份恢复后合并刷新。库文件清单按受管理路径、修改时间和内容哈希缓存；外部直接修改库文件需重启管理器重新枚举。存储迁移在下次启动生效后重新导出真实路径。

后台读取独立只读 SQLite 连接，在读事务内获取库、配置和部署记录。快照使用同目录临时文件写入并关闭后替换，失败保留原快照并后台重试；结束流程清理临时文件。消费者按 `snapshotId` 或文件时间判断变化，每次打开、读完并关闭。Windows 原生读取器应允许 `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`，避免长时间持有禁止替换的文件句柄。入口与各主版本文件分别替换，跨文件不保证同时切换；业务数据只读取选中版本的完整 runtime 文件。

JSON 是最后一次发布的状态，管理器关闭后不会主动更新；游戏文件在发布后仍可能被外部改动。消费者如需最新存在状态应自行核对目标。接口不修改第三方模组或它们的本机配置；现有 MDL 只识别自己的管理器格式，需要单独的格式适配，提供原生接口不会让它自动识别。

## 外部参考

- [JSON RFC 8259](https://www.rfc-editor.org/rfc/rfc8259)：UTF-8、对象与数组语义。
- [Semantic Versioning](https://semver.org/)：兼容变更与破坏性主版本。
- [Microsoft File.Move](https://learn.microsoft.com/dotnet/api/system.io.file.move)：同目录文件替换。
- [Windows CreateFileW](https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-createfilew)：共享读取与允许删除/替换。
- [SQLite 隔离](https://www.sqlite.org/isolation.html)：读事务快照。
