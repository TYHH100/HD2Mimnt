using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Deployment transaction record
/// Tracks all changes made during a deployment operation
/// </summary>
public class DeploymentTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Transaction session ID (same as deployment session)
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Profile ID (if deploying from a profile)
    /// </summary>
    public Guid? ProfileId { get; set; }

    /// <summary>
    /// Logical Mod ID (if deploying a LogicalMod)
    /// </summary>
    public Guid? LogicalModId { get; set; }

    /// <summary>
    /// Traditional Mod ID (if deploying a traditional Mod)
    /// </summary>
    public Guid? ModId { get; set; }

    /// <summary>
    /// Component ID (if deploying a specific component)
    /// </summary>
    public Guid? ComponentId { get; set; }

    /// <summary>
    /// Operation type: Deploy, Undeploy, Update, Switch
    /// </summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Deployment method used
    /// </summary>
    public DeploymentMethod Method { get; set; }

    /// <summary>
    /// Transaction status
    /// </summary>
    public DeploymentStatus Status { get; set; }

    /// <summary>
    /// Files added during this transaction
    /// </summary>
    public List<string> AddedFiles { get; set; } = new();

    /// <summary>
    /// Files updated during this transaction
    /// </summary>
    public List<string> UpdatedFiles { get; set; } = new();

    /// <summary>
    /// Files removed during this transaction
    /// </summary>
    public List<string> RemovedFiles { get; set; } = new();

    /// <summary>
    /// Files skipped (unchanged)
    /// </summary>
    public List<string> SkippedFiles { get; set; } = new();

    /// <summary>
    /// File hashes for verification
    /// Key: file path, Value: hash
    /// </summary>
    public Dictionary<string, string> FileHashes { get; set; } = new();

    /// <summary>
    /// Conflicts encountered
    /// </summary>
    public List<string> Conflicts { get; set; } = new();

    /// <summary>
    /// When this transaction started
    /// </summary>
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this transaction completed
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}
