using System.Text.Json.Serialization;

namespace HD2Mimnt.Infrastructure.Hd2IAS.Companion;

/// <summary>上游 external-runtime.json（HD2ExternalRuntime1）中的一个文件行。字段名与上游 JSON 对齐。</summary>
public sealed class IasRuntimeFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("preserve_existing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreserveExisting { get; set; }
}

/// <summary>生成器产出的 armor 组件包清单（HD2ExternalRuntime1）。</summary>
public sealed class IasRuntimePackage
{
    public const string Schema = "HD2ExternalRuntime1";

    [JsonPropertyName("schema")] public string SchemaField { get; set; } = "";
    [JsonPropertyName("component")] public string Component { get; set; } = "";
    [JsonPropertyName("metadata_only")] public bool MetadataOnly { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("host")] public Dictionary<string, string> Host { get; set; } = [];
    [JsonPropertyName("files")] public List<IasRuntimeFile> Files { get; set; } = [];
    [JsonPropertyName("bootstrap")] public List<IasRuntimeFile> Bootstrap { get; set; } = [];
    [JsonPropertyName("game_sources")] public List<IasRuntimeFile>? GameSources { get; set; }
}

/// <summary>事务记录中的一个文件变更（HD2ExternalTransaction1.files 行）。</summary>
public sealed class IasTransactionChange
{
    [JsonPropertyName("scope")] public string Scope { get; set; } = "runtime";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("before")] public string? Before { get; set; }
    [JsonPropertyName("after")] public string? After { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("permanent_delete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PermanentDelete { get; set; }
    /// <summary>仅 receipt 重写变更使用：deployment.json 的新内容（事务 JSON 中序列化为 base64，与上游一致）。</summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Content { get; set; }
    [JsonPropertyName("before_time_utc")] public long BeforeTimeUtc { get; set; }
    [JsonPropertyName("after_time_utc")] public long AfterTimeUtc { get; set; }

    [JsonIgnore]
    public bool IsDelete => After == null;
}

/// <summary>与上游 HD2ExternalTransaction1 兼容的事务记录：上游“恢复中断的安装”可直接识别。</summary>
public sealed class IasCompanionTransaction
{
    public const string Schema = "HD2ExternalTransaction1";
    public const string TransactionFileName = "external-runtime-transaction.json";

    [JsonPropertyName("schema")] public string SchemaField { get; set; } = Schema;
    [JsonPropertyName("status")] public string Status { get; set; } = "prepared";
    [JsonPropertyName("game")] public string Game { get; set; } = "";
    [JsonPropertyName("runtime_root")] public string RuntimeRoot { get; set; } = "";
    [JsonPropertyName("created_local")] public string CreatedLocal { get; set; } = "";
    [JsonPropertyName("finished_local")] public string? FinishedLocal { get; set; }
    [JsonPropertyName("files")] public List<IasTransactionChange> Files { get; set; } = [];
    [JsonPropertyName("resolved")] public Dictionary<string, string> Resolved { get; set; } = [];
    [JsonPropertyName("roster_before")] public List<string> RosterBefore { get; set; } = [];
    [JsonPropertyName("roster_after")] public List<string> RosterAfter { get; set; } = [];
    [JsonPropertyName("runtime_only")] public bool RuntimeOnly { get; set; } = true;
}
