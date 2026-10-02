# Phase 19: AI Integration - 开发计划

## Phase 19 概述

根据 "HD2Mimnt v2 总体开发计划.md" 的要求，Phase 19 实现 AI 辅助功能。

## 设计原则（来自计划 Section 113-115）

### 核心原则

1. **AI 是可选外挂**
   - Core 功能没有 AI 也必须完整运行
   - AI 不能成为核心依赖

2. **AI 只提供解释，不执行操作**
   ```text
   正确：
   Analyzer → AnalysisContext → AI Provider → Explanation
   
   错误：
   AI → Core Tools → 自动操作
   ```

3. **明确不做**
   - ❌ 自动安装
   - ❌ 自动删除
   - ❌ 自动部署
   - ❌ 自动修改游戏
   - ❌ 自动执行脚本
   - ❌ 通用 AI Agent

## AI 功能清单（Section 235 & 115）

### 支持的功能
- ✅ Translation - 翻译（Mod 描述、README）
- ✅ Analysis - Mod 分析解释
- ✅ Log Explanation - 日志解释
- ✅ Summary - README/Changelog 总结
- ✅ Security Assistance - 安全辅助检查
- ✅ 冲突解释

## 架构设计

### 1. Core Layer (`HD2Mimnt.Core`)

#### 实体
```csharp
// AI 提供商配置
public class AIProviderConfig
{
    public string Id { get; set; }
    public string Name { get; set; }
    public AIProviderType Type { get; set; }
    public string ApiEndpoint { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public bool IsEnabled { get; set; }
    public int MaxTokens { get; set; }
    public double Temperature { get; set; }
}

// AI 分析上下文
public class AIAnalysisContext
{
    public string ContextType { get; set; }  // Mod, Log, Conflict, Security
    public Dictionary<string, string> Data { get; set; }
    public string Language { get; set; }
}

// AI 响应
public class AIResponse
{
    public bool Success { get; set; }
    public string Content { get; set; }
    public string? Error { get; set; }
    public int TokensUsed { get; set; }
}
```

#### 枚举
```csharp
public enum AIProviderType
{
    None = 0,
    OpenAI = 1,
    Claude = 2,
    LocalLLM = 3,
    Custom = 99
}

public enum AIContextType
{
    Translation,
    ModAnalysis,
    LogExplanation,
    ChangelogSummary,
    ConflictExplanation,
    SecurityCheck
}
```

#### 接口
```csharp
public interface IAIProviderConfigRepository
{
    Task<AIProviderConfig?> GetActiveProviderAsync(CancellationToken cancellationToken = default);
    Task<List<AIProviderConfig>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<AIProviderConfig?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task AddAsync(AIProviderConfig config, CancellationToken cancellationToken = default);
    Task UpdateAsync(AIProviderConfig config, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public interface IAIService
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<AIResponse> TranslateAsync(string text, string targetLanguage, CancellationToken cancellationToken = default);
    Task<AIResponse> AnalyzeModAsync(string modId, CancellationToken cancellationToken = default);
    Task<AIResponse> ExplainLogAsync(string logContent, CancellationToken cancellationToken = default);
    Task<AIResponse> SummarizeChangelogAsync(string changelog, CancellationToken cancellationToken = default);
    Task<AIResponse> ExplainConflictAsync(List<string> modIds, CancellationToken cancellationToken = default);
    Task<AIResponse> CheckSecurityAsync(string modId, CancellationToken cancellationToken = default);
}

public interface IAIProvider
{
    Task<AIResponse> SendRequestAsync(AIAnalysisContext context, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}
```

### 2. Infrastructure Layer (`HD2Mimnt.Infrastructure`)

#### AIProviderConfigRepository
- 实现 IAIProviderConfigRepository
- 使用原生 SQLite
- 支持多个提供商配置
- 只有一个 active provider

#### 数据库 Schema
```sql
CREATE TABLE IF NOT EXISTS AIProviderConfigs (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Type INTEGER NOT NULL,
    ApiEndpoint TEXT NOT NULL,
    ApiKey TEXT,
    Model TEXT,
    IsEnabled INTEGER NOT NULL DEFAULT 0,
    MaxTokens INTEGER NOT NULL DEFAULT 4000,
    Temperature REAL NOT NULL DEFAULT 0.7,
    CreatedAt TEXT NOT NULL,
    ModifiedAt TEXT
);
CREATE INDEX IF NOT EXISTS idx_aiproviderconfigs_enabled ON AIProviderConfigs(IsEnabled);
```

### 3. Application Layer (`HD2Mimnt.Application`)

#### AIService
主要服务实现，协调 AI 功能

#### OpenAIProvider
```csharp
public class OpenAIProvider : IAIProvider
{
    private readonly AIProviderConfig _config;
    private readonly HttpClient _httpClient;
    
    public async Task<AIResponse> SendRequestAsync(AIAnalysisContext context, CancellationToken cancellationToken = default)
    {
        // 构建 prompt
        var prompt = BuildPrompt(context);
        
        // 调用 OpenAI API
        var response = await CallOpenAIAsync(prompt, cancellationToken);
        
        return response;
    }
}
```

#### ClaudeProvider
```csharp
public class ClaudeProvider : IAIProvider
{
    // 类似 OpenAIProvider
}
```

#### LocalLLMProvider
```csharp
public class LocalLLMProvider : IAIProvider
{
    // 支持本地 LLM（ollama, llama.cpp 等）
}
```

### 4. UI Layer (`HD2Mimnt.UI`)

#### ViewModels
```csharp
public partial class AISettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<AIProviderConfigItemViewModel> _providers;
    
    [ObservableProperty]
    private AIProviderConfigItemViewModel? _activeProvider;
    
    [RelayCommand]
    private async Task TestConnectionAsync(AIProviderConfigItemViewModel provider);
    
    [RelayCommand]
    private async Task SetActiveProviderAsync(AIProviderConfigItemViewModel provider);
}

public partial class AIAssistantViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isAIAvailable;
    
    [ObservableProperty]
    private string _inputText = string.Empty;
    
    [ObservableProperty]
    private string _outputText = string.Empty;
    
    [ObservableProperty]
    private bool _isProcessing;
    
    [RelayCommand]
    private async Task TranslateAsync(string targetLanguage);
    
    [RelayCommand]
    private async Task AnalyzeModAsync(string modId);
    
    [RelayCommand]
    private async Task ExplainLogAsync();
}
```

## 实现步骤

### Step 1: Core 层实体和接口
- [ ] 创建 AIProviderConfig 实体
- [ ] 创建 AIAnalysisContext 实体
- [ ] 创建 AIResponse 实体
- [ ] 定义 AIProviderType 枚举
- [ ] 定义 AIContextType 枚举
- [ ] 定义 IAIProviderConfigRepository 接口
- [ ] 定义 IAIService 接口
- [ ] 定义 IAIProvider 接口

### Step 2: Infrastructure 层数据访问
- [ ] 实现 AIProviderConfigRepository
- [ ] 在 HD2MimntDbContext 中添加 AIProviderConfigs 表
- [ ] 编写 AIProviderConfigRepositoryTests

### Step 3: Application 层业务逻辑
- [ ] 实现 AIService
- [ ] 实现 OpenAIProvider
- [ ] 实现 ClaudeProvider
- [ ] 实现 LocalLLMProvider
- [ ] 实现 Prompt 构建逻辑
- [ ] 编写 AIServiceTests

### Step 4: UI 层
- [ ] 创建 AISettingsViewModel
- [ ] 创建 AIAssistantViewModel
- [ ] 创建 AISettingsView.axaml
- [ ] 创建 AIAssistantView.axaml
- [ ] 集成到主界面

### Step 5: 集成测试
- [ ] 测试 OpenAI 连接
- [ ] 测试 Claude 连接
- [ ] 测试本地 LLM 连接
- [ ] 测试所有 AI 功能
- [ ] 测试无 AI 时的降级行为

## Prompt 设计

### Translation Prompt
```
Translate the following text to {targetLanguage}.
Keep the technical terms unchanged.
Preserve markdown formatting.

Text:
{text}
```

### Mod Analysis Prompt
```
Analyze the following Helldivers 2 mod:

Name: {modName}
Files: {fileList}
Structure: {modStructure}

Explain:
1. What this mod does
2. Which game aspects it modifies
3. Potential compatibility concerns
4. Installation requirements
```

### Log Explanation Prompt
```
Explain the following error log from a Helldivers 2 mod manager:

{logContent}

Provide:
1. What went wrong
2. Possible causes
3. Suggested solutions
```

### Security Check Prompt
```
Analyze the security aspects of this Helldivers 2 mod:

Files: {fileList}
Scripts: {scriptList}

Check for:
1. Suspicious file operations
2. Network connections
3. System modifications
4. Unusual permissions

Provide a safety assessment.
```

## 降级策略

当 AI 不可用时：
1. UI 显示 "AI 功能未配置" 提示
2. 相关按钮禁用
3. 提供手动文档链接
4. 核心功能不受影响

## 安全考虑

1. **API Key 存储**
   - 使用 Windows DPAPI 加密
   - 不明文存储在数据库
   - 提供导入/导出功能

2. **数据隐私**
   - 不发送敏感文件内容
   - 只发送必要的元数据
   - 提供数据发送前预览

3. **速率限制**
   - 实现请求队列
   - 避免 API 滥用
   - 显示令牌使用统计

## 测试计划

### 单元测试
- AIProviderConfigRepository (CRUD)
- AIService (各功能)
- Provider 实现 (模拟 API)

### 集成测试
- 真实 OpenAI API 调用
- 真实 Claude API 调用
- 本地 LLM 调用

### UI 测试
- 配置管理
- 功能触发
- 错误处理
- 降级行为

## 参考资料

### OpenAI API
- Endpoint: https://api.openai.com/v1/chat/completions
- Model: gpt-4-turbo, gpt-3.5-turbo

### Claude API
- Endpoint: https://api.anthropic.com/v1/messages
- Model: claude-3-opus, claude-3-sonnet

### Local LLM
- Ollama: http://localhost:11434/api/generate
- llama.cpp server: http://localhost:8080/completion

## 估算工作量

- Core 层: 2-3 小时
- Infrastructure 层: 3-4 小时
- Application 层: 6-8 小时
- UI 层: 4-6 小时
- 测试: 4-5 小时

**总计: 约 19-26 小时**

## 下一步行动

开始实现 Phase 19 Step 1: Core 层实体和接口。
