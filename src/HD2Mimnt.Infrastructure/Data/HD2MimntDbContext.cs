using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Data;

/// <summary>
/// SQLite 数据库上下文
/// 使用 WAL 模式以提高并发性能
/// </summary>
public class HD2MimntDbContext : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private int _disposed;

    public string DatabasePath { get; }

    public HD2MimntDbContext(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 5
        }.ToString();
    }

    /// <summary>调用方独占连接并负责释放；连接不跨 Repository 操作共享。</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _initializationGate.WaitAsync(cancellationToken);
        try { await InitializeSchemaAsync(cancellationToken); }
        finally { _initializationGate.Release(); }
    }

    /// <summary>
    /// 以在线备份方式把外部数据库文件的内容替换导入当前库（事务替换，不关闭其它操作的连接）。
    /// </summary>
    public async Task ImportFromAsync(string sourceDatabasePath, CancellationToken cancellationToken = default)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourceDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await source.OpenAsync(cancellationToken);
        using var target = await OpenConnectionAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(target);
    }

    private async Task InitializeSchemaAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenConnectionAsync(cancellationToken);
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL;";
            await journal.ExecuteNonQueryAsync(cancellationToken);
        }

        var sql = @"
            CREATE TABLE IF NOT EXISTS Mods (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Version TEXT NOT NULL,
                Author TEXT,
                Description TEXT,
                Path TEXT NOT NULL,
                Category INTEGER NOT NULL DEFAULT 0,
                Tags TEXT,
                ResourceTypeIds TEXT,
                BslRole INTEGER,
                BslDetectionVersion INTEGER NOT NULL DEFAULT 0,
                WorkflowType TEXT,
                ImportedAt TEXT NOT NULL,
                ModifiedAt TEXT NOT NULL,
                Size INTEGER,
                Hash TEXT,
                ThumbnailPath TEXT,
                Source INTEGER NOT NULL DEFAULT 0,
                Status INTEGER NOT NULL DEFAULT 0,
                ManifestJson TEXT,
                ContainsScripts INTEGER NOT NULL DEFAULT 0,
                NexusModId INTEGER
            );

            CREATE INDEX IF NOT EXISTS idx_mods_name ON Mods(Name);
            CREATE INDEX IF NOT EXISTS idx_mods_category ON Mods(Category);

            CREATE TABLE IF NOT EXISTS Profiles (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE,
                Description TEXT,
                CreatedAt TEXT NOT NULL,
                ModifiedAt TEXT NOT NULL,
                IsActive INTEGER NOT NULL,
                IsDefault INTEGER NOT NULL DEFAULT 0,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                IsLuaGroupCollapsed INTEGER NOT NULL DEFAULT 0,
                LuaSeparatorColor TEXT NOT NULL DEFAULT '',
                IsLibraryLayout INTEGER NOT NULL DEFAULT 0,
                DefaultMigrationStateJson TEXT NOT NULL DEFAULT '{}'
            );

            CREATE TABLE IF NOT EXISTS ProfileMods (
                ProfileId TEXT NOT NULL,
                ModId TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                [Order] INTEGER NOT NULL,
                OptionsJson TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (ProfileId, ModId),
                FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE,
                FOREIGN KEY (ModId) REFERENCES Mods(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_profilemods_order ON ProfileMods([Order]);

            CREATE TABLE IF NOT EXISTS ProfileSeparators (
                Id TEXT PRIMARY KEY,
                ProfileId TEXT NOT NULL,
                Name TEXT NOT NULL,
                [Order] INTEGER NOT NULL,
                IsCollapsed INTEGER NOT NULL DEFAULT 0,
                MemberCount INTEGER,
                Color TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_profile_separators_profile_order ON ProfileSeparators(ProfileId, [Order]);

            CREATE TABLE IF NOT EXISTS ProfileLogicalMods (
                ProfileId TEXT NOT NULL,
                LogicalModId TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                [Order] INTEGER NOT NULL,
                EnabledComponentIds TEXT NOT NULL DEFAULT '[]',
                ComponentConfigurations TEXT NOT NULL DEFAULT '{}',
                PRIMARY KEY (ProfileId, LogicalModId),
                FOREIGN KEY (ProfileId) REFERENCES Profiles(Id) ON DELETE CASCADE,
                FOREIGN KEY (LogicalModId) REFERENCES LogicalMods(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_profilelogicalmods_order ON ProfileLogicalMods([Order]);
            CREATE INDEX IF NOT EXISTS idx_profilelogicalmods_enabled ON ProfileLogicalMods(Enabled);

            CREATE TABLE IF NOT EXISTS Tasks (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Type TEXT NOT NULL,
                Status INTEGER NOT NULL,
                Progress REAL,
                Stage TEXT,
                CurrentItem TEXT,
                Message TEXT,
                StartTime TEXT NOT NULL,
                EndTime TEXT,
                Errors TEXT,
                Result TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_tasks_status ON Tasks(Status);
            CREATE INDEX IF NOT EXISTS idx_tasks_starttime ON Tasks(StartTime DESC);

            CREATE TABLE IF NOT EXISTS CrashDeploymentSnapshots (
                SessionId TEXT PRIMARY KEY,
                StartedUtc TEXT NOT NULL,
                KnownGood INTEGER NOT NULL DEFAULT 0,
                SnapshotJson TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS DeploymentRecords (
                Id TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                ProfileId TEXT NOT NULL,
                ModId TEXT NOT NULL,
                Workflow TEXT,
                DeploymentMethod TEXT,
                StartTime TEXT NOT NULL,
                EndTime TEXT,
                Status TEXT,
                OptionsSnapshotJson TEXT,
                DeploymentOrderIndex INTEGER,
                ModVersion TEXT,
                GamePath TEXT,
                DeploymentOrder TEXT,
                AppliedAt TEXT,
                FOREIGN KEY (ProfileId) REFERENCES Profiles(Id),
                FOREIGN KEY (ModId) REFERENCES Mods(Id)
            );

            CREATE INDEX IF NOT EXISTS idx_deployment_session ON DeploymentRecords(SessionId);
            CREATE INDEX IF NOT EXISTS idx_deployment_mod ON DeploymentRecords(ModId);

            CREATE TABLE IF NOT EXISTS DeploymentFiles (
                Id TEXT PRIMARY KEY,
                DeploymentId TEXT NOT NULL,
                ModId TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                TargetPath TEXT NOT NULL,
                Method TEXT,
                GamePath TEXT,
                SourceKey TEXT,
                SourceRelativePath TEXT,
                FOREIGN KEY (DeploymentId) REFERENCES DeploymentRecords(Id) ON DELETE CASCADE,
                FOREIGN KEY (ModId) REFERENCES Mods(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_deploymentfiles_deployment ON DeploymentFiles(DeploymentId);
            CREATE INDEX IF NOT EXISTS idx_deploymentfiles_mod ON DeploymentFiles(ModId);
            CREATE INDEX IF NOT EXISTS idx_deploymentfiles_target ON DeploymentFiles(TargetPath);

            CREATE TABLE IF NOT EXISTS PlatformCredentials (
                PlatformId TEXT PRIMARY KEY,
                ProtectedApiKey BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SettingsMigrations (
                Id TEXT PRIMARY KEY
            );

            CREATE TABLE IF NOT EXISTS PlatformCaches (
                Id TEXT PRIMARY KEY,
                PlatformId TEXT NOT NULL,
                PlatformModId TEXT NOT NULL,
                CachedData TEXT NOT NULL,
                CachedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                Etag TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_platformcaches_platform ON PlatformCaches(PlatformId, PlatformModId);
            CREATE INDEX IF NOT EXISTS idx_platformcaches_expiresat ON PlatformCaches(ExpiresAt);

            CREATE TABLE IF NOT EXISTS Plugins (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Version TEXT NOT NULL,
                Author TEXT,
                Description TEXT,
                ManifestPath TEXT NOT NULL,
                AssemblyPath TEXT NOT NULL,
                AssemblySha256 TEXT,
                InstalledAt TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                PermissionMode TEXT NOT NULL DEFAULT 'Sandbox'
            );

            CREATE INDEX IF NOT EXISTS idx_plugins_name ON Plugins(Name);
            CREATE INDEX IF NOT EXISTS idx_plugins_enabled ON Plugins(Enabled);

            CREATE TABLE IF NOT EXISTS PluginPermissions (
                Id TEXT PRIMARY KEY,
                PluginId TEXT NOT NULL,
                Permission TEXT NOT NULL,
                Granted INTEGER NOT NULL DEFAULT 0,
                GrantedAt TEXT,
                FOREIGN KEY (PluginId) REFERENCES Plugins(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_pluginpermissions_pluginid ON PluginPermissions(PluginId);

            CREATE TABLE IF NOT EXISTS PluginExecutionHistory (
                Id TEXT PRIMARY KEY,
                PluginId TEXT NOT NULL,
                Command TEXT NOT NULL,
                StartTime TEXT NOT NULL,
                EndTime TEXT,
                Status TEXT NOT NULL,
                Result TEXT,
                Error TEXT,
                FOREIGN KEY (PluginId) REFERENCES Plugins(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_pluginexecution_pluginid ON PluginExecutionHistory(PluginId);
            CREATE INDEX IF NOT EXISTS idx_pluginexecution_starttime ON PluginExecutionHistory(StartTime DESC);

            CREATE TABLE IF NOT EXISTS ExternalTools (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Description TEXT,
                ExecutablePath TEXT NOT NULL,
                Arguments TEXT,
                WorkingDirectory TEXT,
                Icon TEXT,
                SupportedExtensions TEXT,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                DisplayOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                ModifiedAt TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_externaltools_enabled ON ExternalTools(IsEnabled);
            CREATE INDEX IF NOT EXISTS idx_externaltools_displayorder ON ExternalTools(DisplayOrder);

            -- AI Provider Configs table
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

            -- FTS5 全文搜索索引（优化 SearchByName 性能）
            CREATE VIRTUAL TABLE IF NOT EXISTS ModsFTS USING fts5(
                Id UNINDEXED,
                Name,
                Description,
                Author,
                content='Mods',
                content_rowid='rowid'
            );

            -- FTS5 触发器：插入时同步
            CREATE TRIGGER IF NOT EXISTS mods_fts_insert AFTER INSERT ON Mods BEGIN
                INSERT INTO ModsFTS(rowid, Id, Name, Description, Author)
                VALUES (new.rowid, new.Id, new.Name, new.Description, new.Author);
            END;

            -- FTS5 触发器：更新时同步
            CREATE TRIGGER IF NOT EXISTS mods_fts_update AFTER UPDATE ON Mods BEGIN
                UPDATE ModsFTS SET Name=new.Name, Description=new.Description, Author=new.Author
                WHERE rowid=new.rowid;
            END;

            -- FTS5 触发器：删除时同步
            CREATE TRIGGER IF NOT EXISTS mods_fts_delete AFTER DELETE ON Mods BEGIN
                DELETE FROM ModsFTS WHERE rowid=old.rowid;
            END;

            -- Logical Mods table (HD2IAS nested archive support)
            CREATE TABLE IF NOT EXISTS LogicalMods (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Version TEXT NOT NULL DEFAULT '',
                Author TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                ManifestVersion INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL,
                ModifiedAt TEXT NOT NULL,
                TotalSize INTEGER NOT NULL DEFAULT 0,
                ThumbnailPath TEXT,
                Tags TEXT NOT NULL DEFAULT '[]',
                Metadata TEXT NOT NULL DEFAULT '{}'
            );

            CREATE INDEX IF NOT EXISTS idx_logicalmods_name ON LogicalMods(Name);
            CREATE INDEX IF NOT EXISTS idx_logicalmods_createdat ON LogicalMods(CreatedAt DESC);

            -- Logical Mod Components table
            CREATE TABLE IF NOT EXISTS LogicalModComponents (
                Id TEXT PRIMARY KEY,
                LogicalModId TEXT NOT NULL,
                ComponentId TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Name TEXT NOT NULL,
                SourcePackageId TEXT NOT NULL,
                Size INTEGER NOT NULL DEFAULT 0,
                Enabled INTEGER NOT NULL DEFAULT 1,
                Dependencies TEXT NOT NULL DEFAULT '[]',
                Metadata TEXT NOT NULL DEFAULT '{}',
                FOREIGN KEY (LogicalModId) REFERENCES LogicalMods(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_components_logicalmodid ON LogicalModComponents(LogicalModId);
            CREATE INDEX IF NOT EXISTS idx_components_type ON LogicalModComponents(Type);

            -- Package Sources table
            CREATE TABLE IF NOT EXISTS PackageSources (
                Id TEXT PRIMARY KEY,
                LogicalModId TEXT NOT NULL,
                Name TEXT NOT NULL,
                PhysicalPath TEXT NOT NULL,
                ArchiveType INTEGER NOT NULL,
                Size INTEGER NOT NULL DEFAULT 0,
                Hash TEXT NOT NULL DEFAULT '',
                LastModified TEXT NOT NULL,
                ParentPackageId TEXT,
                Depth INTEGER NOT NULL DEFAULT 0,
                PathInParent TEXT,
                FOREIGN KEY (LogicalModId) REFERENCES LogicalMods(Id) ON DELETE CASCADE,
                FOREIGN KEY (ParentPackageId) REFERENCES PackageSources(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_packages_logicalmodid ON PackageSources(LogicalModId);
            CREATE INDEX IF NOT EXISTS idx_packages_hash ON PackageSources(Hash);

            -- Deployable Files table
            CREATE TABLE IF NOT EXISTS DeployableFiles (
                Id TEXT PRIMARY KEY,
                ComponentId TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                TargetPath TEXT NOT NULL,
                Size INTEGER NOT NULL DEFAULT 0,
                Hash TEXT NOT NULL DEFAULT '',
                PackageId TEXT NOT NULL,
                IsShared INTEGER NOT NULL DEFAULT 0,
                Metadata TEXT NOT NULL DEFAULT '{}',
                FOREIGN KEY (ComponentId) REFERENCES LogicalModComponents(Id) ON DELETE CASCADE,
                FOREIGN KEY (PackageId) REFERENCES PackageSources(Id)
            );

            CREATE INDEX IF NOT EXISTS idx_deployablefiles_componentid ON DeployableFiles(ComponentId);
            CREATE INDEX IF NOT EXISTS idx_deployablefiles_targetpath ON DeployableFiles(TargetPath);

            -- File Ownerships table
            CREATE TABLE IF NOT EXISTS FileOwnerships (
                Id TEXT PRIMARY KEY,
                LogicalModId TEXT NOT NULL,
                ComponentId TEXT NOT NULL,
                PackageId TEXT NOT NULL,
                DeployPath TEXT NOT NULL,
                ContentHash TEXT NOT NULL,
                TransactionId TEXT NOT NULL,
                DeployedAt TEXT NOT NULL,
                IsShared INTEGER NOT NULL DEFAULT 0,
                ReferenceCount INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (LogicalModId) REFERENCES LogicalMods(Id) ON DELETE CASCADE,
                FOREIGN KEY (ComponentId) REFERENCES LogicalModComponents(Id) ON DELETE CASCADE,
                FOREIGN KEY (PackageId) REFERENCES PackageSources(Id)
            );

            CREATE INDEX IF NOT EXISTS idx_ownerships_logicalmodid ON FileOwnerships(LogicalModId);
            CREATE INDEX IF NOT EXISTS idx_ownerships_deploypath ON FileOwnerships(DeployPath);
            CREATE INDEX IF NOT EXISTS idx_ownerships_shared ON FileOwnerships(IsShared);

            -- Archive Analysis Cache table
            CREATE TABLE IF NOT EXISTS ArchiveAnalysisCache (
                Id TEXT PRIMARY KEY,
                FilePath TEXT NOT NULL,
                FileSize INTEGER NOT NULL,
                LastModified TEXT NOT NULL,
                ContentHash TEXT NOT NULL,
                ArchiveTreeJson TEXT NOT NULL,
                DetectionResultsJson TEXT NOT NULL,
                CachedAt TEXT NOT NULL,
                LastAccessed TEXT NOT NULL,
                AccessCount INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_archivecache_filepath ON ArchiveAnalysisCache(FilePath);
            CREATE INDEX IF NOT EXISTS idx_archivecache_cachedat ON ArchiveAnalysisCache(CachedAt);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_archivecache_unique ON ArchiveAnalysisCache(FilePath, FileSize, LastModified);

            -- Deployment Transactions table
            CREATE TABLE IF NOT EXISTS DeploymentTransactions (
                Id TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                ProfileId TEXT,
                LogicalModId TEXT,
                ModId TEXT,
                ComponentId TEXT,
                Operation TEXT NOT NULL,
                Method TEXT NOT NULL,
                Status TEXT NOT NULL,
                AddedFiles TEXT NOT NULL DEFAULT '[]',
                UpdatedFiles TEXT NOT NULL DEFAULT '[]',
                RemovedFiles TEXT NOT NULL DEFAULT '[]',
                SkippedFiles TEXT NOT NULL DEFAULT '[]',
                FileHashes TEXT NOT NULL DEFAULT '{}',
                Conflicts TEXT NOT NULL DEFAULT '[]',
                StartTime TEXT NOT NULL,
                EndTime TEXT,
                ErrorMessage TEXT,
                Metadata TEXT NOT NULL DEFAULT '{}',
                FOREIGN KEY (ProfileId) REFERENCES Profiles(Id),
                FOREIGN KEY (LogicalModId) REFERENCES LogicalMods(Id),
                FOREIGN KEY (ModId) REFERENCES Mods(Id),
                FOREIGN KEY (ComponentId) REFERENCES LogicalModComponents(Id)
            );

            CREATE INDEX IF NOT EXISTS idx_transactions_sessionid ON DeploymentTransactions(SessionId);
            CREATE INDEX IF NOT EXISTS idx_transactions_profileid ON DeploymentTransactions(ProfileId);
            CREATE INDEX IF NOT EXISTS idx_transactions_logicalmodid ON DeploymentTransactions(LogicalModId);
            CREATE INDEX IF NOT EXISTS idx_transactions_modid ON DeploymentTransactions(ModId);
            CREATE INDEX IF NOT EXISTS idx_transactions_starttime ON DeploymentTransactions(StartTime DESC);

            -- 兼容性检查结果摘要：跨会话回填角标；ContentStamp 变化即视为模组内容已变更。
            -- 不加外键（本库未开启 foreign_keys），模组删除后的孤儿行由读取时清理。
            CREATE TABLE IF NOT EXISTS CompatibilityResults (
                ModId TEXT PRIMARY KEY,
                Status INTEGER NOT NULL,
                Reason INTEGER NOT NULL,
                UnitCount INTEGER NOT NULL,
                ComparedUnitCount INTEGER NOT NULL,
                HasBlockingIssues INTEGER NOT NULL,
                ReferenceSource TEXT,
                IssuesJson TEXT NOT NULL DEFAULT '[]',
                CheckedAtUtc TEXT NOT NULL,
                ContentStamp TEXT NOT NULL
            );
        ";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 平台信息与模组保存在同一行，删除模组无需额外清理关联表。
        foreach (var (column, type) in new[]
        {
            ("PlatformId", "TEXT"), ("PlatformModId", "TEXT"), ("PlatformVersion", "TEXT"),
            ("LastUpdateCheck", "TEXT"), ("PlatformLinkJson", "TEXT")
        })
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Mods') WHERE name = @Column";
            check.Parameters.AddWithValue("@Column", column);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE Mods ADD COLUMN {column} {type}";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        using (var separatorColumnCheck = connection.CreateCommand())
        {
            separatorColumnCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ProfileSeparators') WHERE name = 'IsCollapsed'";
            if (Convert.ToInt64(await separatorColumnCheck.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                using var alterSeparator = connection.CreateCommand();
                alterSeparator.CommandText = "ALTER TABLE ProfileSeparators ADD COLUMN IsCollapsed INTEGER NOT NULL DEFAULT 0";
                await alterSeparator.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // 插件完整性锚点：安装时记录程序集 SHA-256，启动前校验（Phase 2 安全加固）
        using (var pluginHashCheck = connection.CreateCommand())
        {
            pluginHashCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Plugins') WHERE name = 'AssemblySha256'";
            if (Convert.ToInt64(await pluginHashCheck.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                using var alterPluginHash = connection.CreateCommand();
                alterPluginHash.CommandText = "ALTER TABLE Plugins ADD COLUMN AssemblySha256 TEXT";
                await alterPluginHash.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        foreach (var (table, column, type) in new[]
        {
            ("ProfileSeparators", "Color", "TEXT NOT NULL DEFAULT ''"),
            ("ProfileSeparators", "MemberCount", "INTEGER"),
            ("Profiles", "IsLuaGroupCollapsed", "INTEGER NOT NULL DEFAULT 0"),
            ("Profiles", "LuaSeparatorColor", "TEXT NOT NULL DEFAULT ''"),
            ("Profiles", "IsLibraryLayout", "INTEGER NOT NULL DEFAULT 0"),
            ("Profiles", "DefaultMigrationStateJson", "TEXT NOT NULL DEFAULT '{}'"),
            ("DeploymentRecords", "OptionsSnapshotJson", "TEXT"),
            ("DeploymentRecords", "DeploymentOrderIndex", "INTEGER"),
            ("DeploymentRecords", "ModVersion", "TEXT"),
            ("DeploymentRecords", "GamePath", "TEXT"),
            ("DeploymentRecords", "DeploymentOrder", "TEXT"),
            ("DeploymentRecords", "AppliedAt", "TEXT"),
            ("DeploymentFiles", "GamePath", "TEXT"),
            ("DeploymentFiles", "SourceKey", "TEXT"),
            ("DeploymentFiles", "SourceRelativePath", "TEXT")
        })
        {
            using var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @Column";
            check.Parameters.AddWithValue("@Column", column);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // 这些索引依赖本次新增的列；必须在旧库补列之后创建，不能放进上方初始 SQL。
        using (var deploymentIndexes = connection.CreateCommand())
        {
            deploymentIndexes.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_deployment_gamepath_mod ON DeploymentRecords(GamePath, ModId);
                CREATE INDEX IF NOT EXISTS idx_deploymentfiles_gamepath_target ON DeploymentFiles(GamePath, TargetPath);
                CREATE INDEX IF NOT EXISTS idx_deploymentfiles_sourcekey ON DeploymentFiles(DeploymentId, SourceKey);
                """;
            await deploymentIndexes.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var checkDefault = connection.CreateCommand())
        {
            checkDefault.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Profiles') WHERE name = 'IsDefault';";
            if (Convert.ToInt64(await checkDefault.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                using var alterDefault = connection.CreateCommand();
                alterDefault.CommandText = "ALTER TABLE Profiles ADD COLUMN IsDefault INTEGER NOT NULL DEFAULT 0;";
                await alterDefault.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // 十六进制字符串 JSON 避免 SQLite 有符号整数无法容纳完整的 64 位 Type ID。
        using (var checkTypes = connection.CreateCommand())
        {
            checkTypes.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Mods') WHERE name = 'ResourceTypeIds';";
            if (Convert.ToInt64(await checkTypes.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                using var alterTypes = connection.CreateCommand();
                alterTypes.CommandText = "ALTER TABLE Mods ADD COLUMN ResourceTypeIds TEXT;";
                await alterTypes.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        using (var checkBsl = connection.CreateCommand())
        {
            checkBsl.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Mods') WHERE name = 'BslRole';";
            if (Convert.ToInt64(await checkBsl.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                using var alterBsl = connection.CreateCommand();
                alterBsl.CommandText = "ALTER TABLE Mods ADD COLUMN BslRole INTEGER;";
                await alterBsl.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        using (var checkBslVersion = connection.CreateCommand())
        {
            checkBslVersion.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Mods') WHERE name = 'BslDetectionVersion';";
            if (Convert.ToInt64(await checkBslVersion.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                using var alterBslVersion = connection.CreateCommand();
                alterBslVersion.CommandText = "ALTER TABLE Mods ADD COLUMN BslDetectionVersion INTEGER NOT NULL DEFAULT 0;";
                await alterBslVersion.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // 轻量列迁移：老库的 Profiles 没有 SortOrder 列时补列，并按创建时间回填顺序
        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('Profiles') WHERE name = 'SortOrder';";
            var hasSortOrder = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!hasSortOrder)
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Profiles ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;";
                await alterCommand.ExecuteNonQueryAsync(cancellationToken);

                using var backfillCommand = connection.CreateCommand();
                backfillCommand.CommandText = @"
                    UPDATE Profiles SET SortOrder = (
                        SELECT COUNT(*) FROM Profiles p2
                        WHERE p2.CreatedAt < Profiles.CreatedAt
                           OR (p2.CreatedAt = Profiles.CreatedAt AND p2.Id < Profiles.Id));";
                await backfillCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // 轻量列迁移：老库的 ProfileMods 没有 OptionsJson 列时补列。
        // 只加列、不重建表，模组行与 [Order] 保持原值，软件升级不能打乱配置文件里的模组顺序。
        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('ProfileMods') WHERE name = 'OptionsJson';";
            var hasOptionsJson = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!hasOptionsJson)
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE ProfileMods ADD COLUMN OptionsJson TEXT NOT NULL DEFAULT '';";
                await alterCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    // 短连接由各操作释放，结束上下文不触碰其它上下文的池和 GC。
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
