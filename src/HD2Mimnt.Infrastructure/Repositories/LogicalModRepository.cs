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
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        // 主行、组件、部署文件、包同库同事务写入，避免中途失败留下残缺聚合
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
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
            }

            // 按外键依赖顺序写入：主行 → 包 → 组件 → 部署文件（DeployableFiles.PackageId 引用 PackageSources）
            foreach (var package in logicalMod.Packages)
            {
                await AddPackageAsync(connection, transaction, logicalMod.Id, package, cancellationToken);
            }

            foreach (var component in logicalMod.Components)
            {
                await AddComponentAsync(connection, transaction, logicalMod.Id, component, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return logicalMod;
    }

    public async Task UpdateAsync(LogicalMod logicalMod, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

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
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LogicalMods WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var logicalMod = MapToLogicalMod(reader);
            await reader.CloseAsync();

            await LoadAggregatesAsync(connection, [logicalMod], cancellationToken);

            return logicalMod;
        }

        return null;
    }

    public async Task<List<LogicalMod>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        var logicalMods = new List<LogicalMod>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LogicalMods ORDER BY Name";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            logicalMods.Add(MapToLogicalMod(reader));
        }

        await LoadAggregatesAsync(connection, logicalMods, cancellationToken);

        return logicalMods;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        try
        {
            // Delete in order: deployable files, components, packages, logical mod
            foreach (var sql in new[]
            {
                "DELETE FROM DeployableFiles WHERE ComponentId IN (SELECT Id FROM LogicalModComponents WHERE LogicalModId = @Id)",
                "DELETE FROM LogicalModComponents WHERE LogicalModId = @Id",
                "DELETE FROM PackageSources WHERE LogicalModId = @Id",
                "DELETE FROM LogicalMods WHERE Id = @Id"
            })
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("@Id", id.ToString());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<List<LogicalMod>> SearchByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        using var connection = await _dbContext.OpenConnectionAsync(cancellationToken);
        var logicalMods = new List<LogicalMod>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM LogicalMods WHERE Name LIKE @Name ORDER BY Name";
        command.Parameters.AddWithValue("@Name", $"%{name}%");

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            logicalMods.Add(MapToLogicalMod(reader));
        }

        await LoadAggregatesAsync(connection, logicalMods, cancellationToken);

        return logicalMods;
    }

    /// <summary>
    /// 批量装载组件、包与部署文件并按归属分配，替代逐 mod 的 N+1 查询。
    /// </summary>
    private static async Task LoadAggregatesAsync(SqliteConnection connection, List<LogicalMod> logicalMods, CancellationToken cancellationToken)
    {
        if (logicalMods.Count == 0)
        {
            return;
        }

        var componentsByMod = new Dictionary<Guid, List<LogicalModComponent>>();
        var packagesByMod = new Dictionary<Guid, List<PackageSource>>();
        var allComponents = new List<LogicalModComponent>();

        foreach (var chunk in logicalMods.Select(m => m.Id).Distinct().Chunk(SqliteBatching.MaxParametersPerChunk))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $@"
                SELECT * FROM LogicalModComponents
                WHERE LogicalModId IN ({SqliteBatching.BindInParameters(command, chunk, id => id.ToString())})";

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var modId = Guid.Parse(reader.GetString(reader.GetOrdinal("LogicalModId")));
                var component = MapToComponent(reader);
                allComponents.Add(component);
                if (!componentsByMod.TryGetValue(modId, out var list))
                {
                    componentsByMod[modId] = list = new List<LogicalModComponent>();
                }
                list.Add(component);
            }
        }

        foreach (var chunk in logicalMods.Select(m => m.Id).Distinct().Chunk(SqliteBatching.MaxParametersPerChunk))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $@"
                SELECT * FROM PackageSources
                WHERE LogicalModId IN ({SqliteBatching.BindInParameters(command, chunk, id => id.ToString())})";

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var modId = Guid.Parse(reader.GetString(reader.GetOrdinal("LogicalModId")));
                var package = MapToPackage(reader);
                if (!packagesByMod.TryGetValue(modId, out var list))
                {
                    packagesByMod[modId] = list = new List<PackageSource>();
                }
                list.Add(package);
            }
        }

        var filesByComponent = new Dictionary<Guid, List<DeployableFile>>();
        foreach (var chunk in allComponents.Select(c => c.Id).Distinct().Chunk(SqliteBatching.MaxParametersPerChunk))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $@"
                SELECT * FROM DeployableFiles
                WHERE ComponentId IN ({SqliteBatching.BindInParameters(command, chunk, id => id.ToString())})";

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var componentId = Guid.Parse(reader.GetString(reader.GetOrdinal("ComponentId")));
                var file = MapToDeployableFile(reader);
                if (!filesByComponent.TryGetValue(componentId, out var list))
                {
                    filesByComponent[componentId] = list = new List<DeployableFile>();
                }
                list.Add(file);
            }
        }

        foreach (var component in allComponents)
        {
            if (filesByComponent.TryGetValue(component.Id, out var files))
            {
                component.DeployableFiles = files;
            }
        }

        foreach (var mod in logicalMods)
        {
            if (componentsByMod.TryGetValue(mod.Id, out var components))
            {
                mod.Components = components;
            }
            if (packagesByMod.TryGetValue(mod.Id, out var packages))
            {
                mod.Packages = packages;
            }
        }
    }

    private static async Task AddComponentAsync(SqliteConnection connection, SqliteTransaction transaction, Guid logicalModId, LogicalModComponent component, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
            await AddDeployableFileAsync(connection, transaction, component.Id, file, cancellationToken);
        }
    }

    private static async Task AddPackageAsync(SqliteConnection connection, SqliteTransaction transaction, Guid logicalModId, PackageSource package, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static async Task AddDeployableFileAsync(SqliteConnection connection, SqliteTransaction transaction, Guid componentId, DeployableFile file, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static LogicalMod MapToLogicalMod(SqliteDataReader reader)
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

    private static LogicalModComponent MapToComponent(SqliteDataReader reader)
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

    private static PackageSource MapToPackage(SqliteDataReader reader)
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

    private static DeployableFile MapToDeployableFile(SqliteDataReader reader)
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
