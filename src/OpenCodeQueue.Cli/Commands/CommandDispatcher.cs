using OpenCodeQueue.Cli.ConsoleUi;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Cli.Commands;

public sealed class CommandDispatcher(
    IConsoleReporter reporter,
    IAppConfigStore configStore,
    IProjectRegistry projectRegistry,
    IPromptRepository promptRepository,
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
            "resume" => Stub("Восстановление активного run пока не реализовано."),
            "status" => await StatusAsync(command, cancellationToken),
            "list" => await ListPromptsAsync(command, cancellationToken),
            "validate" => await ValidateAsync(command.ConfigPath, cancellationToken),
            "doctor" => Stub("Диагностика окружения пока не реализована."),
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

        reporter.Info($"Проект: {project.Id}");
        reporter.Info($"Путь: {project.ProjectDir}");
        reporter.Info("Статус очереди пока недоступен: state/recovery будут реализованы позже.");
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

        var tasks = await promptRepository.GetTaskPromptsAsync(project, cancellationToken);
        var quality = await promptRepository.GetQualityPromptsAsync(project, cancellationToken);
        reporter.Info($"Основные prompt-файлы: {tasks.Count}");
        foreach (var prompt in tasks)
        {
            reporter.Info($"  {prompt.FileName}");
        }

        reporter.Info($"Проверочные prompt-файлы: {quality.Count}");
        foreach (var prompt in quality)
        {
            reporter.Info($"  {prompt.FileName}");
        }

        return 0;
    }

    private async Task<int> ValidateAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            reporter.Warning($"Файл конфигурации не найден: {configPath}");
            return 2;
        }

        var config = await configStore.LoadAsync(configPath, cancellationToken);
        reporter.Info($"Файл конфигурации найден: {configPath}");
        reporter.Info($"Проектов в registry: {config?.Projects.Count ?? 0}");
        reporter.Info(config?.ActiveProjectId is null ? "Активный проект не выбран." : $"Активный проект: {config.ActiveProjectId}");
        return 0;
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
        var id = reporter.ReadLine("Id проекта: ");
        var projectDir = reporter.ReadLine("Путь к projectDir: ");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(projectDir))
        {
            reporter.Warning("Id и projectDir обязательны.");
            return 2;
        }

        var promptsDir = reporter.ReadLine("Папка prompts [prompts]: ");
        var qualityDir = reporter.ReadLine("Папка quality/reviews [quality]: ");
        var stateDir = reporter.ReadLine("Папка состояния [.queue]: ");

        var project = new ProjectProfile
        {
            Id = id.Trim(),
            ProjectDir = projectDir.Trim(),
            PromptsDir = string.IsNullOrWhiteSpace(promptsDir) ? "prompts" : promptsDir.Trim(),
            QualityDir = string.IsNullOrWhiteSpace(qualityDir) ? "quality" : qualityDir.Trim(),
            StateDir = string.IsNullOrWhiteSpace(stateDir) ? ".queue" : stateDir.Trim()
        };

        var result = await projectRegistry.AddOrUpdateAsync(configPath, project, cancellationToken);
        return PrintRegistryResult(result.IsSuccess, result.Message);
    }

    private async Task<ProjectProfile?> ResolveProjectAsync(CliCommand command, CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(command.ProjectId)
            ? await projectRegistry.GetActiveAsync(command.ConfigPath, cancellationToken)
            : await projectRegistry.GetByIdAsync(command.ConfigPath, command.ProjectId, cancellationToken);
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

    private int Stub(string message)
    {
        reporter.Warning(message);
        return 0;
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
        reporter.Info("  opencode-queue project list --config opencode-queue.json");
        reporter.Info("  opencode-queue project current --config opencode-queue.json");
        reporter.Info("  opencode-queue project select <id> --config opencode-queue.json");
        reporter.Info("  opencode-queue project add --config opencode-queue.json");
    }
}
