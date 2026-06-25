using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Infrastructure.Files;
using OpenCodeQueue.Infrastructure.Json;
using System.Diagnostics;
using System.Text.Json;

namespace OpenCodeQueue.Infrastructure.State;

public sealed class FileRunLock(IClock? clock = null) : IRunLock
{
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task<RunLockAcquireResult> TryAcquireAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stateDir = ProjectPaths.StateDir(project);
        Directory.CreateDirectory(stateDir);
        var lockPath = ProjectPaths.RunLockFile(project);

        if (File.Exists(lockPath))
        {
            var existing = await ReadAsync(project, cancellationToken);
            return new RunLockAcquireResult(null, existing, existing?.IsStale == true
                ? "Найден stale lock. Выполните resume для восстановления или force unlock после проверки."
                : "Очередь этого проекта уже запущена другим процессом.");
        }

        try
        {
            var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            var info = new RunLockInfo
            {
                Pid = Environment.ProcessId,
                MachineName = Environment.MachineName,
                CreatedAt = clock.Now,
                ProjectId = project.Id,
                IsCurrentProcess = true,
                IsStale = false
            };
            await JsonSerializer.SerializeAsync(stream, info, QueueJson.Options, cancellationToken);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            IAsyncDisposable releaser = new FileLockReleaser(stream, lockPath);
            return new RunLockAcquireResult(releaser, null, null);
        }
        catch (IOException)
        {
            var existing = await ReadAsync(project, cancellationToken);
            return new RunLockAcquireResult(null, existing, "Не удалось получить lock проекта.");
        }
    }

    public async Task<RunLockInfo?> ReadAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var lockPath = ProjectPaths.RunLockFile(project);
        if (!File.Exists(lockPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var info = await JsonSerializer.DeserializeAsync<RunLockInfo>(stream, QueueJson.Options, cancellationToken);
            return Enrich(info, project);
        }
        catch (Exception) when (File.Exists(lockPath))
        {
            return new RunLockInfo
            {
                ProjectId = project.Id,
                CreatedAt = File.GetCreationTimeUtc(lockPath),
                IsCurrentProcess = false,
                IsStale = false
            };
        }
    }

    public Task ForceUnlockAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileCleanup.TryDelete(ProjectPaths.RunLockFile(project));
        return Task.CompletedTask;
    }

    private static RunLockInfo? Enrich(RunLockInfo? info, ProjectProfile project)
    {
        if (info is null)
        {
            return null;
        }

        var isCurrentProcess = info.Pid == Environment.ProcessId && string.Equals(info.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        var isStale = !isCurrentProcess && IsDefinitelyStale(info.Pid, info.MachineName);
        return info with { ProjectId = info.ProjectId.Value.Length == 0 ? project.Id : info.ProjectId, IsCurrentProcess = isCurrentProcess, IsStale = isStale };
    }

    private static bool IsDefinitelyStale(int? pid, string? machineName)
    {
        if (pid is null || !string.Equals(machineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private sealed class FileLockReleaser(FileStream stream, string lockPath) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            FileCleanup.TryDelete(lockPath);
        }
    }
}
