using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.Repositories;

/// <summary>
/// Repository for logical mod management
/// </summary>
public class LogicalModRepository : ILogicalModRepository
{
    private readonly HD2MimntDbContext _dbContext;

    public LogicalModRepository(HD2MimntDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<LogicalMod> AddAsync(LogicalMod logicalMod, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO LogicalMods (Id, Name, Version, Author, Description, ManifestVersion, CreatedAt, ModifiedAt, TotalSize, ThumbnailPath, Tags, Metadata)
            VALUES (@Id, @Name, @Version, @Author, @Description, @ManifestVersion, @CreatedAt, @ModifiedAt, @TotalSize, @ThumbnailPath, @Tags, @Metadata)";

        command.Parameters.AddWithValue("@Id", logicalMod.Id.ToString());
        command.Parameters.AddWithValue("@Name", logicalMod.Name);
        command.Parameters.AddWithValue("@Version", logicalMod.Version);
        command.Parameters.AddWithValue("@Author", logicalMod.Author);
        command.Parameters.AddWithValue("@Description", logicalMod.Description);
        command.Parameters.AddWithValue("@ManifestVersion", (int)logicalMod.ManifestVersion);
        command.Parameters.AddWithValue("@CreatedAt", logicalMod.CreatedAt);
        command.Parameters.AddWithValue("@ModifiedAt", logicalMod.ModifiedAt);
        command.Parameters.AddWithValue("@TotalSize", logicalMod.TotalSize);
        command.Parameters.AddWithValue("@ThumbnailPath", (object?)logicalMod.ThumbnailPath ?? DBNull.Value);
        command.Parameters.AddWithValue("@Tags", JsonSerializer.Serialize(logicalMod.Tags));
        command.Parameters.AddWithValue("@Metadata", JsonSerializer.Serialize(logicalMod.Metadata));

        await command.ExecuteNonQueryAsync(cancellationToken);

        // Insert components
        foreach (var component in logicalMod.Components)
        {
            await AddComponentAsync(logicalMod.Id, component, cancellationToken);
        }

        // Insert packages
        foreach (var package in logicalMod.Packages)
        {
            await AddPackageAsync(logicalMod.Id, package, cancellationToken);
        }

        return logicalMod;
    }

    public async Task UpdateAsync(LogicalMod logicalMod, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE LogicalMods
            SET Name = @Name, Version = @Version, Author = @Author, Description = @Description,
                ModifiedAt = @ModifiedAt, TotalSize = @TotalSize, ThumbnailPath = @ThumbnailPath,
                Tags = @Tags, Metadata = @Metadata
            WHERE Id = @Id";

        command.Parameters.AddWithValue("@Id", logicalMod.Id.ToString());
        command.Parameters.AddWithValue("@Name", logicalMod.Name);
        command.Parameters.AddWithValue("@Version", logicalMod.Version);
        command.Parameters.AddWithValue("@Author", logicalMod.Author);
        command.Parameters.AddWithValue("@Description", logicalMod.Description);
        command.Parameters.AddWithValue("@ModifiedAt", DateTime.UtcNow);
        command.Parameters.AddWithValue("@TotalSize", logicalMod.TotalSize);
        command.Parameters.AddWithValue("@ThumbnailPath", (object?)logicalMod.ThumbnailPath ?? DBNull.Value);
        command.Parameters.AddWithValue("@Tags", JsonSerializer.Serialize(logicalMod.Tags));
        command.Parameters.AddWithValue("@Metadata", JsonSerializer.Serialize(logicalMod.Metadata));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LogicalMod?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LogicalMods WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var logicalMod = MapToLogicalMod(reader);
            await reader.CloseAsync();

            // Load components
            logicalMod.Components = await GetComponentsAsync(logicalMod.Id, cancellationToken);

            // Load packages
            logicalMod.Packages = await GetPackagesAsync(logicalMod.Id, cancellationToken);

            return logicalMod;
        }

        return null;
    }

    public async Task<List<LogicalMod>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        var logicalMods = new List<LogicalMod>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LogicalMods ORDER BY Name";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            logicalMods.Add(MapToLogicalMod(reader));
        }

        // Load components and packages for each
        foreach (var mod in logicalMods)
        {
            mod.Components = await GetComponentsAsync(mod.Id, cancellationToken);
            mod.Packages = await GetPackagesAsync(mod.Id, cancellationToken);
        }

        return logicalMods;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        // Delete in order: deployable files, components, packages, logical mod
        using var deleteFilesCmd = connection.CreateCommand();
        deleteFilesCmd.CommandText = "DELETE FROM DeployableFiles WHERE ComponentId IN (SELECT Id FROM LogicalModComponents WHERE LogicalModId = @Id)";
        deleteFilesCmd.Parameters.AddWithValue("@Id", id.ToString());
        await deleteFilesCmd.ExecuteNonQueryAsync(cancellationToken);

        using var deleteComponentsCmd = connection.CreateCommand();
        deleteComponentsCmd.CommandText = "DELETE FROM LogicalModComponents WHERE LogicalModId = @Id";
        deleteComponentsCmd.Parameters.AddWithValue("@Id", id.ToString());
        await deleteComponentsCmd.ExecuteNonQueryAsync(cancellationToken);

        using var deletePackagesCmd = connection.CreateCommand();
        deletePackagesCmd.CommandText = "DELETE FROM PackageSources WHERE LogicalModId = @Id";
        deletePackagesCmd.Parameters.AddWithValue("@Id", id.ToString());
        await deletePackagesCmd.ExecuteNonQueryAsync(cancellationToken);

        using var deleteModCmd = connection.CreateCommand();
        deleteModCmd.CommandText = "DELETE FROM LogicalMods WHERE Id = @Id";
        deleteModCmd.Parameters.AddWithValue("@Id", id.ToString());
        await deleteModCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<LogicalMod>> SearchByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        var logicalMods = new List<LogicalMod>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LogicalMods WHERE Name LIKE @Name ORDER BY Name";
        command.Parameters.AddWithValue("@Name", $"%{name}%");

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            logicalMods.Add(MapToLogicalMod(reader));
        }

        foreach (var mod in logicalMods)
        {
            mod.Components = await GetComponentsAsync(mod.Id, cancellationToken);
            mod.Packages = await GetPackagesAsync(mod.Id, cancellationToken);
        }

        return logicalMods;
    }

    private async Task AddComponentAsync(Guid logicalModId, LogicalModComponent component, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO LogicalModComponents (Id, LogicalModId, ComponentId, Type, Name, SourcePackageId, Size, Enabled, Dependencies, Metadata)
            VALUES (@Id, @LogicalModId, @ComponentId, @Type, @Name, @SourcePackageId, @Size, @Enabled, @Dependencies, @Metadata)";

        command.Parameters.AddWithValue("@Id", component.Id.ToString());
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());
        command.Parameters.AddWithValue("@ComponentId", component.ComponentId);
        command.Parameters.AddWithValue("@Type", (int)component.Type);
        command.Parameters.AddWithValue("@Name", component.Name);
        command.Parameters.AddWithValue("@SourcePackageId", component.SourcePackageId.ToString());
        command.Parameters.AddWithValue("@Size", component.Size);
        command.Parameters.AddWithValue("@Enabled", component.Enabled);
        command.Parameters.AddWithValue("@Dependencies", JsonSerializer.Serialize(component.Dependencies));
        command.Parameters.AddWithValue("@Metadata", JsonSerializer.Serialize(component.Metadata));

        await command.ExecuteNonQueryAsync(cancellationToken);

        // Insert deployable files
        foreach (var file in component.DeployableFiles)
        {
            await AddDeployableFileAsync(component.Id, file, cancellationToken);
        }
    }

    private async Task AddPackageAsync(Guid logicalModId, PackageSource package, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO PackageSources (Id, LogicalModId, Name, PhysicalPath, ArchiveType, Size, Hash, LastModified, ParentPackageId, Depth, PathInParent)
            VALUES (@Id, @LogicalModId, @Name, @PhysicalPath, @ArchiveType, @Size, @Hash, @LastModified, @ParentPackageId, @Depth, @PathInParent)";

        command.Parameters.AddWithValue("@Id", package.Id.ToString());
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());
        command.Parameters.AddWithValue("@Name", package.Name);
        command.Parameters.AddWithValue("@PhysicalPath", package.PhysicalPath);
        command.Parameters.AddWithValue("@ArchiveType", (int)package.ArchiveType);
        command.Parameters.AddWithValue("@Size", package.Size);
        command.Parameters.AddWithValue("@Hash", package.Hash);
        command.Parameters.AddWithValue("@LastModified", package.LastModified);
        command.Parameters.AddWithValue("@ParentPackageId", (object?)package.ParentPackageId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("@Depth", package.Depth);
        command.Parameters.AddWithValue("@PathInParent", (object?)package.PathInParent ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task AddDeployableFileAsync(Guid componentId, DeployableFile file, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO DeployableFiles (Id, ComponentId, SourcePath, TargetPath, Size, Hash, PackageId, IsShared, Metadata)
            VALUES (@Id, @ComponentId, @SourcePath, @TargetPath, @Size, @Hash, @PackageId, @IsShared, @Metadata)";

        command.Parameters.AddWithValue("@Id", file.Id.ToString());
        command.Parameters.AddWithValue("@ComponentId", componentId.ToString());
        command.Parameters.AddWithValue("@SourcePath", file.SourcePath);
        command.Parameters.AddWithValue("@TargetPath", file.TargetPath);
        command.Parameters.AddWithValue("@Size", file.Size);
        command.Parameters.AddWithValue("@Hash", file.Hash);
        command.Parameters.AddWithValue("@PackageId", file.PackageId.ToString());
        command.Parameters.AddWithValue("@IsShared", file.IsShared);
        command.Parameters.AddWithValue("@Metadata", JsonSerializer.Serialize(file.Metadata));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<LogicalModComponent>> GetComponentsAsync(Guid logicalModId, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        var components = new List<LogicalModComponent>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LogicalModComponents WHERE LogicalModId = @LogicalModId";
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var component = MapToComponent(reader);
            components.Add(component);
        }

        // Load deployable files for each component
        foreach (var component in components)
        {
            component.DeployableFiles = await GetDeployableFilesAsync(component.Id, cancellationToken);
        }

        return components;
    }

    private async Task<List<PackageSource>> GetPackagesAsync(Guid logicalModId, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        var packages = new List<PackageSource>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PackageSources WHERE LogicalModId = @LogicalModId";
        command.Parameters.AddWithValue("@LogicalModId", logicalModId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            packages.Add(MapToPackage(reader));
        }

        return packages;
    }

    private async Task<List<DeployableFile>> GetDeployableFilesAsync(Guid componentId, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.GetConnectionAsync(cancellationToken);
        var files = new List<DeployableFile>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM DeployableFiles WHERE ComponentId = @ComponentId";
        command.Parameters.AddWithValue("@ComponentId", componentId.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(MapToDeployableFile(reader));
        }

        return files;
    }

    private LogicalMod MapToLogicalMod(SqliteDataReader reader)
    {
        return new LogicalMod
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Version = reader.GetString(reader.GetOrdinal("Version")),
            Author = reader.GetString(reader.GetOrdinal("Author")),
            Description = reader.GetString(reader.GetOrdinal("Description")),
            ManifestVersion = (Core.Enums.LogicalModVersion)reader.GetInt32(reader.GetOrdinal("ManifestVersion")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            ModifiedAt = reader.GetDateTime(reader.GetOrdinal("ModifiedAt")),
            TotalSize = reader.GetInt64(reader.GetOrdinal("TotalSize")),
            ThumbnailPath = reader.IsDBNull(reader.GetOrdinal("ThumbnailPath")) ? null : reader.GetString(reader.GetOrdinal("ThumbnailPath")),
            Tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("Tags"))) ?? new(),
            Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("Metadata"))) ?? new()
        };
    }

    private LogicalModComponent MapToComponent(SqliteDataReader reader)
    {
        return new LogicalModComponent
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            ComponentId = reader.GetString(reader.GetOrdinal("ComponentId")),
            Type = (Core.Enums.ComponentType)reader.GetInt32(reader.GetOrdinal("Type")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            SourcePackageId = Guid.Parse(reader.GetString(reader.GetOrdinal("SourcePackageId"))),
            Size = reader.GetInt64(reader.GetOrdinal("Size")),
            Enabled = reader.GetBoolean(reader.GetOrdinal("Enabled")),
            Dependencies = JsonSerializer.Deserialize<List<Guid>>(reader.GetString(reader.GetOrdinal("Dependencies"))) ?? new(),
            Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("Metadata"))) ?? new()
        };
    }

    private PackageSource MapToPackage(SqliteDataReader reader)
    {
        return new PackageSource
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            PhysicalPath = reader.GetString(reader.GetOrdinal("PhysicalPath")),
            ArchiveType = (Core.Enums.ArchiveType)reader.GetInt32(reader.GetOrdinal("ArchiveType")),
            Size = reader.GetInt64(reader.GetOrdinal("Size")),
            Hash = reader.GetString(reader.GetOrdinal("Hash")),
            LastModified = reader.GetDateTime(reader.GetOrdinal("LastModified")),
            ParentPackageId = reader.IsDBNull(reader.GetOrdinal("ParentPackageId")) ? null : Guid.Parse(reader.GetString(reader.GetOrdinal("ParentPackageId"))),
            Depth = reader.GetInt32(reader.GetOrdinal("Depth")),
            PathInParent = reader.IsDBNull(reader.GetOrdinal("PathInParent")) ? null : reader.GetString(reader.GetOrdinal("PathInParent"))
        };
    }

    private DeployableFile MapToDeployableFile(SqliteDataReader reader)
    {
        return new DeployableFile
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
            SourcePath = reader.GetString(reader.GetOrdinal("SourcePath")),
            TargetPath = reader.GetString(reader.GetOrdinal("TargetPath")),
            Size = reader.GetInt64(reader.GetOrdinal("Size")),
            Hash = reader.GetString(reader.GetOrdinal("Hash")),
            PackageId = Guid.Parse(reader.GetString(reader.GetOrdinal("PackageId"))),
            IsShared = reader.GetBoolean(reader.GetOrdinal("IsShared")),
            Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("Metadata"))) ?? new()
        };
    }
}
