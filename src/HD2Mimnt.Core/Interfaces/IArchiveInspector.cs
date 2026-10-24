using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Interface for inspecting archive structure without full extraction
/// </summary>
public interface IArchiveInspector
{
    /// <summary>
    /// Inspects an archive and builds its tree structure
    /// </summary>
    /// <param name="archivePath">Path to the archive file</param>
    /// <param name="maxDepth">Maximum nesting depth to inspect</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Archive tree structure</returns>
    Task<ArchiveTree> InspectAsync(string archivePath, int maxDepth = 5, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines if a file is an archive
    /// </summary>
    /// <param name="filePath">Path to check</param>
    /// <returns>True if the file is a recognized archive format</returns>
    bool IsArchive(string filePath);

    /// <summary>
    /// Gets the archive type from file path or content
    /// </summary>
    /// <param name="filePath">Path to the archive</param>
    /// <returns>Archive type</returns>
    Task<Enums.ArchiveType> GetArchiveTypeAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists entries in an archive without extracting
    /// </summary>
    /// <param name="archivePath">Path to the archive</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of entry paths</returns>
    Task<List<string>> ListEntriesAsync(string archivePath, CancellationToken cancellationToken = default);
}
