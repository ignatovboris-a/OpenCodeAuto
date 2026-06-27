using System.Security.Cryptography;
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
    IClock clock,
    IOpenCodeRunClassifier classifier,
    IConsoleReporter? reporter = null) : IQueueUseCases
{
    private const string OpenCodeIdentifierChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly Lock OpenCodeMessageIdLock = new();
    private static long lastOpenCodeMessageTimestamp;
    private static long openCodeMessageCounter;

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
            if (HasBlockingDiscoveryWarning(discovery, out var warning))
            {
                return QueueOperationResult.Failure(QueueExitCodes.ValidationError, warning) with { Project = project, State = state, Discovery = discovery };
            }

            if (discovery.TaskPrompts.Count == 0)
            {
                return new QueueOperationResult
                {
                    Project = project,
                    State = state,
                    Discovery = discovery,
                    Messages = [.. messages, .. discovery.Warnings, "Очередь задач пуста."]
                };
            }

            var ready = await EnsureOpenCodeReadyAsync(project, state, null, cancellationToken);
            if (ready is not null)
            {
                return ready with { Discovery = discovery };
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
            return QueueOperationResult.Failure(QueueExitCodes.ValidationError, $"manifest.json для activeRunId '{state.ActiveRunId}' отсутствует. Новая задача не выбирается; проверьте .opencodequeue/runs вручную.") with { Project = project, State = state, Manifest = manual };
        }

        var ready = await EnsureOpenCodeReadyAsync(project, state, manifest, cancellationToken);
        if (ready is not null)
        {
            return ready;
        }

        await AppendEventAsync(project, QueueEventTypes.RecoveryStarted, manifest.RunId, null, manifest.SessionId, null, "Resume active run", cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.ActiveRunRecoveredAfterRestart, manifest.RunId, null, manifest.SessionId, manifest.TaskDescriptor?.FileName, "Активный run найден после рестарта; новая задача не запускается.", cancellationToken);
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
        string? abortWarning = null;
        if (!string.IsNullOrWhiteSpace(manifest?.SessionId))
        {
            try
            {
                await openCodeClient.AbortSessionAsync(project, manifest.SessionId, cancellationToken);
            }
            catch (Exception exception) when (exception is OpenCodeClientException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                abortWarning = "OpenCode session не удалось abort через adapter: " + exception.Message;
            }
        }

        var now = clock.Now;
        if (manifest is not null)
        {
            manifest = manifest with { Status = RunStatus.Aborted, LastError = abortWarning, UpdatedAt = now, FinishedAt = now };
            await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
        }

        await stateStore.SaveQueueStateAsync(project, state with { ActiveRunId = null, UpdatedAt = now }, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.RunAborted, state.ActiveRunId, null, manifest?.SessionId, manifest?.TaskDescriptor?.FileName, abortWarning, cancellationToken);
        var messages = abortWarning is null
            ? new[] { "Run переведён в Aborted. Данные сохранены, task prompt не архивирован автоматически." }
            : new[] { "Run переведён в Aborted локально. Данные сохранены, task prompt не архивирован автоматически.", abortWarning };
        return QueueOperationResult.Success(messages) with { Project = project, Manifest = manifest };
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
            OpenCodeSettingsSnapshot = project.OpenCodeOverrides.Redacted(),
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

        if (manifest.Status == RunStatus.Failed && IsMessageIdPayloadValidationFailure(manifest))
        {
            manifest = await ResetFailedMessageIdPayloadValidationStepAsync(project, manifest, cancellationToken);
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

                var sessionId = manifest.SessionId;

                try
                {
                    await openCodeClient.GetSessionAsync(project, sessionId, cancellationToken);
                }
                catch (Exception exception) when (exception is OpenCodeClientException or IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    return await MarkManualAsync(project, manifest, "Сессия OpenCode недоступна или потеряна окончательно: " + exception.Message, cancellationToken);
                }

                var recoveryStep = step with { Status = WorkflowStepStatus.Recovering, LastProgressAt = clock.Now };
                manifest = ReplaceStep(manifest, index, recoveryStep) with { CurrentStepIndex = index, CurrentLogicalStepStatus = recoveryStep.Status.ToString(), UpdatedAt = clock.Now };
                await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
                await AppendEventAsync(project, QueueEventTypes.ActiveRunRecoveredAfterRestart, manifest.RunId, recoveryStep.Id.Value, manifest.SessionId, manifest.TaskDescriptor?.FileName, $"Сессия восстановлена, продолжаю текущий {recoveryStep.Kind.ToString().ToLowerInvariant()} prompt: {Path.GetFileName(recoveryStep.SourcePath)}.", cancellationToken);
                var inFlightMessageId = recoveryStep.RecoveryMessageId ?? recoveryStep.SessionMessageId;
                if (!string.IsNullOrWhiteSpace(inFlightMessageId))
                {
                    var inFlightPayload = (await BuildPayloadAsync(manifest, recoveryStep, cancellationToken)) with { MessageId = inFlightMessageId };
                    manifest = await SendLogicalStepWithRecoveryAsync(project, manifest, index, sessionId, inFlightPayload, isContinuation: recoveryStep.RecoveryMessageId is not null, cancellationToken: cancellationToken, sendFirst: false);
                }
                else
                {
                    var continuationPayload = BuildContinuationPayload(manifest, recoveryStep);
                    manifest = await SendLogicalStepWithRecoveryAsync(project, manifest, index, sessionId, continuationPayload, isContinuation: true, cancellationToken);
                }

                if (manifest.Steps[index].Status == WorkflowStepStatus.Completed)
                {
                    continue;
                }

                return manifest;
            }

            manifest = await SendStepAsync(project, manifest, index, cancellationToken);
            if (manifest.Status == RunStatus.NeedsManualIntervention)
            {
                return manifest;
            }

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
        var running = step with { Status = WorkflowStepStatus.Running, StartedAt = step.StartedAt ?? clock.Now, AttemptCount = step.AttemptCount + 1, LastProgressAt = clock.Now };
        manifest = ReplaceStep(manifest, stepIndex, running) with
        {
            CurrentStepIndex = stepIndex,
            CurrentStage = running.Kind == PromptKind.Task ? WorkflowStage.Task : WorkflowStage.Quality,
            CurrentPromptPath = running.SourcePath,
            CurrentQualityIndex = running.Kind == PromptKind.Quality ? running.Order : null,
            CurrentLogicalStepStatus = running.Status.ToString(),
            CurrentStepContinuationAttempts = running.ContinuationAttempts,
            CurrentStepTransportRetries = running.TransportRetries,
            LastProgressAt = clock.Now,
            Status = RunStatus.Running,
            UpdatedAt = clock.Now
        };
        await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.StepStarted, manifest.RunId, running.Id.Value, manifest.SessionId, manifest.TaskDescriptor?.FileName, Path.GetFileName(running.SourcePath), cancellationToken);

        try
        {
            var payload = await BuildPayloadAsync(manifest, running, cancellationToken);
            string? sessionId = manifest.SessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                var title = manifest.TaskDescriptor?.FileName ?? Path.GetFileName(running.SourcePath);
                var session = await openCodeClient.StartSessionAsync(project, title, cancellationToken);
                sessionId = session.SessionId;
                manifest = manifest with { SessionId = sessionId, UpdatedAt = clock.Now };
                await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
                await AppendEventAsync(project, QueueEventTypes.SessionCreated, manifest.RunId, running.Id.Value, sessionId, manifest.TaskDescriptor?.FileName, null, cancellationToken);
            }

            return await SendLogicalStepWithRecoveryAsync(project, manifest, stepIndex, sessionId, payload, isContinuation: false, cancellationToken);
        }
        catch (Exception exception) when (exception is OpenCodeClientException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return await MarkStepFailedAsync(project, manifest, stepIndex, exception.Message, cancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return await MarkStepFailedAsync(project, manifest, stepIndex, exception.Message, cancellationToken);
        }
    }

    private async Task<RunManifest> ResetFailedMessageIdPayloadValidationStepAsync(ProjectProfile project, RunManifest manifest, CancellationToken cancellationToken)
    {
        var index = manifest.CurrentStepIndex >= 0 && manifest.CurrentStepIndex < manifest.Steps.Count
            ? manifest.CurrentStepIndex
            : manifest.Steps.ToList().FindIndex(step => step.Status == WorkflowStepStatus.Failed);
        if (index < 0)
        {
            return manifest;
        }

        var step = manifest.Steps[index];
        var reset = step with
        {
            Status = WorkflowStepStatus.Pending,
            SessionMessageId = null,
            RecoveryMessageId = null,
            LastInterruptionSignature = null,
            SameSignatureRepeatCount = 0,
            NextRetryAt = null,
            LastProgressAt = clock.Now
        };

        var recovered = ReplaceStep(manifest, index, reset) with
        {
            Status = RunStatus.Running,
            CurrentStepIndex = index,
            CurrentLogicalStepStatus = reset.Status.ToString(),
            LastError = null,
            LastInterruptionSignature = null,
            SameSignatureRepeatCount = 0,
            UpdatedAt = clock.Now,
            FinishedAt = null
        };
        await stateStore.SaveRunManifestAsync(project, recovered, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.ActiveRunRecoveredAfterRestart, recovered.RunId, reset.Id.Value, recovered.SessionId, recovered.TaskDescriptor?.FileName, "Повторяю step после OpenCode payload validation error по messageID; предыдущий prompt не был принят server API.", cancellationToken);
        return recovered;
    }

    private static bool IsMessageIdPayloadValidationFailure(RunManifest manifest)
    {
        var text = manifest.LastError ?? string.Empty;
        return text.Contains("messageID", StringComparison.OrdinalIgnoreCase)
            && text.Contains("Expected a string starting with", StringComparison.OrdinalIgnoreCase)
            && text.Contains("msg", StringComparison.OrdinalIgnoreCase)
            && text.Contains("BadRequest", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<RunManifest> SendLogicalStepWithRecoveryAsync(ProjectProfile project, RunManifest manifest, int stepIndex, string sessionId, PromptPayload payload, bool isContinuation, CancellationToken cancellationToken, bool sendFirst = true)
    {
        var resilience = manifest.OpenCodeSettingsSnapshot.Resilience;
        var stepStartedAt = clock.Now;
        var currentPayload = payload;
        var continuation = isContinuation;

        while (true)
        {
            OpenCodeMessageResult result;
            var messageLogPath = await runWorkspace.WriteAttemptMessageAsync(project, manifest.RunId, currentPayload.MessageId, currentPayload.Content, cancellationToken);
            manifest = await RecordDispatchedMessageAsync(project, manifest, stepIndex, currentPayload.MessageId, continuation, cancellationToken);
            try
            {
                result = sendFirst
                    ? await openCodeClient.SendPromptAsync(project, sessionId, currentPayload, cancellationToken)
                    : await openCodeClient.WaitForPromptAsync(project, sessionId, currentPayload.MessageId, cancellationToken);
                result = result with { MessageLogPath = result.MessageLogPath ?? messageLogPath };
            }
            catch (OpenCodeClientException exception) when (IsLostSessionError(exception))
            {
                return await MarkManualAsync(project, manifest, "Сессия OpenCode потеряна окончательно: " + exception.Message, cancellationToken);
            }
            catch (OpenCodeClientException exception)
            {
                result = new OpenCodeMessageResult(false, currentPayload.MessageId, ErrorMessage: exception.Message, IsTransportError: IsRecoverableTransportException(exception), IsTimeout: IsTimeoutException(exception), StartedAt: clock.Now, FinishedAt: clock.Now, MessageLogPath: messageLogPath);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                result = new OpenCodeMessageResult(false, currentPayload.MessageId, ErrorMessage: exception.Message, StartedAt: clock.Now, FinishedAt: clock.Now, MessageLogPath: messageLogPath);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or TaskCanceledException or InvalidOperationException)
            {
                result = new OpenCodeMessageResult(false, currentPayload.MessageId, ErrorMessage: exception.Message, IsTransportError: exception is IOException or TimeoutException or TaskCanceledException, IsTimeout: exception is TimeoutException or TaskCanceledException, StartedAt: clock.Now, FinishedAt: clock.Now, MessageLogPath: messageLogPath);
            }

            var classification = classifier.Classify(result, resilience);
            manifest = await RecordAttemptAsync(project, manifest, stepIndex, result, classification, continuation, cancellationToken);
            var step = manifest.Steps[stepIndex];
            sendFirst = true;

            if (classification.Kind == OpenCodeStepOutcomeKind.Completed)
            {
                if (continuation)
                {
                    await AppendEventAsync(project, QueueEventTypes.ContinuationAttemptCompleted, manifest.RunId, step.Id.Value, sessionId, manifest.TaskDescriptor?.FileName, "Continuation завершился успешно.", cancellationToken);
                }

                return await MarkStepCompletedAsync(project, manifest, stepIndex, result.MessageId, cancellationToken);
            }

            if (classification.Kind == OpenCodeStepOutcomeKind.PermissionRequest && resilience.PermissionPolicy != PermissionPolicy.AutoApprove)
            {
                return await MarkManualAsync(project, manifest, classification.Message ?? "OpenCode запросил разрешение. Автоматическое подтверждение выключено по умолчанию.", cancellationToken);
            }

            if (classification.Kind == OpenCodeStepOutcomeKind.QuestionRequest && !resilience.AutoRespondToRecoverableQuestions)
            {
                return await MarkManualAsync(project, manifest, classification.Message ?? "OpenCode задал вопрос пользователю. Автоответы выключены.", cancellationToken);
            }

            if (classification.Kind == OpenCodeStepOutcomeKind.NeedsManualIntervention)
            {
                return await MarkManualAsync(project, manifest, classification.Message ?? "OpenCode запросил ручное вмешательство.", cancellationToken);
            }

            if (classification.Kind is OpenCodeStepOutcomeKind.FatalFailure or OpenCodeStepOutcomeKind.NonRecoverableError || !resilience.Enabled)
            {
                return await MarkStepFailedAsync(project, manifest, stepIndex, classification.Message ?? result.ErrorMessage ?? "OpenCode не выполнил prompt успешно.", cancellationToken);
            }

            var limitError = GetRecoveryLimitError(step, resilience, stepStartedAt, clock.Now);
            if (limitError is not null)
            {
                await AppendEventAsync(project, QueueEventTypes.RecoveryLimitExceeded, manifest.RunId, step.Id.Value, sessionId, manifest.TaskDescriptor?.FileName, limitError, cancellationToken);
                return await MarkManualAsync(project, manifest, limitError, cancellationToken);
            }

            var message = $"Обнаружено прерывание OpenCode: {classification.Signature ?? classification.Message ?? "recoverable interruption"}. Текущий шаг не считается завершённым; запускаю recovery в той же сессии OpenCode. Попытка continuation: {step.ContinuationAttempts + 1} из {resilience.MaxContinuationAttemptsPerStep}.";
            await AppendEventAsync(project, QueueEventTypes.RecoverableInterruptionDetected, manifest.RunId, step.Id.Value, sessionId, manifest.TaskDescriptor?.FileName, message, cancellationToken);

            if (result.IsTransportError || result.IsTimeout)
            {
                manifest = await ScheduleTransportRetryAsync(project, manifest, stepIndex, resilience, cancellationToken);
            }

            var readinessError = await WaitForSessionReadyForContinuationAsync(project, manifest, stepIndex, sessionId, resilience, cancellationToken);
            if (readinessError is not null)
            {
                return await MarkManualAsync(project, manifest, readinessError, cancellationToken);
            }

            currentPayload = BuildContinuationPayload(manifest, manifest.Steps[stepIndex]);
            continuation = true;
            await AppendEventAsync(project, QueueEventTypes.ContinuationPromptSent, manifest.RunId, step.Id.Value, sessionId, manifest.TaskDescriptor?.FileName, "Continuation prompt отправлен в ту же sessionId.", cancellationToken);
        }
    }

    private async Task<RunManifest> RecordDispatchedMessageAsync(ProjectProfile project, RunManifest manifest, int stepIndex, string messageId, bool isContinuation, CancellationToken cancellationToken)
    {
        var step = manifest.Steps[stepIndex];
        var updatedStep = isContinuation
            ? step with { RecoveryMessageId = messageId, LastProgressAt = clock.Now }
            : step with { SessionMessageId = messageId, LastProgressAt = clock.Now };
        var updated = ReplaceStep(manifest, stepIndex, updatedStep) with
        {
            CurrentLogicalStepStatus = updatedStep.Status.ToString(),
            LastProgressAt = updatedStep.LastProgressAt,
            UpdatedAt = clock.Now
        };
        await stateStore.SaveRunManifestAsync(project, updated, cancellationToken);
        return updated;
    }

    private async Task<string?> WaitForSessionReadyForContinuationAsync(ProjectProfile project, RunManifest manifest, int stepIndex, string sessionId, ResilienceSettings resilience, CancellationToken cancellationToken)
    {
        var deadline = clock.Now.AddMinutes(Math.Max(1, resilience.StepTimeoutMinutes));
        var step = manifest.Steps[stepIndex];
        var maxChecks = Math.Max(1, resilience.MaxTransportRetriesPerAttempt + resilience.MaxContinuationAttemptsPerStep + 1);
        for (var check = 0; check < maxChecks && clock.Now <= deadline; check++)
        {
            OpenCodeSessionStatus status;
            try
            {
                status = await openCodeClient.GetSessionStatusAsync(project, sessionId, cancellationToken);
            }
            catch (OpenCodeClientException exception) when (IsRecoverableTransportException(exception) || IsTimeoutException(exception))
            {
                await AppendEventAsync(project, QueueEventTypes.TransportRetryScheduled, manifest.RunId, step.Id.Value, sessionId, manifest.TaskDescriptor?.FileName, "Не удалось проверить статус session перед continuation: " + exception.Message, cancellationToken);
                await DelayBeforeStatusRetryAsync(resilience, cancellationToken);
                continue;
            }

            if (status.State is OpenCodeSessionState.Idle or OpenCodeSessionState.Unknown)
            {
                return null;
            }

            if (status.State == OpenCodeSessionState.Retry)
            {
                await AppendEventAsync(project, QueueEventTypes.TransportRetryScheduled, manifest.RunId, step.Id.Value, sessionId, manifest.TaskDescriptor?.FileName, "OpenCode session сейчас во встроенном retry. Continuation пока не отправляется.", cancellationToken);
                await DelayBeforeStatusRetryAsync(resilience, cancellationToken);
                continue;
            }

            if (status.State == OpenCodeSessionState.Busy)
            {
                await AppendEventAsync(project, QueueEventTypes.TransportRetryScheduled, manifest.RunId, step.Id.Value, sessionId, manifest.TaskDescriptor?.FileName, "OpenCode session занята. Continuation будет отправлен только после Idle.", cancellationToken);
                await DelayBeforeStatusRetryAsync(resilience, cancellationToken);
                continue;
            }

            return $"OpenCode session перешла в статус {status.State}. Continuation не отправлен; требуется ручное вмешательство.";
        }

        return "Не удалось дождаться Idle status перед continuation. Очередь остановлена, task prompt не архивирован.";
    }

    private static Task DelayBeforeStatusRetryAsync(ResilienceSettings resilience, CancellationToken cancellationToken)
    {
        var delay = Math.Max(0, resilience.RetryDelaySeconds);
        return delay == 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
    }

    private async Task<RunManifest> RecordAttemptAsync(ProjectProfile project, RunManifest manifest, int stepIndex, OpenCodeMessageResult result, StepClassification classification, bool isContinuation, CancellationToken cancellationToken)
    {
        var step = manifest.Steps[stepIndex];
        var signature = classification.Signature;
        var sameSignatureRepeats = !string.IsNullOrWhiteSpace(signature) && string.Equals(signature, step.LastInterruptionSignature, StringComparison.OrdinalIgnoreCase)
            ? step.SameSignatureRepeatCount + 1
            : string.IsNullOrWhiteSpace(signature) ? step.SameSignatureRepeatCount : 1;
        var logs = step.AttemptLogs.ToList();
        logs.Add(new StepAttemptLog
        {
            AttemptNumber = logs.Count + 1,
            IsContinuation = isContinuation,
            MessageId = result.MessageId,
            Outcome = classification.Kind.ToString(),
            Signature = signature,
            StdoutLogPath = result.StdoutLogPath,
            StderrLogPath = result.StderrLogPath,
            MessageLogPath = result.MessageLogPath,
            StartedAt = result.StartedAt ?? clock.Now,
            FinishedAt = result.FinishedAt ?? clock.Now
        });

        var updatedStep = step with
        {
            Status = classification.Kind == OpenCodeStepOutcomeKind.Completed ? step.Status : WorkflowStepStatus.Recovering,
            ContinuationAttempts = isContinuation ? step.ContinuationAttempts + 1 : step.ContinuationAttempts,
            TransportRetries = result.IsTransportError || result.IsTimeout ? step.TransportRetries + 1 : step.TransportRetries,
            LastInterruptionSignature = signature ?? step.LastInterruptionSignature,
            SameSignatureRepeatCount = sameSignatureRepeats,
            LastProgressAt = clock.Now,
            AttemptLogs = logs,
            RecoveryMessageId = isContinuation ? result.MessageId ?? step.RecoveryMessageId : step.RecoveryMessageId,
            SessionMessageId = !isContinuation ? result.MessageId ?? step.SessionMessageId : step.SessionMessageId
        };

        var updated = ReplaceStep(manifest, stepIndex, updatedStep) with
        {
            CurrentLogicalStepStatus = updatedStep.Status.ToString(),
            CurrentStepContinuationAttempts = updatedStep.ContinuationAttempts,
            CurrentStepTransportRetries = updatedStep.TransportRetries,
            LastInterruptionSignature = updatedStep.LastInterruptionSignature,
            SameSignatureRepeatCount = updatedStep.SameSignatureRepeatCount,
            LastProgressAt = updatedStep.LastProgressAt,
            UpdatedAt = clock.Now
        };
        await stateStore.SaveRunManifestAsync(project, updated, cancellationToken);
        return updated;
    }

    private async Task<RunManifest> ScheduleTransportRetryAsync(ProjectProfile project, RunManifest manifest, int stepIndex, ResilienceSettings resilience, CancellationToken cancellationToken)
    {
        var step = manifest.Steps[stepIndex];
        var delaySeconds = resilience.RetryDelaySeconds * Math.Pow(Math.Max(1.0, resilience.RetryBackoffMultiplier), Math.Max(0, step.TransportRetries - 1));
        var nextRetryAt = clock.Now.AddSeconds(delaySeconds);
        var updatedStep = step with { NextRetryAt = nextRetryAt };
        var updated = ReplaceStep(manifest, stepIndex, updatedStep) with { NextRetryAt = nextRetryAt, UpdatedAt = clock.Now };
        await stateStore.SaveRunManifestAsync(project, updated, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.TransportRetryScheduled, manifest.RunId, step.Id.Value, manifest.SessionId, manifest.TaskDescriptor?.FileName, $"Ожидаю {delaySeconds:0} секунд перед повтором из-за сетевой ошибки.", cancellationToken);
        if (delaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
        }

        return updated;
    }

    private static string? GetRecoveryLimitError(WorkflowStep step, ResilienceSettings resilience, DateTimeOffset stepStartedAt, DateTimeOffset now)
    {
        if (step.ContinuationAttempts >= resilience.MaxContinuationAttemptsPerStep)
        {
            return "Достигнут лимит continuation attempts для текущего шага. Требуется ручное вмешательство. Очередь остановлена. Task prompt не перенесён в completed/archive.";
        }

        if (step.SameSignatureRepeatCount > resilience.StopAfterSameSignatureRepeats)
        {
            return "Достигнут лимит повторов одной и той же ошибки. Требуется ручное вмешательство. Очередь остановлена. Task prompt не перенесён в completed/archive.";
        }

        if (step.TransportRetries > resilience.MaxTransportRetriesPerAttempt)
        {
            return "Достигнут лимит transport retries для текущего шага. Требуется ручное вмешательство.";
        }

        if (now - stepStartedAt > TimeSpan.FromMinutes(resilience.StepTimeoutMinutes))
        {
            return "Истёк общий timeout logical step. Требуется ручное вмешательство.";
        }

        return null;
    }

    private static bool IsLostSessionError(OpenCodeClientException exception)
    {
        var text = ExceptionText(exception);
        return text.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase)
            && text.Contains("/session/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecoverableTransportException(OpenCodeClientException exception)
    {
        var text = ExceptionText(exception);
        return exception.InnerException is HttpRequestException or IOException or TimeoutException or TaskCanceledException
            || text.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || text.Contains("network error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ETIMEDOUT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("socket hang up", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTimeoutException(OpenCodeClientException exception)
    {
        var text = ExceptionText(exception);
        return exception.InnerException is TimeoutException or TaskCanceledException
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ETIMEDOUT", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExceptionText(Exception exception)
    {
        return string.Join('\n', exception.Message, exception.InnerException?.Message);
    }

    private PromptPayload BuildContinuationPayload(RunManifest manifest, WorkflowStep step)
    {
        var attempt = step.ContinuationAttempts + 1;
        return new PromptPayload
        {
            Content = manifest.OpenCodeSettingsSnapshot.Resilience.ContinuationPrompt ?? OpenCodeContinuationPrompt.Default,
            SourcePath = step.SnapshotPath ?? step.SourcePath,
            MessageId = BuildOpenCodeMessageId(),
            Transport = PromptTransport.Inline,
            MaxInlinePromptChars = manifest.OpenCodeSettingsSnapshot.MaxInlinePromptChars,
            RunId = manifest.RunId,
            StepId = step.Id.Value
        };
    }

    private async Task<PromptPayload> BuildPayloadAsync(RunManifest manifest, WorkflowStep step, CancellationToken cancellationToken)
    {
        var path = step.SnapshotPath ?? step.SourcePath;
        var content = await promptRepository.ReadPromptTextAsync(path, cancellationToken);
        return new PromptPayload
        {
            Content = content,
            SourcePath = path,
            MessageId = BuildOpenCodeMessageId(),
            Transport = manifest.OpenCodeSettingsSnapshot.PromptTransport,
            MaxInlinePromptChars = manifest.OpenCodeSettingsSnapshot.MaxInlinePromptChars,
            RunId = manifest.RunId,
            StepId = step.Id.Value
        };
    }

    private string BuildOpenCodeMessageId()
    {
        var timestamp = clock.Now.ToUnixTimeMilliseconds();
        long counter;
        lock (OpenCodeMessageIdLock)
        {
            if (timestamp != lastOpenCodeMessageTimestamp)
            {
                lastOpenCodeMessageTimestamp = timestamp;
                openCodeMessageCounter = 0;
            }

            counter = ++openCodeMessageCounter;
        }

        var current = (timestamp * 0x1000L + counter) & 0xffffffffffffL;
        Span<byte> bytes = stackalloc byte[14];
        RandomNumberGenerator.Fill(bytes);

        Span<char> suffix = stackalloc char[14];
        for (var index = 0; index < bytes.Length; index++)
        {
            suffix[index] = OpenCodeIdentifierChars[bytes[index] % OpenCodeIdentifierChars.Length];
        }

        return "msg_" + current.ToString("x12") + new string(suffix);
    }

    private async Task<RunManifest> MarkStepCompletedAsync(ProjectProfile project, RunManifest manifest, int stepIndex, string? messageId, CancellationToken cancellationToken)
    {
        var step = manifest.Steps[stepIndex];
        var completed = step with { Status = WorkflowStepStatus.Completed, SessionMessageId = messageId ?? step.SessionMessageId, CompletedAt = clock.Now, LastProgressAt = clock.Now };
        manifest = ReplaceStep(manifest, stepIndex, completed) with { CurrentStepIndex = stepIndex + 1, CurrentLogicalStepStatus = completed.Status.ToString(), LastProgressAt = clock.Now, UpdatedAt = clock.Now, LastError = null };
        await stateStore.SaveRunManifestAsync(project, manifest, cancellationToken);
        await AppendEventAsync(project, QueueEventTypes.StepCompleted, manifest.RunId, completed.Id.Value, manifest.SessionId, manifest.TaskDescriptor?.FileName, Path.GetFileName(completed.SourcePath), cancellationToken);
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
        await AppendEventAsync(project, QueueEventTypes.NeedsManualInterventionDetected, manifest.RunId, null, manifest.SessionId, manifest.TaskDescriptor?.FileName, error, cancellationToken);
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

    private async Task<QueueOperationResult?> EnsureOpenCodeReadyAsync(ProjectProfile project, QueueState? state, RunManifest? manifest, CancellationToken cancellationToken)
    {
        try
        {
            await openCodeClient.EnsureReadyAsync(project, cancellationToken);
            return null;
        }
        catch (OpenCodeProjectMismatchException exception)
        {
            return QueueOperationResult.Failure(QueueExitCodes.OpenCodeUnavailableOrProjectMismatch, "OpenCode server открыт для другого проекта. Очередь автоматически не запускается. " + exception.Message) with { Project = project, State = state, Manifest = manifest };
        }
        catch (OpenCodeClientException exception)
        {
            return QueueOperationResult.Failure(QueueExitCodes.OpenCodeUnavailableOrProjectMismatch, "OpenCode недоступен: " + exception.Message) with { Project = project, State = state, Manifest = manifest };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return QueueOperationResult.Failure(QueueExitCodes.OpenCodeUnavailableOrProjectMismatch, "Runtime-проверка OpenCode не прошла: " + exception.Message) with { Project = project, State = state, Manifest = manifest };
        }
    }

    private static bool HasBlockingDiscoveryWarning(PromptDiscoveryResult discovery, out string message)
    {
        message = discovery.Warnings.FirstOrDefault(warning => warning.StartsWith("Папка tasks не существует", StringComparison.Ordinal)
            || warning.StartsWith("Папка quality не существует", StringComparison.Ordinal)) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(message);
    }

    private async Task<IAsyncDisposable?> AcquireLockAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var result = await runLock.TryAcquireAsync(project, cancellationToken);
        return result.Releaser;
    }

    private async Task AppendEventAsync(ProjectProfile project, string type, string? runId, string? stepId, string? sessionId, string? taskFile, string? message, CancellationToken cancellationToken)
    {
        ReportProgress(project, type, stepId, taskFile, message);
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

    private void ReportProgress(ProjectProfile project, string type, string? stepId, string? taskFile, string? message)
    {
        if (reporter is null)
        {
            return;
        }

        var quiet = project.OpenCodeOverrides.ConsoleVerbosity == ConsoleVerbosity.Quiet;
        if (type is QueueEventTypes.NeedsManualInterventionDetected or QueueEventTypes.RecoveryLimitExceeded)
        {
            reporter.Warning(message ?? "Требуется ручное вмешательство.");
            return;
        }

        var progress = FormatProgressMessage(type, stepId, taskFile, message);
        if (string.IsNullOrWhiteSpace(progress))
        {
            return;
        }

        if (type is QueueEventTypes.StepFailed or QueueEventTypes.RecoverableInterruptionDetected)
        {
            reporter.Warning(progress);
            return;
        }

        if (!quiet)
        {
            reporter.Info(progress);
        }
    }

    private static string? FormatProgressMessage(string type, string? stepId, string? taskFile, string? message)
    {
        var stepLabel = FormatStepLabel(stepId);
        var promptFile = string.IsNullOrWhiteSpace(message) ? taskFile : message;
        return type switch
        {
            QueueEventTypes.RunCreated => $"Запущена задача: {taskFile ?? "без имени"}.",
            QueueEventTypes.StepStarted when stepLabel is not null => $"Запущен {stepLabel}: {promptFile ?? "prompt"}.",
            QueueEventTypes.StepCompleted when stepLabel is not null => $"Завершён {stepLabel}: {promptFile ?? "prompt"}.",
            QueueEventTypes.StepFailed when stepLabel is not null => $"{stepLabel} завершился ошибкой: {TrimProgressText(message)}",
            QueueEventTypes.RecoverableInterruptionDetected => $"Произошла ошибка \"{ExtractInterruption(message)}\". Запускаю recovery для {stepLabel ?? "текущего шага"}.",
            QueueEventTypes.ContinuationPromptSent => $"Recovery prompt отправлен для {stepLabel ?? "текущего шага"}.",
            QueueEventTypes.ContinuationAttemptCompleted => $"Recovery завершён для {stepLabel ?? "текущего шага"}.",
            QueueEventTypes.TransportRetryScheduled => TrimProgressText(message),
            QueueEventTypes.ActiveRunRecoveredAfterRestart => TrimProgressText(message),
            QueueEventTypes.TaskArchived => $"Задача архивирована: {taskFile ?? "без имени"}.",
            QueueEventTypes.RunCompleted => $"Задача завершена: {taskFile ?? "без имени"}.",
            QueueEventTypes.RunAborted => $"Run остановлен: {taskFile ?? "без имени"}.",
            _ => null
        };
    }

    private static string? FormatStepLabel(string? stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId))
        {
            return null;
        }

        return string.Equals(stepId, WorkflowStepId.Task.Value, StringComparison.OrdinalIgnoreCase)
            ? "task"
            : stepId;
    }

    private static string TrimProgressText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "нет деталей";
        }

        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..237] + "...";
    }

    private static string ExtractInterruption(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "recoverable interruption";
        }

        const string prefix = "Обнаружено прерывание OpenCode:";
        var start = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var valueStart = start + prefix.Length;
            var valueEnd = message.IndexOf('.', valueStart);
            var value = valueEnd > valueStart ? message[valueStart..valueEnd] : message[valueStart..];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return TrimProgressText(value.Trim());
            }
        }

        return TrimProgressText(message);
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
