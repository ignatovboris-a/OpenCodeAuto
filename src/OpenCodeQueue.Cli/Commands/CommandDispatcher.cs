using OpenCodeQueue.Cli.ConsoleUi;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Infrastructure;

namespace OpenCodeQueue.Cli.Commands;

public sealed class CommandDispatcher(
    IConsoleReporter reporter,
    IAppConfigStore configStore,
    IProjectRegistry projectRegistry,
    IPromptRepository promptRepository,
    IStateStore stateStore,
    IRunLock runLock,
    IClock clock,
    IProjectDiscoveryService projectDiscoveryService,
    ProjectProfilePrompt projectProfilePrompt,
    ProjectConsolePresenter projectPresenter,
    InteractiveMenu interactiveMenu)
{
    public async Task<int> DispatchAsync(CliCommand command, CancellationToken cancellationToken)
    {
        if (command.HelpRequested || string.Equals(command.Name, "help", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        return command.Name.ToLowerInvariant() switch
        {
            "menu" => await interactiveMenu.RunAsync(command.ConfigPath, cancellationToken),
            "run" => await RunAsync(command, cancellationToken),
            "resume" => await ResumeAsync(command, cancellationToken),
            "status" => await StatusAsync(command, cancellationToken),
            "list" => await ListPromptsAsync(command, cancellationToken),
            "validate" => await ValidateAsync(command, cancellationToken),
            "doctor" => await DoctorAsync(command, cancellationToken),
            "abort" => await AbortAsync(command, cancellationToken),
            "project" => await DispatchProjectAsync(command, cancellationToken),
            _ => Unknown(command.Name)
        };
    }

    private async Task<int> DispatchProjectAsync(CliCommand command, CancellationToken cancellationToken)
    {
        return command.SubCommand?.ToLowerInvariant() switch
        {
            "list" => await ProjectListAsync(command.ConfigPath, cancellationToken),
            "current" => await ProjectCurrentAsync(command.ConfigPath, cancellationToken),
            "select" => await ProjectSelectAsync(command.ConfigPath, command.Argument, cancellationToken),
            "add" => await ProjectAddAsync(command.ConfigPath, cancellationToken),
            "remove" => await ProjectRemoveAsync(command.ConfigPath, command.Argument, cancellationToken),
            "update" => await ProjectUpdateAsync(command.ConfigPath, command.Argument, cancellationToken),
            "discover" => await ProjectDiscoverAsync(command.ConfigPath, cancellationToken),
            _ => Unknown("project " + command.SubCommand)
        };
    }

    private async Task<int> RunAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(command, cancellationToken);
        if (project is null)
        {
            reporter.Warning("Проект не выбран. Используйте меню или команду project select/add.");
            return 2;
        }

        if (!await EnsureNoActiveRunAsync(project, cancellationToken))
        {
            return 2;
        }

        await using var acquiredLock = await AcquireRunLockOrReportAsync(project, cancellationToken);
        if (acquiredLock is null)
        {
            return 2;
        }

        var discovery = await promptRepository.DiscoverAsync(project, cancellationToken);

        if (discovery.TaskPrompts.Count == 0)
        {
            reporter.Warning("Очередь задач пуста.");
            return 0;
        }

        reporter.Info($"Очередь для проекта '{project.Id}' будет запущена из: {project.ProjectDir}");
        reporter.Info(command.Once ? "Режим: одна задача." : "Режим: вся очередь.");
        reporter.Warning("Логика оркестрации очереди ещё не реализована на этом шаге.");
        return 0;
    }

    private async Task<int> StatusAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(command, cancellationToken);
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return 2;
        }

        try
        {
            var discovery = await promptRepository.DiscoverAsync(project, cancellationToken);
            projectPresenter.PrintStatus(project, discovery.TaskPrompts.Count, discovery.QualityPrompts.Count);
            await PrintStateStatusAsync(project, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            reporter.Warning(exception.Message);
            return 2;
        }

        return 0;
    }

    private async Task<int> ResumeAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(command, cancellationToken);
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return 2;
        }

        await using var acquiredLock = await AcquireRunLockOrReportAsync(project, cancellationToken);
        if (acquiredLock is null)
        {
            return 2;
        }

        try
        {
            var state = await stateStore.LoadQueueStateAsync(project, cancellationToken);
            if (string.IsNullOrWhiteSpace(state?.ActiveRunId))
            {
                reporter.Info("В выбранном проекте нет активного run для восстановления.");
                return 0;
            }

            var now = clock.Now;
            var manifest = await stateStore.LoadRunManifestAsync(project, state.ActiveRunId, cancellationToken);
            if (manifest is null)
            {
                var manual = new RunManifest
                {
                    RunId = state.ActiveRunId,
                    ProjectId = project.Id,
                    ProjectDirSnapshot = project.ProjectDir,
                    Status = RunStatus.NeedsManualIntervention,
                    LastError = "manifest.json отсутствует",
                    CreatedAt = now,
                    StartedAt = now,
                    UpdatedAt = now
                };
                await stateStore.SaveRunManifestAsync(project, manual, cancellationToken);
                await MarkNeedsManualInterventionAsync(project, state.ActiveRunId, "manifest.json отсутствует", cancellationToken);
                reporter.Warning($"manifest.json для activeRunId '{state.ActiveRunId}' отсутствует. Не запускайте новую задачу; проверьте папку runs вручную.");
                return 2;
            }

            await stateStore.AppendEventAsync(project, NewEvent(QueueEventTypes.RecoveryStarted, project, manifest.RunId, manifest.CurrentStepIndex < manifest.Steps.Count ? manifest.Steps[manifest.CurrentStepIndex].Id.Value : null, manifest.SessionId, null, null), cancellationToken);
            var recovered = manifest with
            {
                Status = manifest.Status == RunStatus.CompletedPendingArchive ? RunStatus.CompletedPendingArchive : RunStatus.Running,
                RecoveryAttempts = manifest.RecoveryAttempts + 1,
                UpdatedAt = clock.Now
            };
            await stateStore.SaveRunManifestAsync(project, recovered, cancellationToken);
            await stateStore.AppendEventAsync(project, NewEvent(QueueEventTypes.RecoveryCompleted, project, recovered.RunId, null, recovered.SessionId, null, "ConservativeContinue: продолжайте предыдущий шаг в той же session"), cancellationToken);

            if (recovered.Status == RunStatus.CompletedPendingArchive)
            {
                reporter.Info("Run находится в CompletedPendingArchive. При продолжении нужно завершить архивирование task prompt, prompts повторно не отправляются.");
            }
            else
            {
                reporter.Warning("Консервативное восстановление: новая задача не выбирается. Продолжайте активный шаг в той же OpenCode session; если нельзя доказать завершение, отправьте recovery prompt в эту session.");
            }

            await PrintManifestAsync(project, recovered);
            return 0;
        }
        catch (InvalidOperationException exception)
        {
            reporter.Warning(exception.Message);
            return 2;
        }
    }

    private async Task<int> AbortAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(command, cancellationToken);
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return 2;
        }

        if (!reporter.Confirm("Перевести active run в Aborted без удаления данных? [y/N]: "))
        {
            reporter.Warning("Abort отменён.");
            return 2;
        }

        await using var acquiredLock = await AcquireRunLockOrReportAsync(project, cancellationToken);
        if (acquiredLock is null)
        {
            return 2;
        }

        var state = await stateStore.LoadQueueStateAsync(project, cancellationToken);
        if (string.IsNullOrWhiteSpace(state?.ActiveRunId))
        {
            reporter.Info("В выбранном проекте нет активного run.");
            return 0;
        }

        var manifest = await stateStore.LoadRunManifestAsync(project, state.ActiveRunId, cancellationToken);
        var now = clock.Now;
        if (manifest is not null)
        {
            await stateStore.SaveRunManifestAsync(project, manifest with { Status = RunStatus.Aborted, UpdatedAt = now, FinishedAt = now }, cancellationToken);
        }

        await stateStore.SaveQueueStateAsync(project, state with { ActiveRunId = null, UpdatedAt = now }, cancellationToken);
        await stateStore.AppendEventAsync(project, NewEvent(QueueEventTypes.RunAborted, project, state.ActiveRunId, null, manifest?.SessionId, null, null), cancellationToken);
        reporter.Info("Run переведён в Aborted. Данные сохранены, task prompt не архивирован автоматически.");
        return 0;
    }

    private async Task<int> ListPromptsAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(command, cancellationToken);
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return 2;
        }

        var discovery = await promptRepository.DiscoverAsync(project, cancellationToken);
        projectPresenter.PrintPromptList(project, discovery);
        return 0;
    }

    private async Task<int> ValidateAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var configPath = command.ConfigPath;
        if (!File.Exists(configPath))
        {
            reporter.Warning($"Файл конфигурации не найден: {configPath}");
            return 2;
        }

        var config = await configStore.LoadAsync(configPath, cancellationToken);
        reporter.Info($"Файл конфигурации найден: {configPath}");
        reporter.Info($"Проектов в registry: {config?.Projects.Count ?? 0}");
        reporter.Info(config?.ActiveProjectId is null ? "Активный проект не выбран." : $"Активный проект: {config.ActiveProjectId}");
        if (config is null)
        {
            reporter.Warning("Файл конфигурации пуст или не может быть прочитан.");
            return 2;
        }

        var errors = AppConfigValidator.Validate(config);
        if (errors.Count == 0)
        {
            reporter.Info("Конфигурация валидна.");
            var project = await ResolveProjectAsync(command, cancellationToken);
            if (project is null)
            {
                return 0;
            }

            var discovery = await promptRepository.DiscoverAsync(project, cancellationToken);
            foreach (var warning in discovery.Warnings)
            {
                reporter.Warning(warning);
            }

            return discovery.Warnings.Count == 0 ? 0 : 2;
        }

        reporter.Warning("Найдены ошибки конфигурации:");
        foreach (var error in errors)
        {
            reporter.Warning("- " + error);
        }

        return 2;
    }

    private async Task<int> ProjectListAsync(string configPath, CancellationToken cancellationToken)
    {
        var projects = await projectRegistry.ListAsync(configPath, cancellationToken);
        if (projects.Count == 0)
        {
            reporter.Info("В registry пока нет проектов.");
            return 0;
        }

        foreach (var project in projects)
        {
            reporter.Info($"{project.Id}: {project.ProjectDir}");
        }

        return 0;
    }

    private async Task<int> ProjectCurrentAsync(string configPath, CancellationToken cancellationToken)
    {
        var project = await projectRegistry.GetActiveAsync(configPath, cancellationToken);
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return 2;
        }

        reporter.Info($"Активный проект: {project.Id}");
        reporter.Info(project.ProjectDir);
        return 0;
    }

    private async Task<int> ProjectSelectAsync(string configPath, string? projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            reporter.Warning("Укажите id проекта: project select <id> --config opencode-queue.json");
            return 2;
        }

        var result = await projectRegistry.SelectAsync(configPath, projectId, cancellationToken);
        return PrintRegistryResult(result.IsSuccess, result.Message);
    }

    private async Task<int> ProjectAddAsync(string configPath, CancellationToken cancellationToken)
    {
        var project = projectProfilePrompt.ReadNewProject(askOpenCodeOverrides: true);
        if (project is null)
        {
            return 2;
        }

        reporter.PrintProjectForConfirmation(project);
        if (!reporter.Confirm("Сохранить проект в config? [y/N]: "))
        {
            reporter.Warning("Проект не сохранён.");
            return 2;
        }

        var result = await projectRegistry.AddOrUpdateAsync(configPath, project, cancellationToken);
        return PrintRegistryResult(result.IsSuccess, result.Message);
    }

    private async Task<int> ProjectRemoveAsync(string configPath, string? projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            reporter.Warning("Укажите id проекта: project remove <id> --config opencode-queue.json");
            return 2;
        }

        var active = await projectRegistry.GetActiveAsync(configPath, cancellationToken);
        var confirmed = active is not null
            && string.Equals(active.Id.Value, projectId, StringComparison.OrdinalIgnoreCase)
            && reporter.Confirm("Вы удаляете активный проект из registry. Подтвердить? [y/N]: ");
        var result = await projectRegistry.RemoveAsync(configPath, projectId.Trim(), confirmed, cancellationToken);
        return PrintRegistryResult(result.IsSuccess, result.Message);
    }

    private async Task<int> ProjectUpdateAsync(string configPath, string? projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            reporter.Warning("Укажите id проекта: project update <id> --config opencode-queue.json");
            return 2;
        }

        var current = await projectRegistry.GetByIdAsync(configPath, projectId.Trim(), cancellationToken);
        if (current is null)
        {
            reporter.Warning("Проект с таким id не найден.");
            return 2;
        }

        var updated = projectProfilePrompt.ReadUpdatedProject(current);
        reporter.PrintProjectForConfirmation(updated);
        if (!reporter.Confirm("Сохранить изменения проекта в config? [y/N]: "))
        {
            reporter.Warning("Проект не изменён.");
            return 2;
        }

        var result = await projectRegistry.AddOrUpdateAsync(configPath, updated, cancellationToken);
        return PrintRegistryResult(result.IsSuccess, result.Message);
    }

    private async Task<int> ProjectDiscoverAsync(string configPath, CancellationToken cancellationToken)
    {
        var discovered = await projectDiscoveryService.DiscoverAsync(configPath, cancellationToken);
        if (discovered.Count == 0)
        {
            reporter.Info("Проекты не обнаружены. Ручной ввод пути доступен через project add.");
            return 0;
        }

        projectPresenter.PrintDiscoveredProjects(discovered);
        reporter.Info("Ручной ввод пути доступен через project add.");
        return 0;
    }

    private async Task<int> DoctorAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(command, cancellationToken);
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return 2;
        }

        reporter.Info($"Диагностика проекта: {project.Id}");
        projectPresenter.PrintDiagnostics(project);
        var existingLock = await runLock.ReadAsync(project, cancellationToken);
        if (existingLock is not null)
        {
            reporter.Warning(existingLock.IsStale
                ? $"Найден stale lock: pid={existingLock.Pid}, machine={existingLock.MachineName}. Используйте resume или force unlock после проверки."
                : $"Найден активный lock: pid={existingLock.Pid}, machine={existingLock.MachineName}.");
        }

        try
        {
            await PrintStateStatusAsync(project, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            reporter.Warning(exception.Message);
            return 2;
        }

        return 0;
    }

    private async Task<bool> EnsureNoActiveRunAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        try
        {
            var state = await stateStore.LoadQueueStateAsync(project, cancellationToken);
            if (!string.IsNullOrWhiteSpace(state?.ActiveRunId))
            {
                reporter.Warning($"В проекте уже есть active run: {state.ActiveRunId}. Новая задача не будет выбрана; используйте resume/status/abort.");
                return false;
            }

            return true;
        }
        catch (InvalidOperationException exception)
        {
            reporter.Warning(exception.Message);
            return false;
        }
    }

    private async Task<IAsyncDisposable?> AcquireRunLockOrReportAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var lockResult = await runLock.TryAcquireAsync(project, cancellationToken);
        if (lockResult.Acquired)
        {
            return lockResult.Releaser;
        }

        var existing = lockResult.ExistingLock;
        if (existing?.IsStale == true)
        {
            reporter.Warning($"Найден stale lock: pid={existing.Pid}, machine={existing.MachineName}, createdAt={existing.CreatedAt:u}.");
            reporter.Warning("Lock не удалён автоматически. Проверьте, что runner не работает, затем выполните recovery/force unlock вручную.");
        }
        else
        {
            reporter.Warning(lockResult.Message ?? "Не удалось получить lock проекта.");
        }

        return null;
    }

    private async Task PrintStateStatusAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadQueueStateAsync(project, cancellationToken);
        reporter.Info($"State dir: {ProjectPaths.StateDir(project)}");
        if (state is null || string.IsNullOrWhiteSpace(state.ActiveRunId))
        {
            reporter.Info("Active run: нет");
            return;
        }

        reporter.Info($"Active run: {state.ActiveRunId}");
        var manifest = await stateStore.LoadRunManifestAsync(project, state.ActiveRunId, cancellationToken);
        if (manifest is null)
        {
            reporter.Warning("manifest.json отсутствует. Требуется ручная проверка, новая задача не выбирается.");
            return;
        }

        await PrintManifestAsync(project, manifest);
    }

    private Task PrintManifestAsync(ProjectProfile project, RunManifest manifest)
    {
        reporter.Info($"Run status: {manifest.Status}");
        reporter.Info($"Session id: {manifest.SessionId ?? "не создана"}");
        reporter.Info($"Logs: {Path.Combine(ProjectPaths.RunDir(project, manifest.RunId), "logs")}");
        if (manifest.CurrentStepIndex >= 0 && manifest.CurrentStepIndex < manifest.Steps.Count)
        {
            var step = manifest.Steps[manifest.CurrentStepIndex];
            reporter.Info($"Current step: {step.Id} ({step.Status})");
        }
        else
        {
            reporter.Info("Current step: нет");
        }

        if (!string.IsNullOrWhiteSpace(manifest.LastError))
        {
            reporter.Warning("Последняя ошибка: " + manifest.LastError);
        }

        return Task.CompletedTask;
    }

    private async Task MarkNeedsManualInterventionAsync(ProjectProfile project, string runId, string reason, CancellationToken cancellationToken)
    {
        await stateStore.AppendEventAsync(project, NewEvent(QueueEventTypes.RecoveryStarted, project, runId, null, null, null, reason), cancellationToken);
    }

    private QueueEvent NewEvent(string type, ProjectProfile project, string? runId, string? stepId, string? sessionId, string? taskFile, string? message)
    {
        return new QueueEvent
        {
            Type = type,
            ProjectId = project.Id,
            RunId = runId,
            StepId = stepId,
            SessionId = sessionId,
            TaskFile = taskFile,
            Message = message,
            CreatedAt = clock.Now
        };
    }

    private async Task<ProjectProfile?> ResolveProjectAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var project = string.IsNullOrWhiteSpace(command.ProjectId)
            ? await projectRegistry.GetActiveAsync(command.ConfigPath, cancellationToken)
            : await projectRegistry.GetByIdAsync(command.ConfigPath, command.ProjectId, cancellationToken);
        if (project is null && !string.IsNullOrWhiteSpace(command.ProjectId))
        {
            reporter.Warning($"Проект '{command.ProjectId}' не найден. activeProjectId не изменён.");
        }

        return project;
    }

    private int PrintRegistryResult(bool isSuccess, string? message)
    {
        if (isSuccess)
        {
            reporter.Info(message ?? "Готово.");
            return 0;
        }

        reporter.Warning(message ?? "Операция не выполнена.");
        return 2;
    }

    private int Unknown(string? commandName)
    {
        reporter.Error($"Неизвестная команда: {commandName}");
        PrintHelp();
        return 1;
    }

    private void PrintHelp()
    {
        reporter.Info("OpenCodeQueue — последовательный запуск Markdown-промптов в OpenCode.");
        reporter.Info("Использование:");
        reporter.Info("  opencode-queue");
        reporter.Info("  opencode-queue --help");
        reporter.Info("  opencode-queue menu --config opencode-queue.json");
        reporter.Info("  opencode-queue run --config opencode-queue.json [--project <id>] [--once]");
        reporter.Info("  opencode-queue resume --config opencode-queue.json [--project <id>]");
        reporter.Info("  opencode-queue status --config opencode-queue.json [--project <id>]");
        reporter.Info("  opencode-queue list --config opencode-queue.json [--project <id>]");
        reporter.Info("  opencode-queue validate --config opencode-queue.json [--project <id>]");
        reporter.Info("  opencode-queue doctor --config opencode-queue.json [--project <id>]");
        reporter.Info("  opencode-queue abort --config opencode-queue.json [--project <id>]");
        reporter.Info("  opencode-queue project list --config opencode-queue.json");
        reporter.Info("  opencode-queue project current --config opencode-queue.json");
        reporter.Info("  opencode-queue project select <id> --config opencode-queue.json");
        reporter.Info("  opencode-queue project add --config opencode-queue.json");
        reporter.Info("  opencode-queue project remove <id> --config opencode-queue.json");
        reporter.Info("  opencode-queue project update <id> --config opencode-queue.json");
        reporter.Info("  opencode-queue project discover --config opencode-queue.json");
    }
}
