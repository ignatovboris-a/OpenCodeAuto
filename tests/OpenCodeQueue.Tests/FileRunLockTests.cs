using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Infrastructure;
using OpenCodeQueue.Infrastructure.State;

namespace OpenCodeQueue.Tests;

public sealed class FileRunLockTests
{
    [Fact]
    public async Task TryAcquireAsync_TakesOverStaleLockForRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var project = new ProjectProfile { Id = "test", ProjectDir = root };
        var stateDir = ProjectPaths.StateDir(project);
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, "lock"), "{\"pid\":999999,\"machineName\":\"" + Environment.MachineName + "\",\"createdAt\":\"2020-01-01T00:00:00Z\",\"projectId\":\"test\"}");

        var runLock = new FileRunLock();

        var acquired = await runLock.TryAcquireAsync(project, CancellationToken.None);

        Assert.True(acquired.Acquired);
        Assert.Null(acquired.ExistingLock);
        Assert.True(File.Exists(Path.Combine(stateDir, "lock")));
        await acquired.Releaser!.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_DoesNotMarkDifferentMachineLockAsStale()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var project = new ProjectProfile { Id = "test", ProjectDir = root };
        var stateDir = ProjectPaths.StateDir(project);
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, "lock"), "{\"pid\":999999,\"machineName\":\"other-machine\",\"createdAt\":\"2020-01-01T00:00:00Z\",\"projectId\":\"test\"}");

        var acquired = await new FileRunLock().TryAcquireAsync(project, CancellationToken.None);

        Assert.False(acquired.Acquired);
        Assert.False(acquired.ExistingLock!.IsStale);
    }

    [Fact]
    public async Task TryAcquireAsync_CreatesProjectScopedLockMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var runLock = new FileRunLock();
        var project = new ProjectProfile { Id = "test", ProjectDir = root };

        var acquired = await runLock.TryAcquireAsync(project, CancellationToken.None);
        var info = await runLock.ReadAsync(project, CancellationToken.None);

        Assert.True(acquired.Acquired);
        Assert.Equal(Environment.ProcessId, info!.Pid);
        Assert.Equal("test", info.ProjectId.Value);
        await acquired.Releaser!.DisposeAsync();
    }

    [Fact]
    public async Task ForceUnlockAsync_RemovesExistingLock()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var project = new ProjectProfile { Id = "test", ProjectDir = root };
        var stateDir = ProjectPaths.StateDir(project);
        Directory.CreateDirectory(stateDir);
        var lockPath = Path.Combine(stateDir, "lock");
        await File.WriteAllTextAsync(lockPath, "stale");

        await new FileRunLock().ForceUnlockAsync(project, CancellationToken.None);

        Assert.False(File.Exists(lockPath));
    }
}
