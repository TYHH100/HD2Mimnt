param([switch]$NoBuild)

# SQL safety regression gate: foreign keys, delete ordering, failure timing,
# restore and storage-migration rollback. Run locally or from a scheduled job.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'HD2Mimnt.slnx'
$tests = Join-Path $root 'tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj'

if (-not $NoBuild) {
    dotnet build $solution
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$filter = 'FullyQualifiedName~SqlSafetyTimingTests|FullyQualifiedName~SqlRestoreTimingTests|FullyQualifiedName~StorageMigrationTimingTests|FullyQualifiedName~ModDeletionSafetyTests|FullyQualifiedName~BackupServiceTests|FullyQualifiedName~SqlForeignKeysTests|FullyQualifiedName~DatabaseIntegrityGuardTests|FullyQualifiedName~StorageMigrationSafetyTests|FullyQualifiedName~DeploymentStateMigrationServiceTests|FullyQualifiedName~DeploymentServiceIntegrationTests'
dotnet test $tests --no-build --filter $filter --logger 'console;verbosity=normal'
exit $LASTEXITCODE
