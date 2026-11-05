# HD2 重定向形变校验（Phase 4 第一轮，合成数据）

工具：[`research/verify_hd2_retarget_deformation.py`](verify_hd2_retarget_deformation.py)
测试：[`../tests/test_hd2_retarget_deformation.py`](../tests/test_hd2_retarget_deformation.py)
生成件：[`skeleton_comparison/retarget_deformation_checks.md`](skeleton_comparison/retarget_deformation_checks.md)（JSON 同目录，已被 `.gitignore` 排除）

## 1. 这一步要解决什么

路线图 0.8.4 的退出条件是"骨盆、脊柱、头、肩、肘、腕、髋、膝、踝在真实目标 Unit 上的形变检查通过"。
在能拿到真实姿态之前，先把已经量到的约定写成一台**只吃合成数据**的检验机：如果线性混合蒙皮的
刚性距离、权重求和、混合单调性这些不变量都成立不了，那说明palette 公式或矩阵顺序本身就写错了，
没必要再去做权重迁移。

本文件里没有任何来自游戏运行时的观测。所有结论标签最高只能到 Candidate/Validated(针对合成模型)。

## 2. 采用的约定（全部来自已量到的证据）

| 项 | 取值 | 来源 |
| --- | --- | --- |
| 矩阵布局 | row-major 4×4，行向量 `p × M`，平移在浮点 12–15 | 顶点位置空间探针 §2.5、mesh 审计 |
| palette 候选 | `mesh_world × inverse(bone_world)` | BoneInfo palette 比对（162 槽 `1.74e-7`） |
| 骨骼姿态构造 | `rotation × translate(joint)`（先绕自身原点转，再平移到关节）；对已旋转的骨骼用共轭 `bone_world × T(-joint) × R × T(joint)` | 本轮发现，见 §3.1 与 §4 |
| 跨引擎适配 | 尺度 `0.030524`，符号 `(-1, -1, +1)`＝绕 +Z 转 180° | 静止关节对与朝向证据 |
| 混合 | 按权重和归一化 | 上一轮残差分析：非归一化会偏移 `(Σw-1) × 静态目标` |

## 3. 写这台机器时抓到的两个自我错误

### 3.1 姿态矩阵顺序把关节本身也转了（Validated，针对合成模型）

第一稿把 posed 写成 `multiply(translate(joint), rotate_x(θ))`，即"先平移再旋转"。在行向量约定下这会
把关节向量一起旋转：`transform_point(0, T·R) = R(joint)`，于是关节被甩到以父原点为圆心的球面上。
用 `joint = (0.1, -0.2, 0.9417)`、`θ = 45°` 实测，关节偏移超过 `0.3` 米，而正确顺序
`R·T` 让 `transform_point(0, posed)` 与 `joint` 的差小于 `1e-12`。

正确的行向量 TRS 顺序是 `scale × rotation × translate`。这台机器的检查项
`joint_stays_fixed` 就是专门钉住这一点的：写反顺序时它一定红。

### 3.2 "期望值"自己算错，却仍然当通过了（Observed）

第一稿的 `expected` 用了 `pivot_moved + R(v) - v`，把"绕关节旋转"写成了"绕原点旋转再加上关节位移"，
而该字段并不在判定门里，所以一个错误数字会静静躺在报告里。现在期望值直接写成
`joint + R·(v - joint)`，报告字段 `expected_minus_blended` 实测 `0.0`，并升级为硬检查
`matches_rigid_rotation_about_joint`。教训：任何"参考值"必须进门，否则等于没测。

## 4. 九个用例、三十五条检查（全绿）

复现：`py -3 research/verify_hd2_retarget_deformation.py`（全通过时退出码 0，有失败返回 1）

| 用例 | 检查数 | 关键度量 |
| --- | ---: | --- |
| `single_bone_rigid_rotation` | 4 | 期望值与混合结果差 `0.0`；顶点到关节距离 `1.142969` 保持不变 |
| `two_bone_blend_sweep` | 5 | 位移随权重严格线性 `0 → 0.184989`；半径在 0.5 处下沉 `0.2417 → 0.223302` |
| `weight_sum_scale_invariance` | 5 | `(0.7,0.3)`、`(1.4,0.6)`、`(70,30)` 结果完全一致；全零权重抛 `ValueError` |
| `translated_root` | 3 | 顶点偏移与关节偏移逐轴相等，量 `0.722842` |
| `hinge_on_a_rotated_bone` | 5 | 带旋转的骨骼 + 带旋转的 mesh_world：期望差 `0.0`，朴素绕原点旋转把关节甩开 `0.727407` |
| `double_application_refused` | 3 | 正确 `0.0`；重复带 `mesh_world` 偏 `1.465405`；palette 顺序写反偏 `0.621278` |
| `non_uniform_scale_gate` | 3 | 尺度比 `2.0`、归一化后非对角 `1.0` → 拒绝；均匀缩放比 `1.0` 不误判 |
| `adapter_scale_and_orientation` | 4 | 线性部分行列式 `+1.0`（是旋转不是镜像）；左仍排在右之前 |
| `adapter_maps_joint_deltas` | 3 | 差值长度比 `0.030524`；先各自适配再相减与先相减再适配一致 |

`hinge_on_a_rotated_bone` 是通往真实矩阵的那一步：HD2 的 TransformInfo 不是纯平移，所以"关节不动"这条
不变量必须在**骨骼自身带旋转、且 mesh_world 也带旋转**的情形下再验一次。这里的铰链写成共轭
`bone_world × T(-joint) × R × T(joint)`，于是 `palette × posed` 相对静止恰好差一个"绕真实关节的刚体旋转"；
作为对照保留的 `bone_world × R` 把关节甩到 `0.727407` 之外，正是 §3.1 那个坑在带旋转样本上的放大版。

值得单独记一笔的是 `double_application_refused`：它现在用带旋转的 `mesh_world` 与
`bone_world`（15° Z 旋转 + 平移，25° X 旋转 + 平移），所以重复放置和顺序写反都能被**旋转部分**
抓到，而不只是平移偏移；`0.621278` 这个差值说明 palette 的两个因子顺序是可判别的，不能靠
"看起来差不多"糊过去。

## 5. 明确没有证明的东西

- 这是代数不变量检查，不是运行时观测；通过只说明"在该模型下没有丢掉或多乘变换"。
- 非均匀缩放 × 旋转的组合仍然被**拒绝建模**而不是被验证，与解析器的拒绝策略一致。
- 关节位置只是借用量到的 HD2 静止高度当合理枢轴，本机器不解码任何 Unit GPU 负载。
- 两段混合中段到关节的距离变小（`0.2417 → 0.223302`）是 LBS 固有的体积损失，这里只记录不修正；
  将来若要用双四元数之类的手段，得另立实验。
- 适配器是"差值映射"：它的尺度来自成对骨段长度，所以把 `adapter(绝对坐标)` 直接和另一个模型的
  地标比较是无意义的。报告里 `absolute_adapted_hand_vs_other_model_landmark = 0.721343` 只作为
  量级参考，不进判定门。Phase 5 的迁移必须走骨间差值/palette，不能当世界坐标变换用。

## 6. 真实矩阵上的铰链检验（Phase 4 第二轮）

工具：[`research/verify_hd2_bone_hinge_on_real_matrices.py`](verify_hd2_bone_hinge_on_real_matrices.py)
测试：[`../tests/test_hd2_real_bone_hinge.py`](../tests/test_hd2_real_bone_hinge.py)（14 条）
生成件：[`skeleton_comparison/hd2_real_bone_hinge.md`](skeleton_comparison/hd2_real_bone_hinge.md)（三个入库样本）
与 [`skeleton_comparison/hd2_real_bone_hinge_8units.md`](skeleton_comparison/hd2_real_bone_hinge_8units.md)（八个 Unit 泛化）

这台机器不再合成枢轴：它读 `research/skinning_audits/` 与本地 Unit dump，把真实的 BoneInfo
palette 矩阵和真实的 TransformInfo 世界矩阵拿出来，绕**真实关节**加一个 30° 铰链，然后检查

```
palette × posed_bone  ==  mesh_world × hinge(joint, rotation)
```

默认覆盖每个顶点、每个被正权重用到的骨骼（`--sample-limit` / `--max-bones` 可缩小）。实测：

| 运行 | Unit | 网格 | 骨骼（按 Unit+Mesh+槽计） | 行数 | 失败行 | 铰链最大误差 | 刚性最大误差 | 混合线性最大误差 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 三个入库样本 | 3 | 10 | 162 | 810 | 0 | `3.42123e-07` | `2.30036e-07` | `3.26341e-07` |
| 八 Unit 泛化 | 8 | 30 | 240 | 1200 | 0 | `3.42123e-07` | `2.30036e-07` | `3.26341e-07` |

真实关节样例：`l_thigh` 在 `(-0.086986, -0.018955, 0.941668)`，`l_shoulder` 在
`(-0.210121, -0.068876, 1.47449)`。avatar 网格铰链了 61 个骨骼，与解析器早先按正权重得到的
61 个活动命名变换一致。

对照组（**朴素构造** `bone_world × rotation`，即绕模型原点旋转）：八 Unit 的 1200 行里
1195 行被明确区分、5 行因关节恰好在原点而判为 `identical`（不计为证据），0 行落在
"应区分却没区分"。真实姿态下两种构造的顶点级差别不是抽象的：`l_arm_culling` 的 `dropped_placement_max_error`
最大到 `1.782138` 米，肩部护甲四个 LOD 为 `1.464925` 米。

### 6.1 三条必须说清楚的局限（本轮最重要的部分）

1. **铰链恒等式与静止不变量是同一条矩阵等式**。`palette × posed = mesh_world × hinge` 展开后
   就是 `palette × bone_world = mesh_world`，所以本轮不是"新增了一条独立证据"，而是把同一契约
   在旋转和真实关节下重新走了一遍，并首次在**姿态空间**里量出两种构造的差别。
2. **共轭铰链的"关节不动"是代数恒等**：共轭以 `B` 自己的平移行为锚点，`origin × posed` 必然等于
   该关节，所以这条只作为构造审计（`joint_fixity_holds_by_construction`），已从判定门里移出。
   真正有判别力的是铰链误差、刚性距离误差、混合线性以及朴素对照。
3. **`lanes_unresolved` 为 0 不代表部分蒙皮问题消失**。选定配对只用第一个骨骼索引属性，
   第二条与第三条属性的通道被单独计为"未检视"：八 Unit 里 13 个网格共
   `158,120` 个通道槽位从未进入检查（单网格最大 `24,920` 属 torso 样本，两件
   `g_torso_undergarment_*` 各 `22,016`）。675 行来自配对不唯一（`pairing_ambiguous`）的网格，
   且这 675 行的备选属性**装的是不同的骨骼索引**；没有任何一行需要动用"覆盖不足半数即视为空过"
   的剔除规则（`rows_with_vacuous_models_excluded = 0`）。
   测试把这条钉住了：若把 palette 的两个因子写反，配对门会直接**拒绝该网格**
   （`no_passing_weight_pairing`），而不是报出一个假的通过。

### 6.2 索引/权重配对在静止数据上不可判（Observed，新测得）

三个索引属性的网格上，读法并不唯一，而且**不是同一回事都能过**：

| Unit | 通过的单属性读法 | 受影响行数 |
| --- | --- | ---: |
| torso（5 个网格） | attr0 / attr1 / attr2 三种全过 | 465 |
| 内衣 ×2（各 4 个网格） | 只有 attr0 / attr1 两种过（常量 16 那条过不了） | 105 + 105 |
| 其余 5 个 Unit | 唯一：attr0 | 625 |

每种通过的读法权重和都≈1、加结果都落在同一个 `p × mesh_world` 上，但它们引用的骨骼索引各不相同。
以 `l_arm_culling` 为例，attr0 有 13 种索引行、attr1 有 6 种、attr2 只有 2 种（常量
`(1,0,0,0)` 与 `(0,0,0,0)`）；内衣网格的 attr2 是常量 `(16,0,0,0)`/`(0,0,0,0)`，与早先
`vec4_uint32` 普查一致。把三条属性同时接到同一行权重上，权重和立刻变成 `2.994135 ~ 3.0`，
说明这行权重**不可能同时服务三条属性**。

因此："哪条索引属性与权重通道配对"在静止数据里是无法判定的，不是打分器的缺陷。
工具现在把 `passing_models`、`vacuous_models` 和 `alternative_passing_models_differ` 一起写进报告，
只声明"选了哪一个 + 还有哪些并列"，不再把这件事当作已经解决。Phase 5 的权重迁移要么需要一份
**带姿态/动画**的样本（不同角度会让各读法分开），要么需要 SDK 侧对顶点流布局的明确说明；
在此之前迁移只能覆盖被选中的那一条属性，其余通道保持未检视。

另外记录一条几何事实：丢掉 `mesh_world` 造成的差距是 `|placed − raw|`，而刚体旋转保持距离，
所以铰链不会改变它——这正是"静止检查已经裁决了放置空间"的原因，报告里保留该度量只是为了
在姿态语境下给出米数量级。

## 7. 下一步（按顺序）

1. ~~用真实 Unit 的静止 palette 跑同一台机器的接口~~ 已完成，见 §6。
2. 配对歧义已在静止数据上**证明不可判**（§6.2），所以收敛它需要新的输入，二选一：
   (a) 一份带姿态/动画的 HD2 样本——不同角度会让"权重接哪条索引属性"的几种读法分开；
   (b) SDK 侧对骨骼索引顶点流布局的明确说明。`vec4_uint32` 常量的语义仍是 Unknown。
   在此之前，Phase 5 的权重迁移必须显式声明只覆盖被选中的那条属性，其余 `158,120` 个通道
   保持"未检视"，不能写成已解决。
3. 拿到真实动画姿态（或自制旋转）后，才谈"对真实目标 Unit 的形变检查"这一退出条件；
   本轮的旋转是合成的，游戏是否用同一种局部旋转构造尚未观测。
4. 非均匀缩放与旋转的组合仍无任何真实样本，保持"拒绝建模"。

## 8. 外部参考

- glTF 2.0 规范 §3.7.3.3 "Skinned Mesh Attributes"（原文："The skinning matrix is the product of
  the joint matrix and inverse bind matrix."），与本机 palette 候选 `mesh_world × inverse(bone_world) ×
  bone_world` 同构：Copyright 2013–2021 The Khronos Group Inc.，本仓库只引用公式结构，未复制正文。
  <https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html>
- HD2SDK-AQ-Edition 的 Unit / Bones 读取实现（palette 与骨骼表的实测来源，前面阶段已核对过）：
  <https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/stingray/unit.py>、
  <https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/stingray/bones.py>
- HD2 Modding Wiki（网格与材质语义背景，未用于本轮判定）：
  <https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/overview.html>

两段混合中段的体积损失是线性混合蒙皮自身的几何性质，本轮用度量直接演示而非引用外部结论。
第三方 SDK 与维基内容按版本作用域引用，许可证与来源约束见 [README](../README.md) 与
`docs/agent-guides/platforms-and-downloads.md`。
