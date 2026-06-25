using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Infrastructure.State;

namespace OpenCodeQueue.Tests;

public sealed class JsonStateStoreTests
{
    [Fact]
    public async Task SaveStateAndManifestAsync_WritesProjectScopedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var project = new ProjectProfile { Id = "project-a", ProjectDir = root };
        var store = new JsonStateStore();
        var manifest = new RunManifest
        {
            RunId = "run-1",
            ProjectId = "project-a",
            Status = RunStatus.Running,
            ProjectDirSnapshot = root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveRunManifestAsync(project, manifest, CancellationToken.None);
        await store.SaveQueueStateAsync(project, new QueueState
        {
            ProjectId = "project-a",
            ProjectDirSnapshot = root,
            ActiveRunId = "run-1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var loaded = await store.LoadRunManifestAsync(project, "run-1", CancellationToken.None);
        var json = await File.ReadAllTextAsync(Path.Combine(root, ".queue", "state.json"));
        var manifestJson = await File.ReadAllTextAsync(Path.Combine(root, ".queue", "runs", "run-1", "manifest.json"));

        Assert.NotNull(loaded);
        Assert.Equal("project-a", loaded.ProjectId);
        Assert.Equal(RunStatus.Running, loaded.Status);
        Assert.Contains("\"activeRunId\": \"run-1\"", json);
        Assert.Contains("\"projectId\": \"project-a\"", json);
        Assert.Contains("\"status\": \"Running\"", manifestJson);
    }

    [Fact]
    public async Task LoadQueueStateAsync_RejectsProjectMismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var projectA = new ProjectProfile { Id = "project-a", ProjectDir = root };
        var projectB = new ProjectProfile { Id = "project-b", ProjectDir = root };
        var store = new JsonStateStore();

        await store.SaveQueueStateAsync(projectA, new QueueState
        {
            ProjectId = "project-a",
            ProjectDirSnapshot = root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadQueueStateAsync(projectB, CancellationToken.None));
        Assert.Contains("Автоматическое продолжение остановлено", exception.Message);
    }

    [Fact]
    public async Task LoadRunManifestAsync_ReportsCorruptedManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var project = new ProjectProfile { Id = "project-a", ProjectDir = root };
        var manifestPath = Path.Combine(root, ".queue", "runs", "run-1", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllTextAsync(manifestPath, "{ broken json");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new JsonStateStore().LoadRunManifestAsync(project, "run-1", CancellationToken.None));
        Assert.Contains("повреждён", exception.Message);
    }

    [Fact]
    public async Task SaveRunManifestAsync_RejectsUnsafeRunId()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var project = new ProjectProfile { Id = "project-a", ProjectDir = root };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new JsonStateStore().SaveRunManifestAsync(project, new RunManifest
        {
            RunId = "../outside",
            ProjectId = "project-a",
            ProjectDirSnapshot = root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None));

        Assert.Contains("runId", exception.Message);
    }

    [Fact]
    public async Task LoadQueueStateAsync_TreatsMissingRequiredProjectDirSnapshotAsCorruptedState()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var project = new ProjectProfile { Id = "project-a", ProjectDir = root };
        var stateDir = Path.Combine(root, ".queue");
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, "state.json"), "{\"schemaVersion\":1,\"projectId\":\"project-a\"}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new JsonStateStore().LoadQueueStateAsync(project, CancellationToken.None));

        Assert.Contains("повреждён", exception.Message);
    }

    [Fact]
    public async Task CompletedPendingArchive_RemainsActiveRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var project = new ProjectProfile { Id = "project-a", ProjectDir = root };
        var store = new JsonStateStore();

        await store.SaveRunManifestAsync(project, new RunManifest
        {
            RunId = "run-archive",
            ProjectId = "project-a",
            ProjectDirSnapshot = root,
            Status = RunStatus.CompletedPendingArchive,
            ArchiveStatus = ArchiveStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await store.SaveQueueStateAsync(project, new QueueState
        {
            ProjectId = "project-a",
            ProjectDirSnapshot = root,
            ActiveRunId = "run-archive",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var state = await store.LoadQueueStateAsync(project, CancellationToken.None);
        Assert.Equal("run-archive", state!.ActiveRunId);
    }

    [Fact]
    public async Task CompletedRun_CanClearActiveRunAndRecordLastCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var project = new ProjectProfile { Id = "project-a", ProjectDir = root };
        var store = new JsonStateStore();

        await store.SaveRunManifestAsync(project, new RunManifest
        {
            RunId = "run-done",
            ProjectId = "project-a",
            ProjectDirSnapshot = root,
            Status = RunStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        await store.SaveQueueStateAsync(project, new QueueState
        {
            ProjectId = "project-a",
            ProjectDirSnapshot = root,
            ActiveRunId = null,
            LastCompletedRunId = "run-done",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var state = await store.LoadQueueStateAsync(project, CancellationToken.None);
        Assert.Null(state!.ActiveRunId);
        Assert.Equal("run-done", state.LastCompletedRunId);
    }
}
