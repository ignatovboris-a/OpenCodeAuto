using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Core.Workflow;
using OpenCodeQueue.Infrastructure.Configuration;
using OpenCodeQueue.Infrastructure.Files;
using OpenCodeQueue.Infrastructure.Prompts;
using OpenCodeQueue.Infrastructure.State;
using OpenCodeQueue.Infrastructure;

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
        Assert.All(fixture.OpenCode.Payloads, payload => Assert.StartsWith("msg", payload.MessageId, StringComparison.Ordinal));
        Assert.All(fixture.OpenCode.Payloads, payload => Assert.DoesNotContain(':', payload.MessageId));
        Assert.All(fixture.OpenCode.SessionIds, sessionId => Assert.Equal("session-1", sessionId));
        Assert.False(File.Exists(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md")));
        Assert.True(File.Exists(Path.Combine(fixture.ProjectDir, "quality", "01-review.md")));
        Assert.Single(Directory.EnumerateFiles(ProjectPaths.CompletedDir(fixture.Project), "*_01-task.md"));

        var state = await fixture.StateStore.LoadQueueStateAsync(fixture.Project, CancellationToken.None);
        Assert.Null(state!.ActiveRunId);
        var manifest = await fixture.StateStore.LoadRunManifestAsync(fixture.Project, state.LastCompletedRunId!, CancellationToken.None);
        Assert.Equal(RunStatus.Completed, manifest!.Status);
    }

    [Fact]
    public async Task RunQueueAsync_PrintsSimpleProgressLogs()
    {
        var reporter = new TestReporter();
        var fixture = await CreateFixtureAsync(reporter: reporter);
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "quality", "01-review.md"), "review one");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("Запущена задача: 01-task.md.", reporter.Messages);
        Assert.Contains("Запущен task: 01-task.md.", reporter.Messages);
        Assert.Contains("Завершён task: 01-task.md.", reporter.Messages);
        Assert.Contains("Запущен quality-01: 01-review.md.", reporter.Messages);
        Assert.Contains("Завершён quality-01: 01-review.md.", reporter.Messages);
        Assert.Contains("Задача завершена: 01-task.md.", reporter.Messages);
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
    public async Task ResumeAsync_WhenFailedBecauseMessageIdPayloadWasRejected_RetriesStepWithOpenCodeMessageId()
    {
        var fixture = await CreateFixtureAsync();
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");
        var discovered = await new FileSystemPromptRepository().DiscoverAsync(fixture.Project, CancellationToken.None);
        var task = discovered.TaskPrompts[0];
        var runId = "20260626-144202-e4d3f8c6838";
        await SaveActiveManifestAsync(fixture, runId, new RunManifest
        {
            RunId = runId,
            ProjectId = fixture.Project.Id,
            ProjectDirSnapshot = fixture.Project.ProjectDir,
            SessionId = "session-existing",
            Status = RunStatus.Failed,
            CurrentStepIndex = 0,
            LastError = "OpenCode server вернул HTTP 400 для POST /message: {\"name\":\"BadRequest\",\"data\":{\"message\":\"Expected a string starting with \\\"msg\\\", got \\\"20260626-144202-e4d3f8c6838:task:1\\\" at [\\\"messageID\\\"]\"}}",
            TaskDescriptor = task,
            Steps = [new WorkflowStep
            {
                Id = WorkflowStepId.Task,
                Kind = Core.Prompts.PromptKind.Task,
                SourcePath = task.Path,
                ContentHash = task.ContentHash,
                Status = WorkflowStepStatus.Failed,
                SessionMessageId = "20260626-144202-e4d3f8c6838:task:1",
                AttemptCount = 1
            }],
            CreatedAt = FixedNow,
            StartedAt = FixedNow,
            UpdatedAt = FixedNow
        });

        var result = await fixture.UseCases.ResumeAsync(fixture.ConfigPath, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var payload = Assert.Single(fixture.OpenCode.Payloads);
        Assert.StartsWith("msg", payload.MessageId, StringComparison.Ordinal);
        Assert.DoesNotContain(':', payload.MessageId);
        Assert.Equal("session-existing", fixture.OpenCode.SessionIds.Single());
        Assert.Equal(RunStatus.Completed, result.Manifest!.Status);
    }

    [Fact]
    public async Task ResumeAsync_WhenActiveRunningRun_SendsContinuationInSameSessionWithoutOriginalPrompt()
    {
        var fixture = await CreateFixtureAsync(settings: FastRecoverySettings());
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

        Assert.True(result.IsSuccess);
        var payload = Assert.Single(fixture.OpenCode.Payloads);
        Assert.Equal("task", payload.StepId);
        Assert.Equal("session-existing", fixture.OpenCode.SessionIds.Single());
        Assert.Contains("Продолжи", payload.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("task body", payload.Content, StringComparison.Ordinal);
        Assert.Equal(RunStatus.Completed, result.Manifest!.Status);
    }

    [Fact]
    public async Task ResumeAsync_WhenRunningStepLostSessionId_StopsForManualInterventionWithoutResendingPrompt()
    {
        var fixture = await CreateFixtureAsync();
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
    public async Task ResumeAsync_WhenManualStopWasIdleWait_ContinuesExistingPrompt()
    {
        var fixture = await CreateFixtureAsync();
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");
        var discovered = await new FileSystemPromptRepository().DiscoverAsync(fixture.Project, CancellationToken.None);
        var task = discovered.TaskPrompts[0];
        var runId = "run-idle-wait-manual";
        await SaveActiveManifestAsync(fixture, runId, new RunManifest
        {
            RunId = runId,
            ProjectId = fixture.Project.Id,
            ProjectDirSnapshot = fixture.Project.ProjectDir,
            SessionId = "session-existing",
            Status = RunStatus.NeedsManualIntervention,
            CurrentStepIndex = 0,
            LastError = "Не удалось дождаться Idle status перед continuation. Очередь остановлена, task prompt не архивирован.",
            TaskDescriptor = task,
            Steps = [new WorkflowStep
            {
                Id = WorkflowStepId.Task,
                Kind = Core.Prompts.PromptKind.Task,
                SourcePath = task.Path,
                ContentHash = task.ContentHash,
                Status = WorkflowStepStatus.Recovering,
                SessionMessageId = "msg-existing"
            }],
            CreatedAt = FixedNow,
            StartedAt = FixedNow,
            UpdatedAt = FixedNow
        });

        var result = await fixture.UseCases.ResumeAsync(fixture.ConfigPath, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(RunStatus.Completed, result.Manifest!.Status);
        Assert.Empty(fixture.OpenCode.SentStepIds);
        Assert.False(File.Exists(taskPath));
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

    [Fact]
    public async Task RunQueueAsync_PassesConfiguredPromptTransportToOpenCodeClient()
    {
        var fixture = await CreateFixtureAsync(settings: new OpenCodeSettings
        {
            PromptTransport = PromptTransport.FileAttachment,
            MaxInlinePromptChars = 7
        });
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var payload = Assert.Single(fixture.OpenCode.Payloads);
        Assert.Equal(PromptTransport.FileAttachment, payload.Transport);
        Assert.Equal(7, payload.MaxInlinePromptChars);
    }

    [Fact]
    public async Task RunQueueAsync_WhenOpenCodeProjectMismatch_DoesNotCreateRunOrSendPrompt()
    {
        var fixture = await CreateFixtureAsync(ensureReadyException: new OpenCodeProjectMismatchException("selected", "server"));
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "quality", "01-review.md"), "review");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(QueueExitCodes.OpenCodeUnavailableOrProjectMismatch, result.ExitCode);
        Assert.Empty(fixture.OpenCode.SentStepIds);
        Assert.True(File.Exists(taskPath));
        var state = await fixture.StateStore.LoadQueueStateAsync(fixture.Project, CancellationToken.None);
        Assert.Null(state!.ActiveRunId);
    }

    [Fact]
    public async Task RunQueueAsync_WhenQualityDirIsMissing_DoesNotStartOpenCode()
    {
        var fixture = await CreateFixtureAsync();
        Directory.Delete(Path.Combine(fixture.ProjectDir, "quality"), recursive: true);
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(QueueExitCodes.ValidationError, result.ExitCode);
        Assert.Contains("Папка quality", result.Messages.Single(), StringComparison.Ordinal);
        Assert.Empty(fixture.OpenCode.SentStepIds);
        Assert.True(File.Exists(taskPath));
    }

    [Fact]
    public async Task RunQueueAsync_DoesNotPersistServerPasswordInManifest()
    {
        var fixture = await CreateFixtureAsync(settings: new OpenCodeSettings { ServerPassword = "super-secret" });
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var manifestPath = ProjectPaths.RunManifestFile(fixture.Project, result.Manifest!.RunId);
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        Assert.DoesNotContain("super-secret", manifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQueueAsync_WhenTaskIsAborted_SendsContinuationBeforeQuality()
    {
        var fixture = await CreateFixtureAsync(settings: FastRecoverySettings(), scriptedResults: new Dictionary<string, Queue<OpenCodeMessageResult>>
        {
            ["task"] = new([Interrupted("Tool execution aborted"), Success("task-continuation")])
        });
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "quality", "01-review.md"), "review one");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["task", "task", "quality-01"], fixture.OpenCode.SentStepIds);
        Assert.Contains("Не начинай задачу заново", fixture.OpenCode.Payloads[1].Content, StringComparison.OrdinalIgnoreCase);
        var taskAttemptLogs = result.Manifest!.Steps[0].AttemptLogs;
        Assert.Equal(2, taskAttemptLogs.Count);
        Assert.All(taskAttemptLogs, log => Assert.True(File.Exists(log.MessageLogPath)));
        Assert.Single(Directory.EnumerateFiles(ProjectPaths.CompletedDir(fixture.Project), "*_01-task.md"));
    }

    [Fact]
    public async Task RunQueueAsync_WhenSessionBusyAfterInterruption_WaitsForIdleBeforeContinuation()
    {
        var fixture = await CreateFixtureAsync(settings: FastRecoverySettings(), scriptedResults: new Dictionary<string, Queue<OpenCodeMessageResult>>
        {
            ["task"] = new([Interrupted("Tool execution aborted"), Success("task-continuation")])
        }, statusResults: new Queue<OpenCodeSessionStatus>([
            new(OpenCodeSessionState.Busy),
            new(OpenCodeSessionState.Retry, "built-in retry"),
            new(OpenCodeSessionState.Idle)
        ]));
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["task", "task"], fixture.OpenCode.SentStepIds);
        Assert.Equal(3, fixture.OpenCode.StatusRequests);
    }

    [Fact]
    public async Task RunQueueAsync_WhenQualityIsTerminated_ArchivesOnlyAfterContinuationSuccess()
    {
        var fixture = await CreateFixtureAsync(settings: FastRecoverySettings(), scriptedResults: new Dictionary<string, Queue<OpenCodeMessageResult>>
        {
            ["quality-01"] = new([Interrupted("terminated"), Success("quality-continuation")])
        });
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "quality", "01-review.md"), "review one");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["task", "quality-01", "quality-01"], fixture.OpenCode.SentStepIds);
        Assert.False(File.Exists(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md")));
    }

    [Fact]
    public async Task RunQueueAsync_WhenSameInterruptionRepeats_StopsForManualIntervention()
    {
        var settings = FastRecoverySettings() with { Resilience = FastRecoverySettings().Resilience with { StopAfterSameSignatureRepeats = 1, MaxContinuationAttemptsPerStep = 5 } };
        var fixture = await CreateFixtureAsync(settings: settings, scriptedResults: new Dictionary<string, Queue<OpenCodeMessageResult>>
        {
            ["task"] = new([Interrupted("Tool execution aborted"), Interrupted("Tool execution aborted")])
        });
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RunStatus.NeedsManualIntervention, result.Manifest!.Status);
        Assert.True(File.Exists(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md")));
    }

    [Fact]
    public async Task RunQueueAsync_WhenTransportTimeoutThenSuccess_UsesContinuationNotOriginalPromptAgain()
    {
        var fixture = await CreateFixtureAsync(settings: FastRecoverySettings(), scriptedResults: new Dictionary<string, Queue<OpenCodeMessageResult>>
        {
            ["task"] = new([new OpenCodeMessageResult(false, ErrorMessage: "timeout", IsTransportError: true, IsTimeout: true), Success("after-timeout")])
        });
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["task", "task"], fixture.OpenCode.SentStepIds);
        Assert.DoesNotContain("task body", fixture.OpenCode.Payloads[1].Content, StringComparison.Ordinal);
        Assert.Contains("Продолжи", fixture.OpenCode.Payloads[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQueueAsync_WhenNeedsManualMarkerReturned_StopsQueue()
    {
        var fixture = await CreateFixtureAsync(settings: FastRecoverySettings(), scriptedResults: new Dictionary<string, Queue<OpenCodeMessageResult>>
        {
            ["task"] = new([new OpenCodeMessageResult(false, LastAssistantText: "NEEDS_MANUAL_INTERVENTION: нужен токен")])
        });
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RunStatus.NeedsManualIntervention, result.Manifest!.Status);
        Assert.Contains("нужен токен", result.Manifest.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQueueAsync_WhenPermissionRequestReturned_StopsForManualIntervention()
    {
        var fixture = await CreateFixtureAsync(settings: FastRecoverySettings(), scriptedResults: new Dictionary<string, Queue<OpenCodeMessageResult>>
        {
            ["task"] = new([new OpenCodeMessageResult(false, LastAssistantText: "permission request: approve shell command")])
        });
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RunStatus.NeedsManualIntervention, result.Manifest!.Status);
        Assert.Contains("разреш", result.Manifest.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md")));
    }

    [Fact]
    public async Task RunQueueAsync_WhenNonRecoverableOpenCodeError_DoesNotSendContinuation()
    {
        var fixture = await CreateFixtureAsync(settings: FastRecoverySettings(), scriptedResults: new Dictionary<string, Queue<OpenCodeMessageResult>>
        {
            ["task"] = new([new OpenCodeMessageResult(false, ErrorMessage: "Prompt-файл для attachment не найден")])
        });
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDir, "prompts", "01-task.md"), "task body");

        var result = await fixture.UseCases.RunQueueAsync(fixture.ConfigPath, null, once: true, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RunStatus.Failed, result.Manifest!.Status);
        Assert.Equal(["task"], fixture.OpenCode.SentStepIds);
    }

    [Fact]
    public async Task ResumeAsync_WhenSavedSessionCannotBeFound_StopsForManualIntervention()
    {
        var fixture = await CreateFixtureAsync(getSessionException: new OpenCodeClientException("OpenCode server вернул HTTP 404 для GET /session/session-existing: not found"));
        var taskPath = Path.Combine(fixture.ProjectDir, "prompts", "01-task.md");
        await File.WriteAllTextAsync(taskPath, "task body");
        var discovered = await new FileSystemPromptRepository().DiscoverAsync(fixture.Project, CancellationToken.None);
        var task = discovered.TaskPrompts[0];
        var runId = "run-lost-session-id";
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
        Assert.Equal(RunStatus.NeedsManualIntervention, result.Manifest!.Status);
        Assert.Empty(fixture.OpenCode.SentStepIds);
        Assert.Contains("Сессия OpenCode недоступна", result.Manifest.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbortRunAsync_WhenOpenCodeAbortFails_StillClearsActiveRunLocally()
    {
        var fixture = await CreateFixtureAsync(abortSessionException: new OpenCodeClientException("server down"));
        var runId = "run-abort";
        await SaveActiveManifestAsync(fixture, runId, new RunManifest
        {
            RunId = runId,
            ProjectId = fixture.Project.Id,
            ProjectDirSnapshot = fixture.Project.ProjectDir,
            SessionId = "session-existing",
            Status = RunStatus.Running,
            CreatedAt = FixedNow,
            StartedAt = FixedNow,
            UpdatedAt = FixedNow
        });

        var result = await fixture.UseCases.AbortRunAsync(fixture.ConfigPath, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Messages, message => message.Contains("локально", StringComparison.Ordinal));
        var state = await fixture.StateStore.LoadQueueStateAsync(fixture.Project, CancellationToken.None);
        Assert.Null(state!.ActiveRunId);
        var manifest = await fixture.StateStore.LoadRunManifestAsync(fixture.Project, runId, CancellationToken.None);
        Assert.Equal(RunStatus.Aborted, manifest!.Status);
        Assert.Contains("server down", manifest.LastError, StringComparison.Ordinal);
    }

    private static async Task<Fixture> CreateFixtureAsync(string? failStepId = null, bool changeTaskBeforeArchive = false, bool stopOnQualityFailure = true, OpenCodeSettings? settings = null, Exception? ensureReadyException = null, Exception? abortSessionException = null, Exception? getSessionException = null, Dictionary<string, Queue<OpenCodeMessageResult>>? scriptedResults = null, Queue<OpenCodeSessionStatus>? statusResults = null, IConsoleReporter? reporter = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(root, "project");
        var configPath = Path.Combine(root, "opencode-queue.json");
        var configStore = new JsonAppConfigStore();
        var registry = new JsonProjectRegistry(configStore);
        var project = new ProjectProfile { Id = "project-a", ProjectDir = projectDir, StopOnQualityFailure = stopOnQualityFailure, OpenCodeOverrides = settings ?? new OpenCodeSettings() };
        Directory.CreateDirectory(ProjectPaths.PromptsDir(project));
        Directory.CreateDirectory(ProjectPaths.QualityDir(project));
        await registry.AddOrUpdateAsync(configPath, project, CancellationToken.None);
        var stateStore = new JsonStateStore();
        var openCode = new FakeOpenCodeClient(failStepId, changeTaskBeforeArchive, ensureReadyException, abortSessionException, getSessionException, scriptedResults, statusResults);
        var useCases = new QueueUseCases(
            registry,
            new FileSystemPromptRepository(),
            stateStore,
            new FileRunLock(new FixedClock()),
            openCode,
            new RunWorkspace(),
            new FileSystemArchiver(),
            new FixedClock(),
            new OpenCodeStepResultClassifier(),
            reporter);
        return new Fixture(configPath, ProjectPaths.QueueDir(project), project, stateStore, openCode, useCases);
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

    private static OpenCodeSettings FastRecoverySettings() => new()
    {
        Resilience = new ResilienceSettings
        {
            RetryDelaySeconds = 0,
            MaxContinuationAttemptsPerStep = 3,
            StopAfterSameSignatureRepeats = 3,
            MaxTransportRetriesPerAttempt = 3
        }
    };

    private static OpenCodeMessageResult Interrupted(string text) => new(false, ErrorMessage: text, Stderr: text, ExitCode: 1);

    private static OpenCodeMessageResult Success(string messageId) => new(true, messageId, true);

    private sealed class FakeOpenCodeClient(string? failStepId, bool changeTaskBeforeArchive, Exception? ensureReadyException, Exception? abortSessionException, Exception? getSessionException, Dictionary<string, Queue<OpenCodeMessageResult>>? scriptedResults, Queue<OpenCodeSessionStatus>? statusResults) : IOpenCodeClient
    {
        private readonly Dictionary<string, Queue<OpenCodeMessageResult>> scripts = scriptedResults ?? [];
        private readonly Queue<OpenCodeSessionStatus> statuses = statusResults ?? [];

        public List<string> SentStepIds { get; } = [];

        public List<string> SessionIds { get; } = [];

        public List<PromptPayload> Payloads { get; } = [];

        public int StatusRequests { get; private set; }

        public Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken)
        {
            return ensureReadyException is null ? Task.CompletedTask : Task.FromException(ensureReadyException);
        }

        public Task<OpenCodeSession> StartSessionAsync(ProjectProfile project, string title, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeSession("session-1", project.ProjectDir, title));
        }

        public Task<OpenCodeMessageResult> SendPromptAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken)
        {
            return RecordAsync(project, sessionId, payload, cancellationToken).ContinueWith(_ => Result(payload), cancellationToken);
        }

        public Task<OpenCodeMessageResult> WaitForPromptAsync(ProjectProfile project, string sessionId, string messageId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeMessageResult(true, messageId));
        }

        public Task<OpenCodeSessionDetails> GetSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
        {
            return getSessionException is null
                ? Task.FromResult(new OpenCodeSessionDetails(new OpenCodeSession(sessionId, project.ProjectDir), new OpenCodeSessionStatus(OpenCodeSessionState.Idle), []))
                : Task.FromException<OpenCodeSessionDetails>(getSessionException);
        }

        public Task<OpenCodeSessionStatus> GetSessionStatusAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
        {
            StatusRequests++;
            return Task.FromResult(statuses.Count > 0 ? statuses.Dequeue() : new OpenCodeSessionStatus(OpenCodeSessionState.Idle));
        }

        public Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
        {
            return abortSessionException is null ? Task.CompletedTask : Task.FromException(abortSessionException);
        }

        private async Task RecordAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken)
        {
            SentStepIds.Add(payload.StepId!);
            SessionIds.Add(sessionId);
            Payloads.Add(payload);
            if (changeTaskBeforeArchive && payload.StepId == "task")
            {
                await File.WriteAllTextAsync(Path.Combine(ProjectPaths.PromptsDir(project), "01-task.md"), "changed", cancellationToken);
            }
        }

        private OpenCodeMessageResult Result(PromptPayload payload)
        {
            var scripted = TryDequeueScripted(payload.StepId);
            if (scripted is not null)
            {
                return scripted;
            }

            return string.Equals(payload.StepId, failStepId, StringComparison.Ordinal)
                ? new OpenCodeMessageResult(false, ErrorMessage: "fake failure")
                : new OpenCodeMessageResult(true, payload.MessageId);
        }

        private OpenCodeMessageResult? TryDequeueScripted(string? stepId)
        {
            if (stepId is not null && scripts.TryGetValue(stepId, out var exact) && exact.Count > 0)
            {
                return exact.Dequeue();
            }

            return null;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => FixedNow;
    }

    private sealed class TestReporter : IConsoleReporter
    {
        public List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add(message);

        public void Warning(string message) => Messages.Add(message);

        public void Error(string message) => Messages.Add(message);

        public string? ReadLine(string prompt) => null;
    }

    private static DateTimeOffset FixedNow => DateTimeOffset.Parse("2026-06-26T10:00:00Z");
}
