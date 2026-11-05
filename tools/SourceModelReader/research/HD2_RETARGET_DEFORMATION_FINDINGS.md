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
| 骨骼姿态构造 | `rotation × translate(joint)`（先绕自身原点转，再平移到关节） | 本轮发现，见 §3.1 |
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

## 4. 八个用例、三十条检查（全绿）

复现：`py -3 research/verify_hd2_retarget_deformation.py`（全通过时退出码 0，有失败返回 1）

| 用例 | 检查数 | 关键度量 |
| --- | ---: | --- |
| `single_bone_rigid_rotation` | 4 | 期望值与混合结果差 `0.0`；顶点到关节距离 `1.142969` 保持不变 |
| `two_bone_blend_sweep` | 5 | 位移随权重严格线性 `0 → 0.184989`；半径在 0.5 处下沉 `0.2417 → 0.223302` |
| `weight_sum_scale_invariance` | 5 | `(0.7,0.3)`、`(1.4,0.6)`、`(70,30)` 结果完全一致；全零权重抛 `ValueError` |
| `translated_root` | 3 | 顶点偏移与关节偏移逐轴相等，量 `0.722842` |
| `double_application_refused` | 3 | 正确 `0.0`；重复带 `mesh_world` 偏 `1.465405`；palette 顺序写反偏 `0.621278` |
| `non_uniform_scale_gate` | 3 | 尺度比 `2.0`、归一化后非对角 `1.0` → 拒绝；均匀缩放比 `1.0` 不误判 |
| `adapter_scale_and_orientation` | 4 | 线性部分行列式 `+1.0`（是旋转不是镜像）；左仍排在右之前 |
| `adapter_maps_joint_deltas` | 3 | 差值长度比 `0.030524`；先各自适配再相减与先相减再适配一致 |

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

## 6. 下一步（按顺序）

1. 用真实目标 Unit 的静止 palette 跑同一台机器的接口：把合成 `bone_world` 换成解析出来的
   TransformInfo 世界矩阵，检查 `rest_reproduces_stored_position` 是否在真数据上成立
   （顶点位置空间探针 §2.5 已经在 211,956 / 389,215 条通道上做过等价检查，这一步是复用而非重做）。
2. 先解决 `vec4_uint32` 第三骨骼索引属性的语义（Unknown），否则部分蒙皮的 1,045 条未解析通道
   会让真实姿态检查不完整。
3. 拿到真实动画姿态（或自制旋转）后，才谈"对真实目标 Unit 的形变检查"这一退出条件。

## 7. 外部参考

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
