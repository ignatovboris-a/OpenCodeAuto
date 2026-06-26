using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Prompts;
using OpenCodeQueue.Core.State;

namespace OpenCodeQueue.Core.Workflow;

public sealed class QueueUseCases(
    IProjectRegistry projectRegistry,
    IPromptRepository promptRepository,
    IStateStore stateStore,
    IRunLock runLock,
    IOpenCodeClient openCodeClient,
    IRunWorkspace runWorkspace,
    IFileArchiver fileArchiver,
    IClock clock) : IQueueUseCases
{
    public async Task<QueueOperationResult> RunQueueAsync(string configPath, string? projectId, bool once, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(configPath, projectId, cancellationToken);
        if (project is null)
        {
            return QueueOperationResult.Failure(QueueExitCodes.ValidationError, MissingProjectMessage(projectId));
        }

        await using var acquiredLock = await AcquireLockAsync(project, cancellationToken);
        if (acquiredLock is null)
        {
            return QueueOperationResult.Failure(QueueExitCodes.ActiveRunBlocksNewRun, "Не удалось получить lock проекта. Очередь уже запущена или требуется recovery.") with { Project = project };
        }

        var messages = new List<string>();
        while (true)
        {
            var state = await LoadOrCreateStateAsync(project, cancellationToken);
            if (!string.IsNullOrWhiteSpace(state.ActiveRunId))
            {
                return new QueueOperationResult
                {
                    IsSuccess = false,
                    ExitCode = QueueExitCodes.ActiveRunBlocksNewRun,
                    Project = project,
                    State = state,
                    Messages = [.. messages, $"В проекте уже есть активный run: {state.ActiveRunId}. Новая задача не будет выбрана; используйте resume/status/abort."]
                };
            }

            var discovery = await promptRepository.DiscoverAsync(project, cancellationToken);
            if (discovery.TaskPrompts.Count == 0)
            {
                return new QueueOperationResult
                {
                    Project = project,
                    State = state,
                    Discovery = discovery,
                    Messages = [.. messages, "Очередь задач пуста."]
                };
            }

            var manifest = await CreateRunAsync(project, state, discovery.TaskPrompts[0], discovery.QualityPrompts, cancellationToken);
            var completed = await ContinueRunAsync(project, manifest, cancellationToken);
            messages.Add($"Run {completed.RunId} завершён со статусом: {completed.Status}.");

            if (completed.Status != RunStatus.Completed)
            {
                return new QueueOperationResult
                {
                    IsSuccess = false,
                    ExitCode = QueueExitCodes.WorkflowStepFailed,
                    Project = project,
                    Manifest = completed,
                    Discovery = discovery,
                    Messages = messages
                };
            }

            if (once)
            {
                return new QueueOperationResult
                {
                    Project = project,
                    Manifest = completed,
                    Discovery = discovery,
                    Messages = [.. messages, "Режим --once: выполнена одна задача."]
                };
            }
        }
    }

    public async Task<QueueOperationResult> ResumeAsync(string configPath, string? projectId, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(configPath, projectId, cancellationToken);
        if (project is null)
        {
            return QueueOperationResult.Failure(QueueExitCodes.ValidationError, MissingProjectMessage(projectId));
        }

        await using var acquiredLock = await AcquireLockAsync(project, cancellationToken);
        if (acquiredLock is null)
        {
            return QueueOperationResult.Failure(QueueExitCodes.ActiveRunBlocksNewRun, "Не удалось получить lock проекта. Очередь уже запущена или требуется ручная проверка.") with { Project = project };
        }

        var state = await stateStore.LoadQueueStateAsync(project, cancellationToken);
        if (string.IsNullOrWhiteSpace(state?.ActiveRunId))
        {
            return QueueOperationResult.Success("В выбранном проекте нет активного run для восстановления.") with { Project = project, State = state };
        }

        var manifest = await stateStore.LoadRunManifestAsync(project, state.ActiveRunId, cancellationToken);
        if (manifest is null)
        {
            var manual = await SaveManualRunAsync(project, state.ActiveRunId, "manifest.json отсутствует", cancellationToken);
            return QueueOperationResult.Failure(QueueExitCodes.ValidationError, $"manifest.json для activeRunId '{state.ActiveRunId}' отсутствует. Новая задача не выбирается; проверьте .queue/runs вручную.") with { Project = project, State = state, Manifest = manual };
        }

        await AppendEventAsync(project, QueueEventTypes.RecoveryStarted, manifest.RunId, null, manifest.SessionId, null, "Resume active run", cancellationToken);
        manifest = manifest with { RecoveryAttempts = manifest.RecoveryAttempts + 1, UpdatedAt = clock.Now };
        await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);

        var completed = await ContinueRunAsync(project, manifest, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.RecoveryCompleted, completed.RunId, null, completed.SessionId, completed.TaskDescriptor?.FileName, completed.Status.ToString(), cancellationToken);
        return new QueueOperationResult
        {
            IsSuccess = completed.Status == RunStatus.Completed,
            ExitCode = completed.Status == RunStatus.Completed ? QueueExitCodes.Success : QueueExitCodes.WorkflowStepFailed,
            Project = project,
            State = await stateStore.LoadQueueStateAsync(project, cancellationToken),
            Manifest = completed,
            Messages = [$"Resume завершён со статусом run: {completed.Status}."]
        };
    }

    public async Task<QueueOperationResult> GetStatusAsync(string configPath, string? projectId, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(configPath, projectId, cancellationToken);
        if (project is null)
        {
            return QueueOperationResult.Failure(QueueExitCodes.ValidationError, MissingProjectMessage(projectId));
        }

        var discovery = await promptRepository.DiscoverAsync(project, cancellationToken);
        var state = await stateStore.LoadQueueStateAsync(project, cancellationToken);
        RunManifest? manifest = null;
        if (!string.IsNullOrWhiteSpace(state?.ActiveRunId))
        {
            manifest = await stateStore.LoadRunManifestAsync(project, state.ActiveRunId, cancellationToken);
        }

        return new QueueOperationResult
        {
            Project = project,
            Discovery = discovery,
            State = state,
            Manifest = manifest,
            Messages = [$"Задач: {discovery.TaskPrompts.Count}; quality prompts: {discovery.QualityPrompts.Count}; активный run: {state?.ActiveRunId ?? "нет"}."]
        };
    }

    public async Task<QueueOperationResult> ListPromptsAsync(string configPath, string? projectId, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(configPath, projectId, cancellationToken);
        if (project is null)
        {
            return QueueOperationResult.Failure(QueueExitCodes.ValidationError, MissingProjectMessage(projectId));
        }

        var discovery = await promptRepository.DiscoverAsync(project, cancellationToken);
        return new QueueOperationResult { Project = project, Discovery = discovery };
    }

    public async Task<QueueOperationResult> AbortRunAsync(string configPath, string? projectId, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(configPath, projectId, cancellationToken);
        if (project is null)
        {
            return QueueOperationResult.Failure(QueueExitCodes.ValidationError, MissingProjectMessage(projectId));
        }

        await using var acquiredLock = await AcquireLockAsync(project, cancellationToken);
        if (acquiredLock is null)
        {
            return QueueOperationResult.Failure(QueueExitCodes.ActiveRunBlocksNewRun, "Не удалось получить lock проекта.") with { Project = project };
        }

        var state = await stateStore.LoadQueueStateAsync(project, cancellationToken);
        if (string.IsNullOrWhiteSpace(state?.ActiveRunId))
        {
            return QueueOperationResult.Success("В выбранном проекте нет активного run.") with { Project = project, State = state };
        }

        var manifest = await stateStore.LoadRunManifestAsync(project, state.ActiveRunId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(manifest?.SessionId))
        {
            await openCodeClient.AbortSessionAsync(project, manifest.SessionId, cancellationToken);
        }

        var now = clock.Now;
        if (manifest is not null)
        {
            manifest = manifest with { Status = RunStatus.Aborted, UpdatedAt = now, FinishedAt = now };
            await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
        }

        await stateStore.SaveQueueStateAsync(project, state with { ActiveRunId = null, UpdatedAt = now }, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.RunAborted, state.ActiveRunId, null, manifest?.SessionId, manifest?.TaskDescriptor?.FileName, null, cancellationToken);
        return QueueOperationResult.Success("Run переведён в Aborted. Данные сохранены, task prompt не архивирован автоматически.") with { Project = project, Manifest = manifest };
    }

    private async Task<RunManifest> CreateRunAsync(ProjectProfile project, QueueState state, PromptDescriptor task, IReadOnlyList<PromptDescriptor> qualityPrompts, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var runId = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..27];
        var steps = new List<WorkflowStep>();
        var taskSnapshot = await runWorkspace.SnapshotPromptAsync(project, runId, task, "task-" + task.FileName, cancellationToken);
        steps.Add(new WorkflowStep
        {
            Id = WorkflowStepId.Task,
            Kind = PromptKind.Task,
            SourcePath = task.Path,
            SnapshotPath = taskSnapshot,
            ContentHash = task.ContentHash,
            Order = 0
        });

        for (var index = 0; index < qualityPrompts.Count; index++)
        {
            var quality = qualityPrompts[index];
            var snapshot = await runWorkspace.SnapshotPromptAsync(project, runId, quality, $"quality-{index + 1:00}-" + quality.FileName, cancellationToken);
            steps.Add(new WorkflowStep
            {
                Id = WorkflowStepId.Quality(index + 1),
                Kind = PromptKind.Quality,
                SourcePath = quality.Path,
                SnapshotPath = snapshot,
                ContentHash = quality.ContentHash,
                Order = index + 1
            });
        }

        var manifest = new RunManifest
        {
            RunId = runId,
            ProjectId = project.Id,
            ProjectDirSnapshot = project.ProjectDir,
            PromptsDirSnapshot = project.PromptsDir,
            QualityDirSnapshot = project.QualityDir ?? project.ReviewsDir,
            OpenCodeSettingsSnapshot = project.OpenCodeOverrides,
            TaskDescriptor = task,
            Steps = steps,
            Status = RunStatus.Pending,
            ArchiveStatus = ArchiveStatus.NotStarted,
            CreatedAt = now,
            StartedAt = now,
            UpdatedAt = now
        };

        await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
        await stateStore.SaveQueueStateAsync(project, state with { ActiveRunId = runId, UpdatedAt = now }, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.RunCreated, runId, null, null, task.FileName, null, cancellationToken);
        return manifest;
    }

    private async Task<RunManifest> ContinueRunAsync(ProjectProfile project, RunManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.Status == RunStatus.CompletedPendingArchive)
        {
            return await ArchiveCompletedRunAsync(project, manifest, cancellationToken);
        }

        if (manifest.Status is RunStatus.Failed or RunStatus.Aborted or RunStatus.NeedsManualIntervention or RunStatus.Completed)
        {
            return manifest;
        }

        manifest = manifest with { Status = RunStatus.Running, UpdatedAt = clock.Now };
        await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);

        for (var index = FirstIncompleteStepIndex(manifest); index < manifest.Steps.Count; index++)
        {
            var step = manifest.Steps[index];
            if (step.Status == WorkflowStepStatus.Completed || step.Status == WorkflowStepStatus.Skipped)
            {
                continue;
            }

            if (step.Status is WorkflowStepStatus.Running or WorkflowStepStatus.Recovering)
            {
                if (string.IsNullOrWhiteSpace(manifest.SessionId))
                {
                    return await MarkManualAsync(project, manifest, "manifest не содержит sessionId для незавершённого running step; автоматическое восстановление остановлено, чтобы не повторить prompt в новой session.", cancellationToken);
                }

                var recovered = await openCodeClient.TryRecoverStepAsync(project, manifest, step, cancellationToken);
                if (recovered.Outcome == StepRecoveryOutcome.Completed)
                {
                    manifest = await MarkStepCompletedAsync(project, manifest, index, step.SessionMessageId, cancellationToken);
                    continue;
                }

                if (recovered.Outcome == StepRecoveryOutcome.Failed)
                {
                    return await MarkStepFailedAsync(project, manifest, index, recovered.Message ?? "OpenCode сообщил об ошибке шага.", cancellationToken);
                }

                if (recovered.Outcome == StepRecoveryOutcome.ConservativeContinueSent)
                {
                    return await MarkRecoveryPendingAsync(project, manifest, index, recovered.Message ?? "Отправлен recovery prompt; исходный prompt повторно не отправлялся.", cancellationToken);
                }
            }

            manifest = await SendStepAsync(project, manifest, index, cancellationToken);
            if (manifest.Status == RunStatus.Failed)
            {
                if (step.Kind == PromptKind.Quality && !project.StopOnQualityFailure)
                {
                    manifest = manifest with { Status = RunStatus.Running, UpdatedAt = clock.Now };
                    await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
                    continue;
                }

                return manifest;
            }
        }

        if (manifest.Steps.Any(step => step.Status == WorkflowStepStatus.Failed))
        {
            var failed = manifest with { Status = RunStatus.Failed, LastError = "Один или несколько quality steps завершились ошибкой.", UpdatedAt = clock.Now };
            await stateStore.SaveRunManifestAsync(project, failed, cancellationToken);
            return failed;
        }

        var pendingArchive = manifest with
        {
            Status = RunStatus.CompletedPendingArchive,
            ArchiveStatus = ArchiveStatus.Pending,
            CurrentStepIndex = manifest.Steps.Count,
            UpdatedAt = clock.Now
        };
        await stateStore.SaveRunManifestAsync(project, pendingArchive, cancellationToken);
        return await ArchiveCompletedRunAsync(project, pendingArchive, cancellationToken);
    }

    private async Task<RunManifest> SendStepAsync(ProjectProfile project, RunManifest manifest, int stepIndex, CancellationToken cancellationToken)
    {
        var step = manifest.Steps[stepIndex];
        var running = step with { Status = WorkflowStepStatus.Running, StartedAt = step.StartedAt ?? clock.Now, AttemptCount = step.AttemptCount + 1 };
        manifest = ReplaceStep(manifest, stepIndex, running) with { CurrentStepIndex = stepIndex, Status = RunStatus.Running, UpdatedAt = clock.Now };
        await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.StepStarted, manifest.RunId, running.Id.Value, manifest.SessionId, manifest.TaskDescriptor?.FileName, null, cancellationToken);

        try
        {
            var payload = await BuildPayloadAsync(manifest, running, cancellationToken);
            OpenCodeMessageResult result;
            string? sessionId = manifest.SessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                var title = $"{manifest.RunId} {project.Id} {manifest.TaskDescriptor?.FileName}";
                var session = await openCodeClient.StartSessionAsync(project, title, cancellationToken);
                sessionId = session.SessionId;
                manifest = manifest with { SessionId = sessionId, UpdatedAt = clock.Now };
                await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
                await AppendEventAsync(project, QueueEventTypes.SessionCreated, manifest.RunId, running.Id.Value, sessionId, manifest.TaskDescriptor?.FileName, null, cancellationToken);
                result = await openCodeClient.SendPromptAsync(project, sessionId, payload, cancellationToken);
            }
            else
            {
                result = await openCodeClient.SendPromptAsync(project, sessionId, payload, cancellationToken);
            }

            if (!result.IsSuccess)
            {
                return await MarkStepFailedAsync(project, manifest, stepIndex, result.ErrorMessage ?? "OpenCode не выполнил prompt успешно.", cancellationToken);
            }

            return await MarkStepCompletedAsync(project, manifest, stepIndex, result.MessageId, cancellationToken);
        }
        catch (Exception exception) when (exception is OpenCodeClientException or IOException or InvalidOperationException)
        {
            return await MarkStepFailedAsync(project, manifest, stepIndex, exception.Message, cancellationToken);
        }
    }

    private async Task<PromptPayload> BuildPayloadAsync(RunManifest manifest, WorkflowStep step, CancellationToken cancellationToken)
    {
        var path = step.SnapshotPath ?? step.SourcePath;
        var content = await promptRepository.ReadPromptTextAsync(path, cancellationToken);
        return new PromptPayload
        {
            Content = content,
            SourcePath = path,
            MessageId = $"{manifest.RunId}:{step.Id.Value}:{step.AttemptCount}",
            RunId = manifest.RunId,
            StepId = step.Id.Value
        };
    }

    private async Task<RunManifest> MarkStepCompletedAsync(ProjectProfile project, RunManifest manifest, int stepIndex, string? messageId, CancellationToken cancellationToken)
    {
        var step = manifest.Steps[stepIndex];
        var completed = step with { Status = WorkflowStepStatus.Completed, SessionMessageId = messageId ?? step.SessionMessageId, CompletedAt = clock.Now };
        manifest = ReplaceStep(manifest, stepIndex, completed) with { CurrentStepIndex = stepIndex + 1, UpdatedAt = clock.Now, LastError = null };
        await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.StepCompleted, manifest.RunId, completed.Id.Value, manifest.SessionId, manifest.TaskDescriptor?.FileName, null, cancellationToken);
        return manifest;
    }

    private async Task<RunManifest> MarkStepFailedAsync(ProjectProfile project, RunManifest manifest, int stepIndex, string error, CancellationToken cancellationToken)
    {
        var step = manifest.Steps[stepIndex];
        var failed = step with { Status = WorkflowStepStatus.Failed };
        var failedManifest = ReplaceStep(manifest, stepIndex, failed) with { Status = RunStatus.Failed, CurrentStepIndex = stepIndex, LastError = error, UpdatedAt = clock.Now };
        await stateStore.SaveRunManifestAsync(project, failedManifest, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.StepFailed, manifest.RunId, failed.Id.Value, manifest.SessionId, manifest.TaskDescriptor?.FileName, error, cancellationToken);
        return failedManifest;
    }

    private async Task<RunManifest> MarkRecoveryPendingAsync(ProjectProfile project, RunManifest manifest, int stepIndex, string message, CancellationToken cancellationToken)
    {
        var step = manifest.Steps[stepIndex];
        var recovering = step with { Status = WorkflowStepStatus.Recovering };
        var recoveryManifest = ReplaceStep(manifest, stepIndex, recovering) with
        {
            Status = RunStatus.Running,
            CurrentStepIndex = stepIndex,
            LastError = message,
            UpdatedAt = clock.Now
        };
        await stateStore.SaveRunManifestAsync(project, recoveryManifest, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.RecoveryCompleted, manifest.RunId, recovering.Id.Value, manifest.SessionId, manifest.TaskDescriptor?.FileName, message, cancellationToken);
        return recoveryManifest;
    }

    private async Task<RunManifest> ArchiveCompletedRunAsync(ProjectProfile project, RunManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.TaskDescriptor is null)
        {
            return await MarkManualAsync(project, manifest, "manifest не содержит task descriptor; архивирование невозможно.", cancellationToken);
        }

        var result = await fileArchiver.ArchiveCompletedTaskAsync(project, manifest.TaskDescriptor, manifest.TaskDescriptor.ContentHash, clock.Now, cancellationToken);
        if (!result.IsSuccess)
        {
            var pending = manifest with { Status = RunStatus.CompletedPendingArchive, ArchiveStatus = ArchiveStatus.Failed, LastError = result.ErrorMessage, UpdatedAt = clock.Now };
            await stateStore.SaveRunManifestAsync(project, pending, cancellationToken);
            return pending;
        }

        var now = clock.Now;
        var completed = manifest with { Status = RunStatus.Completed, ArchiveStatus = ArchiveStatus.Completed, UpdatedAt = now, FinishedAt = now, LastError = null };
        var state = await LoadOrCreateStateAsync(project, cancellationToken);
        await stateStore.SaveRunManifestAsync(project, completed, cancellationToken);
        await stateStore.SaveQueueStateAsync(project, state with { ActiveRunId = null, LastCompletedRunId = completed.RunId, LastCompletedTaskFile = manifest.TaskDescriptor.FileName, UpdatedAt = now }, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.TaskArchived, completed.RunId, null, completed.SessionId, manifest.TaskDescriptor.FileName, result.ArchivedPath, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.RunCompleted, completed.RunId, null, completed.SessionId, manifest.TaskDescriptor.FileName, null, cancellationToken);
        return completed;
    }

    private async Task<RunManifest> MarkManualAsync(ProjectProfile project, RunManifest manifest, string error, CancellationToken cancellationToken)
    {
        var manual = manifest with { Status = RunStatus.NeedsManualIntervention, LastError = error, UpdatedAt = clock.Now };
        await stateStore.SaveRunManifestAsync(project, manual, cancellationToken);
        return manual;
    }

    private async Task<RunManifest> SaveManualRunAsync(ProjectProfile project, string runId, string error, CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var manifest = new RunManifest
        {
            RunId = runId,
            ProjectId = project.Id,
            ProjectDirSnapshot = project.ProjectDir,
            Status = RunStatus.NeedsManualIntervention,
            LastError = error,
            CreatedAt = now,
            StartedAt = now,
            UpdatedAt = now
        };
        await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
        return manifest;
    }

    private async Task<QueueState> LoadOrCreateStateAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadQueueStateAsync(project, cancellationToken);
        if (state is not null)
        {
            return state;
        }

        var now = clock.Now;
        state = new QueueState { ProjectId = project.Id, ProjectDirSnapshot = project.ProjectDir, CreatedAt = now, UpdatedAt = now };
        await stateStore.SaveQueueStateAsync(project, state, cancellationToken);
        return state;
    }

    private async Task<ProjectProfile?> ResolveProjectAsync(string configPath, string? projectId, CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(projectId)
            ? await projectRegistry.GetActiveAsync(configPath, cancellationToken)
            : await projectRegistry.GetByIdAsync(configPath, projectId, cancellationToken);
    }

    private async Task<IAsyncDisposable?> AcquireLockAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var result = await runLock.TryAcquireAsync(project, cancellationToken);
        return result.Releaser;
    }

    private async Task AppendEventAsync(ProjectProfile project, string type, string? runId, string? stepId, string? sessionId, string? taskFile, string? message, CancellationToken cancellationToken)
    {
        await stateStore.AppendEventAsync(project, new QueueEvent
        {
            Type = type,
            ProjectId = project.Id,
            RunId = runId,
            StepId = stepId,
            SessionId = sessionId,
            TaskFile = taskFile,
            Message = message,
            CreatedAt = clock.Now
        }, cancellationToken);
    }

    private static RunManifest ReplaceStep(RunManifest manifest, int stepIndex, WorkflowStep step)
    {
        var steps = manifest.Steps.ToArray();
        steps[stepIndex] = step;
        return manifest with { Steps = steps };
    }

    private static int FirstIncompleteStepIndex(RunManifest manifest)
    {
        for (var index = 0; index < manifest.Steps.Count; index++)
        {
            if (manifest.Steps[index].Status != WorkflowStepStatus.Completed && manifest.Steps[index].Status != WorkflowStepStatus.Skipped)
            {
                return index;
            }
        }

        return manifest.Steps.Count;
    }

    private static string MissingProjectMessage(string? projectId)
    {
        return string.IsNullOrWhiteSpace(projectId)
            ? "Проект не выбран. Используйте меню или команду project select/add."
            : $"Проект '{projectId}' не найден. activeProjectId не изменён.";
    }
}
