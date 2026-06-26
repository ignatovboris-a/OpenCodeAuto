using System.Text.Json;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Infrastructure.Files;
using OpenCodeQueue.Infrastructure.Json;

namespace OpenCodeQueue.Infrastructure.Configuration;

/// <summary>
/// Relative projectDir values are resolved from the config file directory; other project paths are resolved from projectDir.
/// </summary>
public sealed class JsonAppConfigStore(IClock? clock = null) : IAppConfigStore
{
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task<AppConfig?> LoadAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(configPath);
        await using var stream = File.OpenRead(fullPath);
        var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, QueueJson.Options, cancellationToken);
        return config is null ? null : Normalize(config, fullPath);
    }

    public async Task<AppConfig> LoadOrCreateDefaultAsync(string configPath, CancellationToken cancellationToken)
    {
        return await LoadAsync(configPath, cancellationToken) ?? new AppConfig();
    }

    public async Task SaveAsync(string configPath, AppConfig config, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(configPath);
        var normalized = Normalize(config, fullPath);
        await using var configLock = AcquireConfigLock(fullPath, clock.Now);
        await AtomicFileWriter.WriteAsync(
            fullPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, normalized, QueueJson.Options, token),
            cancellationToken);
    }

    private static IAsyncDisposable AcquireConfigLock(string configPath, DateTimeOffset createdAt)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lockPath = configPath + ".lock";
        var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write($"pid={Environment.ProcessId}; machine={Environment.MachineName}; createdAt={createdAt:u}");
        writer.Flush();
        stream.Flush(flushToDisk: true);
        return new ConfigLock(stream, lockPath);
    }

    private sealed class ConfigLock(FileStream stream, string lockPath) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            FileCleanup.TryDelete(lockPath);
        }
    }

    private static AppConfig Normalize(AppConfig config, string configPath)
    {
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Directory.GetCurrentDirectory();
        var projects = config.Projects.Select(project => Normalize(project, configDir)).ToArray();
        return config with { Projects = projects };
    }

    private static ProjectProfile Normalize(ProjectProfile project, string configDir)
    {
        var projectDir = PathResolver.Resolve(project.ProjectDir, configDir);
        var qualityDir = string.IsNullOrWhiteSpace(project.QualityDir)
            ? project.ReviewsDir ?? "quality"
            : project.QualityDir;
        var stateDir = string.IsNullOrWhiteSpace(project.StateDir) ? ".queue" : project.StateDir;
        var completedDir = string.IsNullOrWhiteSpace(project.CompletedDir) ? Path.Combine(stateDir, "completed") : project.CompletedDir;

        return project with
        {
            Id = new ProjectId(project.Id.Value.Trim()),
            ProjectDir = projectDir,
            PromptsDir = PathResolver.ResolveProjectPath(project.PromptsDir, projectDir),
            QualityDir = PathResolver.ResolveProjectPath(qualityDir, projectDir),
            ReviewsDir = null,
            StateDir = PathResolver.ResolveProjectPath(stateDir, projectDir),
            CompletedDir = PathResolver.ResolveProjectPath(completedDir, projectDir)
        };
    }
}
