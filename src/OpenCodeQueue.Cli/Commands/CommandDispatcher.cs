using OpenCodeQueue.Cli.ConsoleUi;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Core.Workflow;
using OpenCodeQueue.Infrastructure;

namespace OpenCodeQueue.Cli.Commands;

public sealed class CommandDispatcher(
    IConsoleReporter reporter,
    IAppConfigStore configStore,
    IProjectRegistry projectRegistry,
    IPromptRepository promptRepository,
    IStateStore stateStore,
    IRunLock runLock,
    IOpenCodeClient openCodeClient,
    IQueueUseCases queueUseCases,
    IProjectDiscoveryService projectDiscoveryService,
    ProjectProfilePrompt projectProfilePrompt,
    ProjectConsolePresenter projectPresenter,
    OperationResultPrinter operationResultPrinter,
    ProjectDiagnosticsValidator diagnosticsValidator,
    InteractiveMenu interactiveMenu)
{
    public async Task<int> DispatchAsync(CliCommand command, CancellationToken cancellationToken)
    {
        if (command.HelpRequested || string.Equals(command.Name, "help", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp();
            return QueueExitCodes.Success;
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
        var result = await queueUseCases.RunQueueAsync(command.ConfigPath, command.ProjectId, command.Once, cancellationToken);
        return operationResultPrinter.Print(result);
    }

    private async Task<int> StatusAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var result = await queueUseCases.GetStatusAsync(command.ConfigPath, command.ProjectId, cancellationToken);
        if (result.Project is not null && result.Discovery is not null)
        {
            projectPresenter.PrintStatus(result.Project, result.Discovery, result.State, result.Manifest);
        }

        return operationResultPrinter.Print(result);
    }

    private async Task<int> ResumeAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var result = await queueUseCases.ResumeAsync(command.ConfigPath, command.ProjectId, cancellationToken);
        return operationResultPrinter.Print(result);
    }

    private async Task<int> AbortAsync(CliCommand command, CancellationToken cancellationToken)
    {
        if (!reporter.Confirm("Перевести active run в Aborted без удаления данных? [y/N]: "))
        {
            reporter.Warning("Abort отменён.");
            return QueueExitCodes.ValidationError;
        }
        var result = await queueUseCases.AbortRunAsync(command.ConfigPath, command.ProjectId, cancellationToken);
        return operationResultPrinter.Print(result);
    }

    private async Task<int> ListPromptsAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var result = await queueUseCases.ListPromptsAsync(command.ConfigPath, command.ProjectId, cancellationToken);
        if (result.Project is not null && result.Discovery is not null)
        {
            projectPresenter.PrintPromptList(result.Project, result.Discovery);
        }

        return operationResultPrinter.Print(result);
    }

    private async Task<int> ValidateAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var configPath = command.ConfigPath;
        if (!File.Exists(configPath))
        {
            reporter.Warning($"Файл конфигурации не найден: {configPath}");
            return QueueExitCodes.ValidationError;
        }

        var config = await configStore.LoadAsync(configPath, cancellationToken);
        reporter.Info($"Файл конфигурации найден: {configPath}");
        reporter.Info($"Проектов в registry: {config?.Projects.Count ?? 0}");
        reporter.Info(config?.ActiveProjectId is null ? "Активный проект не выбран." : $"Активный проект: {config.ActiveProjectId}");
        if (config is null)
        {
            reporter.Warning("Файл конфигурации пуст или не может быть прочитан.");
            return QueueExitCodes.ValidationError;
        }

        var errors = AppConfigValidator.Validate(config);
        if (errors.Count == 0)
        {
            reporter.Info("Конфигурация валидна.");
            var project = await ResolveProjectAsync(command, cancellationToken);
            if (project is null)
            {
                reporter.Warning(string.IsNullOrWhiteSpace(command.ProjectId)
                    ? "Активный проект не выбран. Укажите --project <id> или выполните project select."
                    : $"Проект '{command.ProjectId}' не найден.");
                return QueueExitCodes.ValidationError;
            }

            var discovery = await promptRepository.DiscoverAsync(project, cancellationToken);
            foreach (var warning in discovery.Warnings)
            {
                reporter.Warning(warning);
            }

            var validation = diagnosticsValidator.Validate(project, discovery);
            foreach (var error in validation.Errors)
            {
                reporter.Warning("- " + error);
            }

            foreach (var warning in validation.Warnings)
            {
                reporter.Warning("- " + warning);
            }

            return discovery.Warnings.Count == 0 && validation.Errors.Count == 0 ? QueueExitCodes.Success : QueueExitCodes.ValidationError;
        }

        reporter.Warning("Найдены ошибки конфигурации:");
        foreach (var error in errors)
        {
            reporter.Warning("- " + error);
        }

        return QueueExitCodes.ValidationError;
    }

    private async Task<int> ProjectListAsync(string configPath, CancellationToken cancellationToken)
    {
        var projects = await projectRegistry.ListAsync(configPath, cancellationToken);
        if (projects.Count == 0)
        {
            reporter.Info("В registry пока нет проектов.");
            return QueueExitCodes.Success;
        }

        foreach (var project in projects)
        {
            reporter.Info($"{project.Id}: {project.ProjectDir}");
        }

        return QueueExitCodes.Success;
    }

    private async Task<int> ProjectCurrentAsync(string configPath, CancellationToken cancellationToken)
    {
        var project = await projectRegistry.GetActiveAsync(configPath, cancellationToken);
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return QueueExitCodes.ValidationError;
        }

        reporter.Info($"Активный проект: {project.Id}");
        reporter.Info(project.ProjectDir);
        return QueueExitCodes.Success;
    }

    private async Task<int> ProjectSelectAsync(string configPath, string? projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            reporter.Warning("Укажите id проекта: project select <id> --config opencode-queue.json");
            return QueueExitCodes.ValidationError;
        }

        var result = await projectRegistry.SelectAsync(configPath, projectId, cancellationToken);
        return PrintRegistryResult(result.IsSuccess, result.Message);
    }

    private async Task<int> ProjectAddAsync(string configPath, CancellationToken cancellationToken)
    {
        var project = projectProfilePrompt.ReadNewProject(askOpenCodeOverrides: true);
        if (project is null)
        {
            return QueueExitCodes.ValidationError;
        }

        reporter.PrintProjectForConfirmation(project);
        if (!reporter.Confirm("Сохранить проект в config? [y/N]: "))
        {
            reporter.Warning("Проект не сохранён.");
            return QueueExitCodes.ValidationError;
        }

        var result = await projectRegistry.AddOrUpdateAsync(configPath, project, cancellationToken);
        return PrintRegistryResult(result.IsSuccess, result.Message);
    }

    private async Task<int> ProjectRemoveAsync(string configPath, string? projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            reporter.Warning("Укажите id проекта: project remove <id> --config opencode-queue.json");
            return QueueExitCodes.ValidationError;
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
            return QueueExitCodes.ValidationError;
        }

        var current = await projectRegistry.GetByIdAsync(configPath, projectId.Trim(), cancellationToken);
        if (current is null)
        {
            reporter.Warning("Проект с таким id не найден.");
            return QueueExitCodes.ValidationError;
        }

        var updated = projectProfilePrompt.ReadUpdatedProject(current);
        reporter.PrintProjectForConfirmation(updated);
        if (!reporter.Confirm("Сохранить изменения проекта в config? [y/N]: "))
        {
            reporter.Warning("Проект не изменён.");
            return QueueExitCodes.ValidationError;
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
            return QueueExitCodes.Success;
        }

        projectPresenter.PrintDiscoveredProjects(discovered);
        reporter.Info("Ручной ввод пути доступен через project add.");
        return QueueExitCodes.Success;
    }

    private async Task<int> DoctorAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var project = await ResolveProjectAsync(command, cancellationToken);
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return QueueExitCodes.ValidationError;
        }

        reporter.Info($"Диагностика проекта: {project.Id}");
        var validateCode = await ValidateAsync(command, cancellationToken);
        if (validateCode != QueueExitCodes.Success)
        {
            reporter.Warning("Runtime-проверки OpenCode пропущены: сначала исправьте ошибки validate.");
            return validateCode;
        }

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
            return QueueExitCodes.ValidationError;
        }

        try
        {
            await openCodeClient.EnsureReadyAsync(project, cancellationToken);
            reporter.Info("Runtime-проверка OpenCode: доступен, выбранный projectDir подтверждён.");
        }
        catch (OpenCodeProjectMismatchException exception)
        {
            reporter.Warning("OpenCode server открыт для другого проекта. Очередь автоматически не запускается.");
            reporter.Warning(exception.Message);
            return QueueExitCodes.OpenCodeUnavailableOrProjectMismatch;
        }
        catch (OpenCodeClientException exception)
        {
            reporter.Warning("OpenCode недоступен: " + exception.Message);
            return QueueExitCodes.OpenCodeUnavailableOrProjectMismatch;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            reporter.Warning("Runtime-проверка OpenCode не прошла: " + exception.Message);
            return QueueExitCodes.OpenCodeUnavailableOrProjectMismatch;
        }

        return validateCode == QueueExitCodes.Success ? QueueExitCodes.Success : validateCode;
    }

    private async Task PrintStateStatusAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadQueueStateAsync(project, cancellationToken);
        reporter.Info($"stateDir: {ProjectPaths.StateDir(project)}");
        if (state is null || string.IsNullOrWhiteSpace(state.ActiveRunId))
        {
            reporter.Info("активный run: нет");
            return;
        }

        reporter.Info($"активный run: {state.ActiveRunId}");
        var manifest = await stateStore.LoadRunManifestAsync(project, state.ActiveRunId, cancellationToken);
        if (manifest is null)
        {
            reporter.Warning("manifest.json отсутствует. Требуется ручная проверка, новая задача не выбирается.");
            return;
        }

        PrintManifest(project, manifest);
    }

    private void PrintManifest(ProjectProfile project, RunManifest manifest)
    {
        projectPresenter.PrintManifest(project, manifest);
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
            return QueueExitCodes.Success;
        }

        reporter.Warning(message ?? "Операция не выполнена.");
        return QueueExitCodes.ValidationError;
    }

    private int Unknown(string? commandName)
    {
        reporter.Error($"Неизвестная команда: {commandName}");
        PrintHelp();
        return QueueExitCodes.UnexpectedError;
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
