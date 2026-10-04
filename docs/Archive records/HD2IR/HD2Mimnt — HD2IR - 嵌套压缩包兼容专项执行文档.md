# HD2Mimnt
## HD2IR / `.hd2irpack.zip` / 嵌套资源包兼容专项执行文档

**文档类型：** 后端专项实施计划  
**适用项目：** HD2Mimnt  
**目标范围：** `.hd2irpack.zip`、嵌套 ZIP、模型/材质分包、混合资源包、逻辑模组归并  
**实施方式：** 单人开发、分阶段实现、每阶段可独立验证  
**核心目标：** 在不破坏现有 Mod 管理架构的前提下兼容现实世界中不同作者、不同打包习惯的 HD2IR 资源。

---

# 一、项目背景

现实中的 HD2IR 相关模组并不存在一个可以完全依赖的统一外层打包规范。

目前观察到的资源可能出现以下情况：

## 1. 最规范的双包结构

```text
VRchat_AIRI.zip
├── VRchat_AIRI.hd2irpack.zip
└── VRchat_AIRI_model.zip
```

含义：

```text
外层 ZIP
├── HD2IR 专用资源
└── 模型资源
```

这是最容易处理的一种结构。

---

## 2. 多组件分包结构

```text
VRchat_AIRI.zip
├── HD2IR.zip
├── Model.zip
├── Texture.zip
└── Material.zip
```

这里 HD2IR、模型、贴图、材质被作者分别打包。

---

## 3. 多层嵌套结构

```text
VRchat_AIRI.zip
└── Resources.zip
    ├── HD2IR.zip
    │   └── AIRI.hd2irpack.zip
    ├── Model.zip
    └── Texture.zip
```

此时真正的 HD2IR 包并不在第一层。

---

## 4. 深层甚至不规则嵌套

```text
VRchat_AIRI.zip
└── NewFolder.zip
    └── Resources.zip
        ├── Something.zip
        │   └── AIRI.hd2irpack.zip
        ├── Model.zip
        └── Data.zip
```

文件名、目录名、压缩层级均不可靠。

---

## 5. 单个 ZIP 混合全部内容

```text
VRchat_AIRI.zip
├── HD2IndependentRig/
├── model/
├── texture/
├── material/
└── other/
```

或者：

```text
VRchat_AIRI.zip
├── HD2IR/
├── model.zip
└── texture.zip
```

---

# 二、专项设计目标

本专项不是为了建立一个“HD2IR 专用部署器”。

真正目标是：

> **把不同作者提供的非统一资源包，转换成 HD2Mimnt 内部统一的逻辑模组结构。**

即：

```text
作者的打包方式
        ↓
     导入分析
        ↓
     组件识别
        ↓
     资源归并
        ↓
   标准化 Logical Mod
        ↓
   统一部署系统
```

最终部署引擎不应该关心：

```text
原始资源到底套了几层 ZIP
```

也不应该关心：

```text
作者把模型和材质分成几个压缩包
```

部署引擎只需要处理：

```text
标准化后的文件集合
+
文件来源关系
+
部署规则
```

---

# 三、核心架构原则

## 原则 1：压缩包不是模组

禁止把：

```text
AIRI.zip
AIRI_Model.zip
AIRI_Texture.zip
AIRI.hd2irpack.zip
```

天然定义为四个 Mod。

压缩包只是资源来源。

真正的管理对象应该是：

```text
Logical Mod
```

---

# 四、Logical Mod 模型

HD2Mimnt 内部推荐采用四层模型：

```text
Logical Mod
│
├── Components
│   ├── HD2IR
│   ├── Model
│   ├── Texture
│   ├── Material
│   └── Other
│
├── Packages
│   ├── Outer ZIP
│   ├── HD2IR ZIP
│   ├── Model ZIP
│   └── Texture ZIP
│
└── Deployable Files
    ├── File A
    ├── File B
    ├── File C
    └── ...
```

关系：

```text
Logical Mod
   ↓
Component
   ↓
Package
   ↓
File
```

---

# 五、四层职责必须严格分离

## 1. Logical Mod Layer

负责：

```text
用户真正管理什么？
```

例如：

```text
VRChat AIRI 原皮
```

用户的启用、禁用、配置文件切换都应该面向这一层。

---

## 2. Component Layer

负责：

```text
这个 Mod 包含哪些逻辑组件？
```

例如：

```text
VRChat AIRI 原皮
├── HD2IR
├── Model
├── Texture
└── Material
```

---

## 3. Package Layer

负责：

```text
组件来自哪个压缩包？
```

例如：

```text
HD2IR
└── AIRI.hd2irpack.zip

Model
└── AIRI_Model.zip

Texture
└── AIRI_Texture.zip
```

---

## 4. File Layer

负责：

```text
最终实际要部署什么文件？
```

例如：

```text
bin/HD2IndependentRig/xxx.hd2phys
bin/HD2IndependentRig/xxx.rigbin
...
```

---

# 六、导入系统总体流程

完整流程定义为：

```text
用户选择文件
    ↓
Archive Inspector
    ↓
读取归档结构
    ↓
检测嵌套 Archive
    ↓
HD2IR / 普通资源识别
    ↓
Component Detector
    ↓
Candidate Mod Group
    ↓
Mod Merger
    ↓
用户确认（必要时）
    ↓
生成 Logical Mod
    ↓
生成内部 Manifest
    ↓
写入 Mod Library
```

---

# 七、Archive Inspector

新增：

```text
ArchiveInspector
```

职责：

```text
1. 读取 ZIP/压缩包目录
2. 不立即完整解压
3. 判断内部是否存在其他压缩包
4. 判断是否存在 HD2IR 特征
5. 判断是否存在普通 HD2 文件
6. 建立 Archive Tree
7. 为后续识别提供结构信息
```

---

# 八、为什么不能“发现 ZIP 就递归解压”

禁止设计成：

```text
发现 ZIP
↓
解压
↓
继续扫描
↓
发现 ZIP
↓
再解压
```

原因：

```text
1. 大型模型包可能非常大
2. 多层 ZIP 会产生大量临时文件
3. 可能出现重复数据
4. 会造成不必要的磁盘 I/O
5. 可能被异常嵌套结构拖垮
6. 用户仅仅查看模组时不应该产生大量文件
```

因此第一阶段应该采用：

```text
Archive Inspection
```

而不是：

```text
Full Extraction
```

即：

> **优先读取 ZIP 中央目录和文件条目，只有真正需要访问文件内容时才读取对应条目。**

---

# 九、嵌套 Archive Tree

内部必须能够表达：

```text
Outer.zip
└── Resources.zip
    ├── HD2IR.zip
    │   └── AIRI.hd2irpack.zip
    ├── Model.zip
    └── Texture.zip
```

建议内部结构：

```text
ArchiveNode
├── Name
├── ArchiveType
├── Parent
├── Children
├── Depth
├── Size
├── EntryCount
└── DetectionResults
```

其中：

```text
Depth
```

用于限制递归扫描深度。

---

# 十、嵌套扫描深度

必须存在最大分析深度。

初始建议：

```text
MaxArchiveInspectionDepth = 5
```

含义：

```text
0 = 外部资源
1 = 第一层嵌套
2 = 第二层
3 = 第三层
4 = 第四层
5 = 第五层
```

超过限制：

```text
不继续自动递归
标记为：
Nested archive depth exceeded
```

不要因为异常包结构导致整个导入线程持续递归。

该参数应可配置，但默认值不应过高。

---

# 十一、HD2IR 识别

HD2IR 不应该仅仅依赖：

```text
*.hd2irpack.zip
```

来判断。

应采用多级检测：

```text
第一优先级：
文件扩展名 / 明确格式标识

第二优先级：
内部目录结构

第三优先级：
HD2IR 特征文件

第四优先级：
文件命名规则

第五优先级：
路径结构

第六优先级：
启发式判断
```

其中启发式判断只能用于：

```text
提示
```

不能作为高置信度事实直接覆盖用户选择。

---

# 十二、HD2IR 特征检测

根据目前观察到的实际部署日志，HD2IR 内容可能出现：

```text
bin/HD2IndependentRig/
```

以及：

```text
*.hd2preview
*.hd2phys
*.hd2pose
*.rigbin
*.hd2shared
*.ini
```

例如此前实际日志中出现：

```text
bin/HD2IndependentRig/
```

以及：

```text
*.hd2preview
*.hd2phys
*.rigbin
*.hd2pose
*.hd2shared
difference.ini
isolation.ini
```

这些可以作为识别规则来源。

但：

> **在完整格式被正式逆向确认之前，不应把当前观察到的文件集合写死成“唯一合法格式”。**

识别器应支持扩展。

---

# 十三、HD2IR Detector

建立：

```text
Hd2IrDetector
```

输出建议：

```text
DetectionResult
├── IsHd2Ir
├── Confidence
├── Reason
├── DetectedFiles
└── DetectedStructure
```

例如：

```text
IsHd2Ir = true
Confidence = High

Reason:
- Extension matched .hd2irpack.zip
- Contains HD2IndependentRig paths
- Contains .hd2phys
- Contains .rigbin
```

---

# 十四、普通资源检测

除 HD2IR 外，还需要检测：

```text
Model
Texture
Material
Animation
Lua
Addon
Other
Unknown
```

但第一阶段不要做复杂 AI 分类。

优先使用：

```text
文件路径
扩展名
目录名
Manifest
明确文件特征
```

例如：

```text
model/
models/

texture/
textures/

material/
materials/
```

只能作为组件候选，不应直接认为分类绝对正确。

---

# 十五、Component Detector

建立统一：

```text
ComponentDetector
```

输入：

```text
ArchiveTree
```

输出：

```text
ComponentCandidates
```

例如：

```text
AIRI.zip

Candidates:
├── HD2IR
│   └── AIRI.hd2irpack.zip
│
├── Model
│   └── AIRI_Model.zip
│
└── Texture
    └── AIRI_Texture.zip
```

---

# 十六、不要强制自动归并

这是整个专项最重要的安全规则之一。

**识别 != 归并。**

例如：

```text
AIRI.zip
AIRI Summer.zip
AIRI Alternate.zip
```

不能因为名字相似就自动归并。

必须区分：

```text
Detection
```

和：

```text
Merge Decision
```

---

# 十七、Mod Merger

建立：

```text
ModMerger
```

负责回答：

> 这些组件是否属于同一个 Logical Mod？

归并依据：

```text
1. 用户一次性导入
2. 外层容器关系
3. 文件夹层级关系
4. 名称相似性
5. Manifest 关系
6. HD2IR 与模型之间的关联
7. 用户显式选择
```

---

# 十八、归并优先级

推荐规则：

```text
明确容器关系
    >
明确 Manifest 关系
    >
同一次导入批次
    >
明显名称关联
    >
启发式推断
```

不要反过来。

尤其：

```text
名称相似
```

不能成为唯一依据。

---

# 十九、用户确认机制

对于高置信度结构：

```text
VRchat_AIRI.zip
├── AIRI.hd2irpack.zip
└── AIRI_model.zip
```

可以直接提供：

```text
检测结果：

VRChat AIRI
├── HD2IR
└── Model

[作为一个模组导入]
[分别导入]
```

对于低置信度结构：

```text
New Folder.zip
├── data1.zip
├── something.zip
└── model.zip
```

则：

```text
发现多个可能相关组件

请选择处理方式：

[合并]
[分别导入]
[忽略]
```

---

# 二十、禁止静默修改用户资源

程序不能擅自：

```text
移动用户原 ZIP
重命名 ZIP
删除 ZIP
重新压缩 ZIP
修改压缩包内容
```

导入阶段的原则：

```text
只读分析
```

原始资源保持不变。

---

# 二十一、内部标准化 Manifest

归并成功之后，生成 HD2Mimnt 自己的标准化描述。

示例：

```json
{
  "schemaVersion": 1,
  "modId": "generated-id",
  "name": "VRChat AIRI",
  "components": [
    {
      "id": "hd2ir",
      "type": "HD2IR",
      "source": "AIRI.hd2irpack.zip"
    },
    {
      "id": "model",
      "type": "Model",
      "source": "AIRI_Model.zip"
    }
  ]
}
```

这里的 Manifest 是：

> **HD2Mimnt 内部管理格式。**

不是要求作者修改原始 Mod。

---

# 二十二、Package Source 与 Deployable Files 必须分离

例如：

```text
Source:
AIRI.hd2irpack.zip
```

最终可能产生：

```text
Deployable:
bin/HD2IndependentRig/xxx.hd2phys
bin/HD2IndependentRig/xxx.rigbin
...
```

Manifest 中必须能够追踪：

```text
Source Package
        ↓
Component
        ↓
Deployable Files
```

这样以后：

```text
清理
卸载
恢复
配置切换
冲突分析
```

才有依据。

---

# 二十三、统一交给 Deployment Engine

归并后禁止 HD2IR 自己再实现一套文件部署逻辑。

最终流程：

```text
Logical Mod
    ↓
Components
    ↓
Deployable File Set
    ↓
Deployment Engine
```

统一执行：

```text
Add
Update
Remove
Skip
Transaction
Rollback
```

---

# 二十四、文件内容一致性

必须实现：

```text
Source == Target
```

时：

```text
Skip
```

而不是：

```text
Delete
↓
Copy
```

行为应该类似此前专用软件日志中看到的：

```text
文件内容一致，无需写入
```

这个规则应成为统一部署引擎能力，而不是 HD2IR 特例。

---

# 二十五、Diff Engine

部署之前计算：

```text
Desired State
        ↓
Current State
        ↓
Diff
```

结果：

```text
Added
Updated
Removed
Unchanged
Conflict
```

例如：

```text
Added:    2
Updated:  13
Removed:  4
Unchanged: 18
Conflict: 0
```

然后再执行。

---

# 二十六、清理逻辑

HD2IR 最大的特殊性之一就是：

> 新配套部署前可能需要清理旧配套。

因此不能只依赖“当前 ZIP 里有什么”。

需要维护：

```text
Component Ownership
```

即：

```text
某个文件属于哪个 Logical Mod
某个文件属于哪个 Component
某个文件由哪个事务部署
```

---

# 二十七、Ownership Model

建议内部记录：

```text
FileOwnership
├── LogicalModId
├── ComponentId
├── PackageId
├── DeployPath
├── ContentHash
└── TransactionId
```

例如：

```text
bin/HD2IndependentRig/A.rigbin

Owner:
Mod = AIRI
Component = HD2IR
Package = AIRI.hd2irpack.zip
```

这样清理才能精确。

---

# 二十八、为什么不能简单按文件名删除

禁止：

```text
清理 HD2IR
↓
删除整个 bin/HD2IndependentRig/
```

因为其中可能存在：

```text
AIRI
B角色
C护甲
共享资源
其他模组
```

正确行为必须是：

```text
查询 Ownership
        ↓
只删除属于目标 Mod/Component 的文件
```

---

# 二十九、共享文件处理

特别关注：

```text
shared_*
```

以及可能存在的：

```text
canonical.*
consumers.*
requires-*
```

这类资源名称从当前日志看存在明显共享/依赖语义，但其完整所有权规则尚未完全确认。

因此后端需要设计：

```text
Shared Resource
```

概念。

不能简单：

```text
一个 Mod 删除
→
共享文件立即删除
```

应该：

```text
检查引用者
    ↓
仍有其他 Mod 使用？
    ├─ 是 → 保留
    └─ 否 → 可删除
```

---

# 三十、依赖关系

以后需要支持：

```text
Mod A
 └── requires HD2IR shared component

Mod B
 └── requires same shared component
```

这样：

```text
删除 A
```

不应该把：

```text
B 仍然需要的 Shared Resource
```

一起删除。

因此建议建立：

```text
DependencyGraph
```

---

# 三十一、Addon / 幻化资源 / Lua 等组件

根据目前观察到的某款专用软件日志，它能够在部署 HD2IR 时明确保持：

```text
addon
幻化资源
Lua
```

不受该操作影响。

HD2Mimnt 因此应该从架构层支持：

```text
Component-specific deployment
```

例如：

```text
AIRI
├── HD2IR
├── Addon
├── Appearance
└── Lua
```

用户只启用：

```text
HD2IR
```

则只改变：

```text
HD2IR
```

其他组件保持现状。

---

# 三十二、配置文件系统接入

最终配置文件不应保存：

```text
AIRI.hd2irpack.zip
AIRI_Model.zip
Texture.zip
```

而保存：

```text
LogicalModId
```

例如：

```json
{
  "profile": "Profile-A",
  "enabledMods": [
    "mod.airi"
  ]
}
```

内部再展开：

```text
mod.airi
├── HD2IR
├── Model
└── Texture
```

---

# 三十三、配置切换流程

目标：

```text
切换配置文件
        ↓
读取目标 Logical Mod Set
        ↓
生成 Desired State
        ↓
与 Current State 做 Diff
        ↓
仅修改差异
        ↓
完成切换
```

禁止：

```text
切换配置
↓
所有模组重新解压
↓
全部重新部署
```

---

# 三十四、导入缓存

大型资源不应该每次导入都重新扫描。

建议建立：

```text
ArchiveAnalysisCache
```

关联：

```text
文件路径
文件大小
修改时间
Hash
```

如果原始 ZIP 没变化：

```text
直接使用上次分析结果
```

---

# 三十五、缓存失效条件

以下任一变化：

```text
路径变化
文件大小变化
修改时间变化
内容 Hash 变化
```

都应重新分析。

可根据性能要求选择：

```text
Fast Fingerprint
```

和：

```text
Full Hash
```

两级策略。

---

# 三十六、临时文件管理

原则：

```text
能不解压，就不解压
```

只有以下情况才允许解压：

```text
1. 压缩库无法直接读取内部 Entry
2. 某项检测必须读取真实文件内容
3. 最终部署需要提取文件
4. 用户明确要求导出
```

临时目录必须：

```text
可预测
可清理
有异常恢复机制
```

---

# 三十七、错误处理

至少覆盖：

```text
ZIP 损坏
嵌套 ZIP 损坏
密码 ZIP
不支持的压缩格式
路径异常
重复文件
同路径冲突
非法文件名
深层嵌套
超大压缩包
磁盘空间不足
读取失败
写入失败
文件被占用
```

任何单个组件解析失败：

原则上不应造成：

```text
整个 Mod Library 数据库损坏
```

---

# 三十八、冲突处理

例如：

```text
Mod A
└── bin/X.rigbin

Mod B
└── bin/X.rigbin
```

不能简单后导入者覆盖。

应该进入：

```text
Conflict Detection
```

状态：

```text
Conflict
├── File Path
├── Existing Owner
├── New Owner
├── Existing Hash
└── Incoming Hash
```

提供：

```text
覆盖
跳过
作为独立变体
```

或者在配置文件级别明确选择。

---

# 三十九、事务系统

所有实际部署变化必须进入统一事务系统。

事务至少记录：

```text
TransactionId
Timestamp
ProfileId
ModId
ComponentId
AddedFiles
UpdatedFiles
RemovedFiles
SkippedFiles
Conflicts
Hashes
```

例如：

```json
{
  "transactionId": "20261003_xxxxx",
  "operation": "deploy",
  "profileId": "default",
  "modIds": [
    "mod.airi"
  ],
  "changes": {
    "added": [],
    "updated": [
      "bin/HD2IndependentRig/xxx.rigbin"
    ],
    "removed": [],
    "skipped": [
      "bin/HD2IndependentRig/xxx.hd2phys"
    ]
  }
}
```

---

# 四十、空事务规则

如果：

```text
Added = 0
Updated = 0
Removed = 0
```

则：

```text
不创建空事务
```

日志记录：

```text
内容一致，无需写入；未创建事务。
```

这点可以直接保留目前参考软件中已经体现出的设计思想。

---

# 四十一、日志设计

日志必须区分：

```text
Import
Analyze
Detect
Merge
Plan
Deploy
Cleanup
Transaction
Error
```

不要全部使用：

```text
已更新
```

---

# 四十二、推荐日志格式

例如：

```text
[20:10:01.002] Import     开始导入：VRchat_AIRI.zip
[20:10:01.021] Analyze    发现 3 个嵌套压缩包
[20:10:01.030] Detect     检测到 HD2IR Package：AIRI.hd2irpack.zip
[20:10:01.032] Detect     检测到 Model Package：AIRI_Model.zip
[20:10:01.034] Detect     检测到 Texture Package：AIRI_Texture.zip
[20:10:01.041] Merge      3 个组件候选属于同一 Logical Mod
[20:10:01.050] Merge      Logical Mod：VRChat AIRI
[20:10:01.055] Import     导入完成
```

部署：

```text
[20:10:03.100] Plan       Added: 2
[20:10:03.100] Plan       Updated: 13
[20:10:03.100] Plan       Removed: 4
[20:10:03.100] Plan       Unchanged: 19
[20:10:03.101] Deploy     开始执行
[20:10:03.140] Deploy     更新 xxx.rigbin
[20:10:03.141] Deploy     跳过 xxx.hd2phys：内容一致
[20:10:03.160] Cleanup    清理 4 个旧文件
[20:10:03.180] Transaction 事务提交完成
[20:10:03.181] Deploy     Mod 部署完成
```

---

# 四十三、性能要求

这个功能最大的性能风险不一定是“复制文件”。

更可能是：

```text
递归扫描 ZIP
读取大量 Entry
Hash
嵌套包分析
模型资源扫描
```

因此必须：

```text
延迟解压
按需读取
分析缓存
取消支持
异步 I/O
避免重复 Hash
避免重复扫描
```

---

# 四十四、导入任务取消

导入大型模型包时，必须支持：

```text
CancellationToken
```

用户关闭导入窗口或者点击取消：

```text
停止扫描
停止分析
删除临时文件
保留已有 Library 数据
```

不得留下半成品 Mod。

---

# 四十五、线程模型

Archive Analysis 不应该运行在 UI Thread。

原则：

```text
UI
↓
ViewModel
↓
Import Service
↓
Background Task
↓
Archive Analyzer
```

UI 线程只负责：

```text
进度
状态
结果
取消
```

---

# 四十六、禁止在导入阶段直接部署

这是必须明确的边界：

```text
导入
≠
部署
```

正确：

```text
导入资源
↓
建立 Logical Mod
↓
加入 Library
```

然后用户：

```text
勾选 Mod
↓
Deploy
```

这样才能满足 HD2Mimnt 的配置文件管理模型。

---

# 四十七、删除逻辑

用户删除：

```text
VRChat AIRI
```

应该执行：

```text
删除 Logical Mod
↓
删除所有 Component 状态
↓
删除关联部署记录
↓
删除属于该 Mod 的已部署文件
↓
处理 Shared Resource 引用
↓
删除事务/历史关联
↓
删除 Library 元数据
```

但：

> **原始用户资源文件是否删除，要与“删除模组库记录”分开。**

建议默认保留原始源文件，除非用户明确选择：

```text
删除源文件
```

---

# 四十八、模组库数据与源文件分离

建议：

```text
Library
├── Mod Metadata
├── Component Metadata
├── Package Metadata
├── Ownership
├── Transactions
└── Analysis Cache
```

源 ZIP：

```text
由用户实际资源位置持有
```

或者：

```text
HD2Mimnt Managed Storage
```

两种模式都可以支持。

---

# 四十九、第一阶段不要做的事情

为了避免专项失控，第一版明确不做：

```text
1. AI 自动判断作者意图
2. 自动修复作者错误目录
3. 自动修改原始 ZIP
4. 无限递归 ZIP
5. 自动强制合并相似名称 Mod
6. 自动重打包资源
7. 自动修改 HD2IR 内部格式
8. 自己实现完整 HD2IR 格式转换器
```

先做到：

```text
识别
+
归并
+
标准化
+
统一部署
```

---

# 五十、实施阶段

## Phase 1 — Archive 基础层

实现：

```text
ArchiveInspector
ArchiveNode
ArchiveTree
NestedArchiveScanner
```

验证：

```text
单 ZIP
双层 ZIP
三层 ZIP
多 ZIP
```

---

## Phase 2 — HD2IR Detector

实现：

```text
Hd2IrDetector
Hd2IrDetectionRules
```

识别：

```text
.hd2irpack.zip
HD2IndependentRig
*.hd2phys
*.rigbin
*.hd2pose
*.hd2preview
*.hd2shared
```

注意：

这些规则应实现为可扩展规则集合，而不是大量硬编码散落在业务代码中。

---

## Phase 3 — Component Detector

实现：

```text
ComponentDetector
ComponentType
DetectionResult
```

首批：

```text
HD2IR
Model
Texture
Material
Lua
Addon
Unknown
```

---

## Phase 4 — Logical Mod Merger

实现：

```text
ModMerger
MergeCandidate
MergeDecision
MergeResult
```

支持：

```text
Automatic High Confidence Merge
Manual Confirmation
Separate Import
Ignore
```

---

## Phase 5 — Internal Manifest

实现：

```text
LogicalModManifest
ComponentManifest
PackageManifest
DeployableFileManifest
```

确定数据库/序列化格式。

---

## Phase 6 — Deployment Integration

让 HD2IR 与普通 Mod 共用：

```text
Diff Engine
Deployment Engine
Cleanup Engine
Transaction Engine
```

而不是另起炉灶。

---

## Phase 7 — Ownership / Dependency

实现：

```text
FileOwnership
SharedResource
DependencyGraph
ReferenceCount
```

解决：

```text
shared_*
canonical.*
requires-*
```

类资源的生命周期问题。

---

## Phase 8 — Profile Integration

接入：

```text
配置文件
模组启用状态
配置文件切换
批量部署
批量清理
```

确保：

```text
Profile Switch
```

只处理差异。

---

## Phase 9 — Performance Optimization

重点测试：

```text
100MB ZIP
500MB ZIP
1GB ZIP
多层 ZIP
大量小文件
大量嵌套 ZIP
多个 HD2IR
多个大型 Model Package
```

记录：

```text
分析耗时
Hash 耗时
归并耗时
Plan 耗时
部署耗时
事务耗时
内存峰值
CPU 峰值
```

---

# 五十一、测试矩阵

必须准备以下测试样本。

## Case A：标准双包

```text
Outer.zip
├── HD2IR.zip
└── Model.zip
```

预期：

```text
1 Logical Mod
2 Components
```

---

## Case B：三组件

```text
Outer.zip
├── HD2IR.zip
├── Model.zip
└── Texture.zip
```

预期：

```text
1 Logical Mod
3 Components
```

---

## Case C：嵌套

```text
Outer.zip
└── Resource.zip
    ├── HD2IR.zip
    └── Model.zip
```

预期：

```text
1 Logical Mod
2 Components
```

---

## Case D：深层嵌套

```text
A.zip
└── B.zip
    └── C.zip
        └── D.zip
            └── HD2IR.zip
```

预期：

```text
正常分析
```

直到最大深度。

---

## Case E：超出深度

```text
A.zip
└── B.zip
    └── C.zip
        └── D.zip
            └── E.zip
                └── F.zip
                    └── HD2IR.zip
```

预期：

```text
停止继续递归
记录 Warning
程序不崩溃
```

---

## Case F：同名不同模组

```text
AIRI.zip
AIRI_Summer.zip
```

预期：

```text
不得静默合并
```

---

## Case G：共享资源

```text
Mod A → Shared Resource X
Mod B → Shared Resource X
```

删除 A：

```text
X 必须保留
```

删除 B：

```text
X 才允许进入删除流程
```

---

## Case H：重复部署

第一次：

```text
Deploy
```

第二次：

```text
Deploy
```

预期：

```text
第二次大量 Unchanged
不重复写入
不创建空事务
```

---

# 五十二、验收标准

专项完成必须满足：

```text
[ ] 能导入单层 HD2IR
[ ] 能导入标准双包
[ ] 能处理多组件 ZIP
[ ] 能处理嵌套 ZIP
[ ] 能识别 HD2IR
[ ] 能识别模型组件
[ ] 能建立 Logical Mod
[ ] 能人工确认归并
[ ] 能防止错误自动归并
[ ] 能保存内部 Manifest
[ ] 能交给统一 Deployment Engine
[ ] 能进行 Diff
[ ] 能跳过内容一致文件
[ ] 能清理旧组件
[ ] 能追踪文件 Ownership
[ ] 能处理共享资源
[ ] 能建立事务
[ ] 能删除整个 Logical Mod
[ ] 能接入配置文件
[ ] 配置文件切换不重复部署全部资源
[ ] 大型 ZIP 分析不阻塞 UI
[ ] 支持取消
[ ] 异常不会破坏 Library
[ ] 无临时文件泄漏
```

---

# 五十三、最终设计结论

HD2Mimnt 对 `.hd2irpack.zip` 的支持不应该定义为：

```text
“增加一个特殊 Mod 类型”
```

而应该定义为：

```text
“增加一个能够理解复杂资源包结构的导入与标准化系统”
```

最终架构：

```text
                         用户资源
                            │
                            ▼
                    ┌────────────────┐
                    │ Archive Import │
                    └───────┬────────┘
                            ▼
                    ┌────────────────┐
                    │ Archive Tree   │
                    └───────┬────────┘
                            ▼
                    ┌────────────────┐
                    │ Component      │
                    │ Detection      │
                    └───────┬────────┘
                            ▼
                    ┌────────────────┐
                    │ Mod Merger     │
                    └───────┬────────┘
                            ▼
                    ┌────────────────┐
                    │ Logical Mod    │
                    └───────┬────────┘
                            ▼
                    ┌────────────────┐
                    │ Manifest       │
                    └───────┬────────┘
                            ▼
                    ┌────────────────┐
                    │ Desired State  │
                    └───────┬────────┘
                            ▼
                    ┌────────────────┐
                    │ Diff Engine    │
                    └───────┬────────┘
                            ▼
                    ┌────────────────┐
                    │ Deployment     │
                    │ Engine         │
                    └───────┬────────┘
                            ▼
                    ┌────────────────┐
                    │ Transaction    │
                    └────────────────┘
```

最终用户看到的是：

```text
VRChat AIRI 原皮
├── HD2IR
├── 模型
├── 材质
└── 其他组件
```

而不是作者原始的：

```text
ZIP
 └── ZIP
      └── ZIP
           ├── ZIP
           └── ZIP
```

**作者怎么打包，是 Import Layer 的问题。**

**用户怎么管理，是 Logical Mod Layer 的问题。**

**最终文件怎么落盘，是 Deployment Engine 的问题。**

这三件事必须彻底分离。

---

# 五十四、开发优先级

最终执行顺序固定为：

```text
P0
Archive Tree
Nested Archive Scanner
HD2IR Detector

P1
Component Detector
Logical Mod
Mod Merger

P2
Manifest
Ownership
Dependency

P3
Deployment Integration
Cleanup
Transaction

P4
Profile Integration
Performance Optimization

P5
复杂异常兼容
更多作者打包方式
更多组件类型
```

开发过程中如果某个 HD2IR 特殊格式尚未完全确认：

```text
先建立可扩展检测框架
不要把未知规则硬编码成永久规范
```

这样以后拿到更多真实样本时，只需要增加 Detector Rule / Parser，而无需重写整个模组管理系统。