# Phase 15: Integrated Armor Workflow - 完整规划

## 概述

Phase 15 实现完整的 Integrated Armor Workflow，这是 HD2Mimnt 中最复杂的 Workflow 之一。

根据开发计划（Section 104），Integrated Armor 包括：

```text
✅ Phase 15.1: Basic Workflow (已完成)
⬜ Phase 15.2: SDK Integration
⬜ Phase 15.3: Custom Skeleton
⬜ Phase 15.4: Physics System
⬜ Phase 15.5: Advanced Variants (Body/Head)
⬜ Phase 15.6: Armor Illusion
⬜ Phase 15.7: Lua-ReShade Integration
⬜ Phase 15.8: Batch & Anti-Dismemberment
```

---

## Phase 15.1: Basic Workflow ✅ (已完成)

### 实现内容
- ✅ ArmorManifest 数据结构
- ✅ ArmorOption (Gameplay/Appearance)
- ✅ VariantOption (Body/Head)
- ✅ IntegratedArmorWorkflow 基础实现
- ✅ Manifest 解析和验证
- ✅ 基础部署文件生成
- ✅ 20 个单元测试

### 测试结果
```
IntegratedArmorWorkflow: 20/20 ✅
总测试数: 181
```

---

## Phase 15.2: SDK Integration

### 目标
集成 Helldivers 2 Armor SDK，支持从 SDK 项目导入和构建。

### 实现内容

#### 1. SDK 项目结构识别
```csharp
public interface ISdkDetector
{
    Task<bool> IsSdkProjectAsync(string path, CancellationToken cancellationToken = default);
    Task<SdkProjectInfo> AnalyzeSdkProjectAsync(string path, CancellationToken cancellationToken = default);
}
```

#### 2. SDK 构建集成
```csharp
public interface ISdkBuilder
{
    Task<SdkBuildResult> BuildAsync(string sdkProjectPath, CancellationToken cancellationToken = default);
    IObservable<SdkBuildProgress> BuildWithProgress(string sdkProjectPath);
}
```

#### 3. SDK 输出导入
- 将 SDK 构建输出自动导入到 Mod Library
- 保留 SDK 项目关联信息
- 支持增量构建和热重载

### 成功标准
- ✅ 能够检测 SDK 项目
- ✅ 能够解析 SDK 项目配置
- ✅ 能够调用 SDK 构建工具
- ✅ 能够导入构建输出
- ✅ 10+ 单元测试

---

## Phase 15.3: Custom Skeleton

### 目标
支持自定义骨骼的护甲 Mod。

### 实现内容

#### 1. Skeleton 文件检测
```csharp
public class SkeletonInfo
{
    public string SkeletonFile { get; set; } = string.Empty;
    public List<string> BoneNames { get; set; } = new();
    public bool IsCustom { get; set; }
}
```

#### 2. Skeleton 验证
- 骨骼文件存在性检查
- 骨骼结构有效性验证
- 与游戏原生骨骼的兼容性检查

#### 3. 部署特殊处理
- Custom Skeleton 文件优先部署
- 依赖关系正确处理

### ArmorManifest 扩展
```json
{
  "features": {
    "customSkeleton": true
  },
  "skeleton": {
    "file": "skeletons/custom_skeleton.skel",
    "type": "custom",
    "bones": [...]
  }
}
```

### 成功标准
- ✅ 能够检测 Custom Skeleton
- ✅ 能够验证骨骼文件
- ✅ 正确处理部署顺序
- ✅ 8+ 单元测试

---

## Phase 15.4: Physics System

### 目标
支持物理系统的护甲 Mod（如布料物理、链条物理等）。

### 实现内容

#### 1. Physics 配置
```csharp
public class PhysicsConfig
{
    public bool Enabled { get; set; }
    public PhysicsType Type { get; set; } // Cloth, Chain, Rigid
    public string ConfigFile { get; set; } = string.Empty;
    public Dictionary<string, object> Parameters { get; set; } = new();
}
```

#### 2. Physics 文件验证
- Physics 配置文件存在性
- Physics 网格文件检查
- Physics 参数合法性验证

#### 3. 性能警告
- 检测高性能消耗的 Physics 配置
- 给出优化建议

### ArmorManifest 扩展
```json
{
  "features": {
    "physics": true
  },
  "physics": {
    "type": "cloth",
    "config": "physics/cloth_config.json",
    "meshes": ["physics/cloth_mesh.phys"],
    "quality": "high"
  }
}
```

### 成功标准
- ✅ 能够检测 Physics 配置
- ✅ 能够验证 Physics 文件
- ✅ 能够识别性能问题
- ✅ 8+ 单元测试

---

## Phase 15.5: Advanced Variants (Body/Head)

### 目标
扩展 Variant 系统，支持更复杂的变体逻辑。

### 实现内容

#### 1. Variant 条件系统
```csharp
public class VariantCondition
{
    public string Type { get; set; } = string.Empty; // Gender, ArmorType, etc.
    public List<string> AllowedValues { get; set; } = new();
    public bool Required { get; set; }
}
```

#### 2. Variant 组合验证
- 检测 Body/Head 组合兼容性
- 验证 Variant 依赖关系
- 警告不兼容的组合

#### 3. Variant 预览
- 生成 Variant 组合预览
- 显示 Variant 差异对比

### ArmorManifest 扩展
```json
{
  "bodyVariants": [
    {
      "id": 1,
      "name": "Male Body A",
      "files": ["variants/male_body_a.patch_0"],
      "enabled": true,
      "conditions": {
        "gender": ["male"],
        "armorType": ["heavy", "medium"]
      },
      "compatibleHeadVariants": [1, 2, 3]
    }
  ]
}
```

### 成功标准
- ✅ 支持条件 Variant
- ✅ 验证 Variant 组合
- ✅ 10+ 单元测试

---

## Phase 15.6: Armor Illusion

### 目标
支持 Armor Illusion 系统（外观护甲覆盖真实护甲属性）。

### 实现内容

#### 1. Illusion 配置
```csharp
public class IllusionConfig
{
    public string BaseArmorId { get; set; } = string.Empty; // 真实属性来源
    public string AppearanceArmorId { get; set; } = string.Empty; // 外观来源
    public bool PreserveStats { get; set; } = true;
}
```

#### 2. Illusion 验证
- 验证 Base Armor 存在
- 验证 Appearance Armor 存在
- 检查属性兼容性

### ArmorManifest 扩展
```json
{
  "features": {
    "illusion": true
  },
  "illusions": [
    {
      "name": "Heavy Armor with Light Appearance",
      "baseArmor": "armor_heavy",
      "appearanceArmor": "appearance_light",
      "preserveStats": true
    }
  ]
}
```

### 成功标准
- ✅ 支持 Illusion 配置
- ✅ 验证 Illusion 有效性
- ✅ 8+ 单元测试

---

## Phase 15.7: Lua-ReShade Integration

### 目标
集成 Lua 脚本和 ReShade 支持。

### 实现内容

#### 1. Lua 脚本检测
```csharp
public class LuaScriptInfo
{
    public string ScriptFile { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = new();
    public Dictionary<string, object> Config { get; set; } = new();
}
```

#### 2. ReShade 预设
```csharp
public class ReShadePreset
{
    public string PresetFile { get; set; } = string.Empty;
    public List<string> Shaders { get; set; } = new();
    public Dictionary<string, object> Settings { get; set; } = new();
}
```

#### 3. 部署集成
- Lua 脚本部署
- ReShade 预设部署
- 依赖文件处理

### ArmorManifest 扩展
```json
{
  "features": {
    "luaReShade": true
  },
  "lua": {
    "scripts": ["scripts/armor_behavior.lua"],
    "config": "scripts/config.json"
  },
  "reshade": {
    "presets": ["reshade/armor_effects.ini"],
    "shaders": ["shaders/glow.fx", "shaders/reflection.fx"]
  }
}
```

### 成功标准
- ✅ 检测 Lua 脚本
- ✅ 检测 ReShade 预设
- ✅ 正确部署相关文件
- ✅ 10+ 单元测试

---

## Phase 15.8: Batch & Anti-Dismemberment

### 目标
支持批量操作和防肢解功能。

### 实现内容

#### 1. Batch Operations
```csharp
public interface IBatchArmorProcessor
{
    Task<BatchResult> ProcessBatchAsync(List<string> armorIds, BatchOperation operation);
    Task<BatchResult> EnableMultipleAsync(List<string> armorIds);
    Task<BatchResult> DisableMultipleAsync(List<string> armorIds);
}
```

#### 2. Anti-Dismemberment
```csharp
public class AntiDismembermentConfig
{
    public bool Enabled { get; set; }
    public List<string> ProtectedParts { get; set; } = new();
    public string ConfigFile { get; set; } = string.Empty;
}
```

### ArmorManifest 扩展
```json
{
  "features": {
    "antiDismemberment": true
  },
  "antiDismemberment": {
    "protectedParts": ["head", "arms", "legs"],
    "config": "config/anti_dismember.json"
  }
}
```

### 成功标准
- ✅ 支持批量启用/禁用
- ✅ 支持防肢解配置
- ✅ 8+ 单元测试

---

## 总体测试目标

```
Phase 15.1: 20 测试 ✅
Phase 15.2: 10 测试
Phase 15.3: 8 测试
Phase 15.4: 8 测试
Phase 15.5: 10 测试
Phase 15.6: 8 测试
Phase 15.7: 10 测试
Phase 15.8: 8 测试
-------------------
总计: 82 测试
```

---

## 架构原则

### 1. 渐进式增强
每个 Phase 都是独立可用的，不依赖后续 Phase。

### 2. 向后兼容
新功能不破坏已有的 Basic Workflow。

### 3. 可选特性
所有高级功能都是可选的，通过 `features` 字段控制。

### 4. 清晰的错误信息
每个验证步骤都提供详细的错误信息和修复建议。

---

## 当前状态

✅ Phase 15.1 完成（181 测试通过）

下一步：Phase 15.2 - SDK Integration
