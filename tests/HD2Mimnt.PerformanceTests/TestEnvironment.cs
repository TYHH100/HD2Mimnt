using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.PerformanceTests;

/// <summary>
/// Isolated test environment for performance testing
/// </summary>
public class TestEnvironment : IDisposable
{
    public string TestDirectory { get; }
    public string DatabasePath { get; }
    public string ModStoragePath { get; }
    public string ThumbnailStoragePath { get; }
    public string DeploymentPath { get; }
    public HD2MimntDbContext DbContext { get; }
    public ModRepository ModRepository { get; }
    public ProfileRepository ProfileRepository { get; }
    public DeploymentRepository DeploymentRepository { get; }
    public TaskRepository TaskRepository { get; }

    public TestEnvironment(string testName)
    {
        // Create isolated test directory
        TestDirectory = Path.Combine(Path.GetTempPath(), "HD2Mimnt_PerfTests", testName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TestDirectory);

        DatabasePath = Path.Combine(TestDirectory, "test.db");
        ModStoragePath = Path.Combine(TestDirectory, "Mods");
        ThumbnailStoragePath = Path.Combine(TestDirectory, "Thumbnails");
        DeploymentPath = Path.Combine(TestDirectory, "Deployed");

        Directory.CreateDirectory(ModStoragePath);
        Directory.CreateDirectory(ThumbnailStoragePath);
        Directory.CreateDirectory(DeploymentPath);

        // Create database context
        DbContext = new HD2MimntDbContext(DatabasePath);
        DbContext.InitializeDatabaseAsync().Wait();

        // Create repositories
        ModRepository = new ModRepository(DbContext);
        ProfileRepository = new ProfileRepository(DbContext);
        DeploymentRepository = new DeploymentRepository(DbContext);
        TaskRepository = new TaskRepository(DbContext);
    }

    public async Task PopulateModsAsync(List<Mod> mods)
    {
        foreach (var mod in mods)
        {
            await ModRepository.AddAsync(mod);
        }
    }

    public async Task PopulateProfilesAsync(List<Profile> profiles)
    {
        foreach (var profile in profiles)
        {
            await ProfileRepository.AddAsync(profile);
        }
    }


    public void Dispose()
    {
        try
        {
            DbContext?.Dispose();

            // Clean up test directory
            if (Directory.Exists(TestDirectory))
            {
                Directory.Delete(TestDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
