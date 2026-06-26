using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Core.Workflow;
using OpenCodeQueue.Infrastructure.Configuration;
using OpenCodeQueue.Infrastructure.Files;
using OpenCodeQueue.Infrastructure.Prompts;
using OpenCodeQueue.Infrastructure.State;

namespace OpenCodeQueue.Tests;

public sealed class QueueUseCasesTests
{
    [Fact]
    public async Task RunQueueAsync_RunsTaskThenAllQualityInSameSessionAndArchivesTask()
    {
        var fixture = await CreateFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "quality", "01-review.md"), "review one");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "quality", "02-tests.md"), "review two");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["task", "quality-01", "quality-02"], fixture.OpenCode.SentStepIds);
        Assert.All(fixture.OpenCode.SessionIds, sessionId => Assert.Equal("session-1", sessionId));
        Assert.False(File.Exists(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md")));
        Assert.True(File.Exists(Path.Combine(fixture.ProjectDir, "quality", "01-review.md")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.ProjectDir, ".queue", "completed"), "*_01-task.md"));

        var state = await fixture.StateStore.LoadQueueStateAsync(fixture.Project, CancellationToken.None);
        Assert.Null(state!.ActiveRunId);
        var manifest = await fixture.StateStore.LoadRunManifestAsync(fixture.Project, state.LastCompletedRunId!, CancellationToken.None);
        Assert.Equal(RunStatus.Completed, manifest!.Status);
    }

    [Fact]
    public async Task RunQueueAsync_WhenQualityFails_StopsAndKeepsTaskPending()
    {
        var fixture = await CreateFixtureAsync(failStepId: "quality-01");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "quality", "01-review.md"), "review one");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "quality", "02-tests.md"), "review two");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(["task", "quality-01"], fixture.OpenCode.SentStepIds);
        Assert.True(File.Exists(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md")));
        var state = await fixture.StateStore.LoadQueueStateAsync(fixture.Project, CancellationToken.None);
        Assert.NotNull(state!.ActiveRunId);
        var manifest = await fixture.StateStore.LoadRunManifestAsync(fixture.Project, state.ActiveRunId!, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, manifest!.Status);
    }

    [Fact]
    public async Task RunQueueAsync_WhenTaskSendFails_SavesSessionIdForRecovery()
    {
        var fixture = await CreateFixtureAsync(failStepId: "task");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("session-1", result.Manifest!.SessionId);
        var state = await fixture.StateStore.LoadQueueStateAsync(fixture.Project, CancellationToken.None);
        var manifest = await fixture.StateStore.LoadRunManifestAsync(fixture.Project, state!.ActiveRunId!, CancellationToken.None);
        Assert.Equal("session-1", manifest!.SessionId);
    }

    [Fact]
    public async Task RunQueueAsync_WhenQualityFailsAndStopIsDisabled_ContinuesQualityButDoesNotArchive()
    {
        var fixture = await CreateFixtureAsync(failStepId: "quality-01", stopOnQualityFailure: false);
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "quality", "01-review.md"), "review one");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "quality", "02-tests.md"), "review two");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(["task", "quality-01", "quality-02"], fixture.OpenCode.SentStepIds);
        Assert.True(File.Exists(taskPath));
        Assert.Equal(RunStatus.Failed, result.Manifest!.Status);
    }

    [Fact]
    public async Task ResumeAsync_WhenCompletedPendingArchive_OnlyArchivesTask()
    {
        var fixture = await CreateFixtureAsync();
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");
        var discovered = await new FileSystemPromptRepository().DiscoverAsync(fixture.Project, CancellationToken.None);
        var task = discovered.TaskPrompts[0];
        var now = DateTimeOffset.Parse("2026-06-26T10:00:00Z");
        var runId = "run-completed-pending";
        await fixture.StateStore.SaveQueueStateAsync(fixture.Project, new QueueState
        {
            ProjectId = fixture.Project.Id,
            ProjectDirSnapshot = fixture.Project.ProjectDir,
            ActiveRunId = runId,
            CreatedAt = now,
            UpdatedAt = now
        }, CancellationToken.None);
        await fixture.StateStore.SaveRunManifestAsync(fixture.Project, new RunManifest
        {
            RunId = runId,
            ProjectId = fixture.Project.Id,
            ProjectDirSnapshot = fixture.Project.ProjectDir,
            SessionId = "session-existing",
            Status = RunStatus.CompletedPendingArchive,
            ArchiveStatus = ArchiveStatus.Pending,
            TaskDescriptor = task,
            Steps = [new WorkflowStep { Id = WorkflowStepId.Task, Kind = Core.Prompts.PromptKind.Task, SourcePath = task.Path, ContentHash = task.ContentHash, Status = WorkflowStepStatus.Completed }],
            CreatedAt = now,
            StartedAt = now,
            UpdatedAt = now
        }, CancellationToken.None);

        var result = await fixture.UseCases.ResumeAsync(fixture.ConfigPath, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.OpenCode.SentStepIds);
        Assert.False(File.Exists(taskPath));
        var state = await fixture.StateStore.LoadQueueStateAsync(fixture.Project, CancellationToken.None);
        Assert.Null(state!.ActiveRunId);
        var manifest = await fixture.StateStore.LoadRunManifestAsync(fixture.Project, runId, CancellationToken.None);
        Assert.Equal(RunStatus.Completed, manifest!.Status);
    }

    [Fact]
    public async Task ResumeAsync_WhenRunAlreadyFailed_DoesNotResendFailedStep()
    {
        var fixture = await CreateFixtureAsync();
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");
        var discovered = await new FileSystemPromptRepository().DiscoverAsync(fixture.Project, CancellationToken.None);
        var task = discovered.TaskPrompts[0];
        var runId = "run-failed";
        await SaveActiveManifestAsync(fixture, runId, new RunManifest
        {
            RunId = runId,
            ProjectId = fixture.Project.Id,
            ProjectDirSnapshot = fixture.Project.ProjectDir,
            SessionId = "session-existing",
            Status = RunStatus.Failed,
            LastError = "previous failure",
            TaskDescriptor = task,
            Steps = [new WorkflowStep { Id = WorkflowStepId.Task, Kind = Core.Prompts.PromptKind.Task, SourcePath = task.Path, ContentHash = task.ContentHash, Status = WorkflowStepStatus.Failed }],
            CreatedAt = FixedNow,
            StartedAt = FixedNow,
            UpdatedAt = FixedNow
        });

        var result = await fixture.UseCases.ResumeAsync(fixture.ConfigPath, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.OpenCode.SentStepIds);
        Assert.Equal(RunStatus.Failed, result.Manifest!.Status);
    }

    [Fact]
    public async Task ResumeAsync_WhenRecoveryIsConservative_DoesNotResendOriginalPrompt()
    {
        var fixture = await CreateFixtureAsync(recoveryOutcome: StepRecoveryOutcome.ConservativeContinueSent);
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");
        var discovered = await new FileSystemPromptRepository().DiscoverAsync(fixture.Project, CancellationToken.None);
        var task = discovered.TaskPrompts[0];
        var runId = "run-recovering";
        await SaveActiveManifestAsync(fixture, runId, new RunManifest
        {
            RunId = runId,
            ProjectId = fixture.Project.Id,
            ProjectDirSnapshot = fixture.Project.ProjectDir,
            SessionId = "session-existing",
            Status = RunStatus.Running,
            TaskDescriptor = task,
            Steps = [new WorkflowStep { Id = WorkflowStepId.Task, Kind = Core.Prompts.PromptKind.Task, SourcePath = task.Path, ContentHash = task.ContentHash, Status = WorkflowStepStatus.Running }],
            CreatedAt = FixedNow,
            StartedAt = FixedNow,
            UpdatedAt = FixedNow
        });

        var result = await fixture.UseCases.ResumeAsync(fixture.ConfigPath, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.OpenCode.SentStepIds);
        Assert.Equal(RunStatus.Running, result.Manifest!.Status);
        Assert.Equal(WorkflowStepStatus.Recovering, result.Manifest.Steps[0].Status);
    }

    [Fact]
    public async Task ResumeAsync_WhenRunningStepLostSessionId_StopsForManualInterventionWithoutResendingPrompt()
    {
        var fixture = await CreateFixtureAsync(recoveryOutcome: StepRecoveryOutcome.NotFound);
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");
        var discovered = await new FileSystemPromptRepository().DiscoverAsync(fixture.Project, CancellationToken.None);
        var task = discovered.TaskPrompts[0];
        var runId = "run-lost-session";
        await SaveActiveManifestAsync(fixture, runId, new RunManifest
        {
            RunId = runId,
            ProjectId = fixture.Project.Id,
            ProjectDirSnapshot = fixture.Project.ProjectDir,
            Status = RunStatus.Running,
            TaskDescriptor = task,
            Steps = [new WorkflowStep { Id = WorkflowStepId.Task, Kind = Core.Prompts.PromptKind.Task, SourcePath = task.Path, ContentHash = task.ContentHash, Status = WorkflowStepStatus.Running }],
            CreatedAt = FixedNow,
            StartedAt = FixedNow,
            UpdatedAt = FixedNow
        });

        var result = await fixture.UseCases.ResumeAsync(fixture.ConfigPath, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.OpenCode.SentStepIds);
        Assert.Equal(RunStatus.NeedsManualIntervention, result.Manifest!.Status);
        Assert.Contains("sessionId", result.Manifest.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_WhenSourceHashChanged_LeavesCompletedPendingArchiveAndTaskInPlace()
    {
        var fixture = await CreateFixtureAsync(changeTaskBeforeArchive: true);
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RunStatus.CompletedPendingArchive, result.Manifest!.Status);
        Assert.True(File.Exists(taskPath));
        Assert.Contains("изменился", result.Manifest.LastError, StringComparison.Ordinal);
    }

    private static async Task<Fixture> CreateFixtureAsync(string? failStepId = null, bool changeTaskBeforeArchive = false, bool stopOnQualityFailure = true, StepRecoveryOutcome recoveryOutcome = StepRecoveryOutcome.Completed)
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(projectDir, "prompts"));
        Directory.CreateDirectory(Path.Combine(projectDir, "quality"));
        var configPath = Path.Combine(root, "opencode-queue.json");
        var configStore = new JsonAppConfigStore();
        var registry = new JsonProjectRegistry(configStore);
        var project = new ProjectProfile { Id = "project-a", ProjectDir = projectDir, StopOnQualityFailure = stopOnQualityFailure };
        await registry.AddOrUpdateAsync(configPath, project, CancellationToken.None);
        var stateStore = new JsonStateStore();
        var openCode = new FakeOpenCodeClient(failStepId, changeTaskBeforeArchive, recoveryOutcome);
        var useCases = new QueueUseCases(
            registry,
            new FileSystemPromptRepository(),
            stateStore,
            new FileRunLock(new FixedClock()),
            openCode,
            new RunWorkspace(),
            new FileSystemArchiver(),
            new FixedClock());
        return new Fixture(configPath, projectDir, project, stateStore, openCode, useCases);
    }

    private sealed record Fixture(string ConfigPath, string ProjectDir, ProjectProfile Project, JsonStateStore StateStore, FakeOpenCodeClient OpenCode, QueueUseCases UseCases);

    private static async Task SaveActiveManifestAsync(Fixture fixture, string runId, RunManifest manifest)
    {
        await fixture.StateStore.SaveQueueStateAsync(fixture.Project, new QueueState
        {
            ProjectId = fixture.Project.Id,
            ProjectDirSnapshot = fixture.Project.ProjectDir,
            ActiveRunId = runId,
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow
        }, CancellationToken.None);
        await fixture.StateStore.SaveRunManifestAsync(fixture.Project, manifest, CancellationToken.None);
    }

    private sealed class FakeOpenCodeClient(string? failStepId, bool changeTaskBeforeArchive, StepRecoveryOutcome recoveryOutcome) : IOpenCodeClient
    {
        public List<string> SentStepIds { get; } = [];

        public List<string> SessionIds { get; } = [];

        public Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OpenCodeSession> StartSessionAsync(ProjectProfile project, string title, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeSession("session-1", project.ProjectDir, title));
        }

        public Task<OpenCodeMessageResult> SendPromptAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken)
        {
            return RecordAsync(project, sessionId, payload, cancellationToken).ContinueWith(_ => Result(payload), cancellationToken);
        }

        public Task<OpenCodeSessionDetails> GetSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeSessionDetails(new OpenCodeSession(sessionId, project.ProjectDir), new OpenCodeSessionStatus(OpenCodeSessionState.Idle), []));
        }

        public Task<OpenCodeSessionStatus> GetSessionStatusAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeSessionStatus(OpenCodeSessionState.Idle));
        }

        public Task<StepRecoveryResult> TryRecoverStepAsync(ProjectProfile project, RunManifest manifest, WorkflowStep step, CancellationToken cancellationToken)
        {
            return Task.FromResult(new StepRecoveryResult(recoveryOutcome, recoveryOutcome.ToString()));
        }

        public Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task RecordAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken)
        {
            SentStepIds.Add(payload.StepId!);
            SessionIds.Add(sessionId);
            if (changeTaskBeforeArchive && payload.StepId == "task")
            {
                await File.WriteAllTextAsync(Path.Combine(project.ProjectDir, "prompts", "01-task.md"), "changed", cancellationToken);
            }
        }

        private OpenCodeMessageResult Result(PromptPayload payload)
        {
            return string.Equals(payload.StepId, failStepId, StringComparison.Ordinal)
                ? new OpenCodeMessageResult(false, ErrorMessage: "fake failure")
                : new OpenCodeMessageResult(true, payload.MessageId);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => FixedNow;
    }

    private static DateTimeOffset FixedNow => DateTimeOffset.Parse("2026-06-26T10:00:00Z");
}
