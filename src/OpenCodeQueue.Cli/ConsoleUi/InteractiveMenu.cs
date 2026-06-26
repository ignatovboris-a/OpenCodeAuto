using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Discovery;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Prompts;
using OpenCodeQueue.Core.Workflow;
using OpenCodeQueue.Infrastructure;

namespace OpenCodeQueue.Cli.ConsoleUi;

public sealed class InteractiveMenu(
    IConsoleReporter reporter,
    IProjectRegistry projectRegistry,
    IPromptRepository promptRepository,
    IProjectDiscoveryService projectDiscoveryService,
    IAppConfigStore configStore,
    IStateStore stateStore,
    IQueueUseCases queueUseCases,
    ProjectProfilePrompt projectProfilePrompt,
    ProjectConsolePresenter projectPresenter,
    OperationResultPrinter operationResultPrinter)
{
    public async Task<int> RunAsync(string configPath, CancellationToken cancellationToken)
    {
        await EnsureProjectSelectedAsync(configPath, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var activeProject = await projectRegistry.GetActiveAsync(configPath, cancellationToken);
            var discovery = activeProject is null ? new PromptDiscoveryResult([], [], []) : await promptRepository.DiscoverAsync(activeProject, cancellationToken);
            var state = activeProject is null ? null : await stateStore.LoadQueueStateAsync(activeProject, cancellationToken);
            var hasActiveRun = !string.IsNullOrWhiteSpace(state?.ActiveRunId);

            reporter.Info("");
            reporter.Info("OpenCodeQueue");
            reporter.Info(activeProject is null ? "Активный проект: не выбран" : $"Активный проект: {activeProject.DisplayName ?? activeProject.Id.Value}");
            reporter.Info(activeProject is null ? "Путь проекта: не выбран" : $"Путь проекта: {activeProject.ProjectDir}");
            if (activeProject is not null)
            {
                reporter.Info($"promptsDir: {ProjectPaths.PromptsDir(activeProject)}");
                reporter.Info($"qualityDir: {ProjectPaths.QualityDir(activeProject)}");
                reporter.Info($"stateDir: {ProjectPaths.StateDir(activeProject)}");
            }
            reporter.Info(activeProject is null ? "Очередь задач: проект не выбран" : $"Очередь задач: prompts = {discovery.TaskPrompts.Count}, quality = {discovery.QualityPrompts.Count}");
            reporter.Info(hasActiveRun ? $"Активный run: {state!.ActiveRunId}" : "Активный run: нет");
            reporter.Info("");
            reporter.Info("1. Запустить очередь до конца");
            reporter.Info("2. Запустить одну следующую задачу");
            reporter.Info("3. Продолжить/восстановить активный run");
            reporter.Info("4. Показать статус");
            reporter.Info("5. Показать список задач и quality prompts");
            reporter.Info("6. Выбор/смена проекта");
            reporter.Info("7. Добавить проект");
            reporter.Info("8. Диагностика проекта");
            reporter.Info("9. Настройки проекта");
            reporter.Info("0. Выход");

            var choice = reporter.ReadLine("Выберите действие: ");
            switch (choice)
            {
                case "1":
                    await RunQueueFromMenuAsync(configPath, activeProject, hasActiveRun, false, cancellationToken);
                    break;
                case "2":
                    await RunQueueFromMenuAsync(configPath, activeProject, hasActiveRun, true, cancellationToken);
                    break;
                case "3":
                    operationResultPrinter.Print(await queueUseCases.ResumeAsync(configPath, activeProject?.Id.Value, cancellationToken));
                    break;
                case "4":
                    await ShowStatusAsync(configPath, activeProject, cancellationToken);
                    break;
                case "5":
                    ShowPromptList(activeProject, discovery);
                    break;
                case "6":
                    await SelectProjectAsync(configPath, cancellationToken);
                    break;
                case "7":
                    await AddProjectAsync(configPath, cancellationToken);
                    break;
                case "8":
                    ShowDiagnostics(activeProject);
                    break;
                case "9":
                    ShowSettings(activeProject);
                    break;
                case "0":
                    reporter.Info("Выход.");
                    return QueueExitCodes.Success;
                default:
                    reporter.Warning("Неизвестный пункт меню.");
                    break;
            }
        }

        return QueueExitCodes.Cancelled;
    }

    private async Task EnsureProjectSelectedAsync(string configPath, CancellationToken cancellationToken)
    {
        var config = await configStore.LoadAsync(configPath, cancellationToken);
        var activeProject = await projectRegistry.GetActiveAsync(configPath, cancellationToken);
        if (activeProject is not null && Directory.Exists(activeProject.ProjectDir))
        {
            return;
        }

        reporter.Info(config is null
            ? "Конфигурация OpenCodeQueue не найдена. Первый старт: выберите существующий проект или добавьте новый."
            : "Активный проект не выбран или недоступен. Выберите существующий проект или добавьте новый.");
        await ShowDiscoveryAsync(configPath, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            reporter.Info("1. Выбрать проект из registry");
            reporter.Info("2. Добавить проект вручную");
            reporter.Info("3. Повторить discovery");
            reporter.Info("0. Выйти без изменений");
            var choice = reporter.ReadLine("Выберите действие первого старта: ");
            switch (choice)
            {
                case "1":
                    await SelectProjectAsync(configPath, cancellationToken);
                    if (await projectRegistry.GetActiveAsync(configPath, cancellationToken) is not null)
                    {
                        return;
                    }
                    break;
                case "2":
                    await AddProjectAsync(configPath, cancellationToken);
                    if (await projectRegistry.GetActiveAsync(configPath, cancellationToken) is not null)
                    {
                        return;
                    }
                    break;
                case "3":
                    await ShowDiscoveryAsync(configPath, cancellationToken);
                    break;
                case "0":
                    return;
                default:
                    reporter.Warning("Неизвестный пункт меню.");
                    break;
            }
        }
    }

    private async Task SelectProjectAsync(string configPath, CancellationToken cancellationToken)
    {
        var projects = await projectRegistry.ListAsync(configPath, cancellationToken);
        var discoveredProjects = await DiscoverSelectableProjectsNotInRegistryAsync(configPath, projects, cancellationToken);
        var totalCount = projects.Count + discoveredProjects.Count;
        if (totalCount == 0)
        {
            reporter.Warning("OpenCode projects не найдены. Проверьте serverUrl/логин/пароль или добавьте проект вручную через пункт 7.");
            return;
        }

        var number = 1;
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            reporter.Info($"{number}. {project.DisplayName ?? project.Id.Value} ({project.ProjectDir}) [{project.Id}] registry");
            number++;
        }

        foreach (var project in discoveredProjects)
        {
            reporter.Info($"{number}. {project.DisplayName} ({project.ProjectDir}) OpenCode");
            number++;
        }
        reporter.Info("0. Назад без выбора");

        var selected = reporter.ReadLine("Выберите проект по номеру или введите id: ");
        if (string.IsNullOrWhiteSpace(selected) || selected.Trim() == "0")
        {
            reporter.Info("Выбор проекта отменён.");
            return;
        }

        var id = selected.Trim();
        if (int.TryParse(id, out var selectedNumber) && selectedNumber >= 1 && selectedNumber <= projects.Count)
        {
            id = projects[selectedNumber - 1].Id.Value;
        }
        else if (int.TryParse(id, out selectedNumber) && selectedNumber > projects.Count && selectedNumber <= totalCount)
        {
            await AddAndSelectDiscoveredProjectAsync(configPath, discoveredProjects[selectedNumber - projects.Count - 1], cancellationToken);
            return;
        }

        var result = await projectRegistry.SelectAsync(configPath, id, cancellationToken);
        if (result.IsSuccess)
        {
            reporter.Info(result.Message ?? "Активный проект выбран.");
        }
        else
        {
            reporter.Warning(result.Message ?? "Проект не выбран.");
        }
    }

    private async Task<IReadOnlyList<DiscoveredProject>> DiscoverSelectableProjectsNotInRegistryAsync(string configPath, IReadOnlyList<ProjectProfile> registryProjects, CancellationToken cancellationToken)
    {
        var registryPaths = registryProjects.Select(project => project.ProjectDir).ToArray();
        var discovered = await projectDiscoveryService.DiscoverAsync(configPath, cancellationToken);
        return discovered
            .Where(project => project.CanSelectDirectly && !string.IsNullOrWhiteSpace(project.ProjectDir))
            .Where(project => registryPaths.All(path => !ProjectPaths.AreSamePath(path, project.ProjectDir!)))
            .GroupBy(project => Path.GetFullPath(project.ProjectDir!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task AddAndSelectDiscoveredProjectAsync(string configPath, DiscoveredProject discovered, CancellationToken cancellationToken)
    {
        var config = await configStore.LoadOrCreateDefaultAsync(configPath, cancellationToken);
        var projectId = CreateUniqueProjectId(discovered, config.Projects);
        var projectDir = Path.GetFullPath(discovered.ProjectDir!);
        var project = new ProjectProfile
        {
            Id = projectId,
            DisplayName = discovered.DisplayName,
            ProjectDir = projectDir,
            PromptsDir = Path.Combine(projectDir, "prompts"),
            QualityDir = Path.Combine(projectDir, "quality"),
            StateDir = Path.Combine(projectDir, ".queue"),
            CompletedDir = Path.Combine(projectDir, ".queue", "completed"),
            OpenCodeOverrides = config.Defaults
        };

        Directory.CreateDirectory(project.PromptsDir);
        Directory.CreateDirectory(project.QualityDir!);
        Directory.CreateDirectory(project.StateDir);

        var addResult = await projectRegistry.AddOrUpdateAsync(configPath, project, cancellationToken);
        if (!addResult.IsSuccess)
        {
            reporter.Warning(addResult.Message ?? "Проект не сохранён.");
            return;
        }

        var selectResult = await projectRegistry.SelectAsync(configPath, project.Id.Value, cancellationToken);
        if (selectResult.IsSuccess)
        {
            reporter.Info($"Активный проект выбран: {project.DisplayName ?? project.Id.Value}");
        }
        else
        {
            reporter.Warning(selectResult.Message ?? "Проект сохранён, но не выбран.");
        }
    }

    private async Task AddProjectAsync(string configPath, CancellationToken cancellationToken)
    {
        var project = await ReadProjectFromOpenCodeOrManualAsync(configPath, cancellationToken);
        if (project is null)
        {
            return;
        }

        reporter.PrintProjectForConfirmation(project);
        if (!reporter.Confirm("Сохранить проект в config? [y/N]: "))
        {
            reporter.Warning("Изменения не сохранены.");
            return;
        }

        var result = await projectRegistry.AddOrUpdateAsync(configPath, project, cancellationToken);
        if (result.IsSuccess)
        {
            reporter.Info(result.Message ?? "Проект сохранён.");
        }
        else
        {
            reporter.Warning(result.Message ?? "Проект не сохранён.");
        }
    }

    private async Task<ProjectProfile?> ReadProjectFromOpenCodeOrManualAsync(string configPath, CancellationToken cancellationToken)
    {
        var discovered = await projectDiscoveryService.DiscoverAsync(configPath, cancellationToken);
        var selectable = discovered
            .Where(project => project.CanSelectDirectly && !string.IsNullOrWhiteSpace(project.ProjectDir))
            .GroupBy(project => Path.GetFullPath(project.ProjectDir!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (selectable.Length == 0)
        {
            reporter.Info("OpenCode не вернул selectable проекты. Доступен ручной ввод.");
            return projectProfilePrompt.ReadNewProject(askOpenCodeOverrides: true);
        }

        reporter.Info("Проекты, найденные через OpenCode:");
        for (var index = 0; index < selectable.Length; index++)
        {
            var project = selectable[index];
            reporter.Info($"{index + 1}. {project.DisplayName} ({project.ProjectDir})");
        }
        reporter.Info("m. Добавить проект вручную");
        reporter.Info("0. Назад без выбора");

        var choice = reporter.ReadLine("Выберите проект: ");
        if (string.IsNullOrWhiteSpace(choice) || choice.Trim() == "0")
        {
            reporter.Info("Добавление проекта отменено.");
            return null;
        }

        if (string.Equals(choice.Trim(), "m", StringComparison.OrdinalIgnoreCase))
        {
            return projectProfilePrompt.ReadNewProject(askOpenCodeOverrides: true);
        }

        if (!int.TryParse(choice.Trim(), out var number) || number < 1 || number > selectable.Length)
        {
            reporter.Warning("Неверный номер проекта.");
            return null;
        }

        var selected = selectable[number - 1];
        var config = await configStore.LoadOrCreateDefaultAsync(configPath, cancellationToken);
        var projectId = CreateUniqueProjectId(selected, config.Projects);
        return projectProfilePrompt.ReadDiscoveredProject(selected, projectId, config.Defaults);
    }

    private async Task ShowDiscoveryAsync(string configPath, CancellationToken cancellationToken)
    {
        var discovered = await projectDiscoveryService.DiscoverAsync(configPath, cancellationToken);
        if (discovered.Count == 0)
        {
            reporter.Info("Автоматически проекты не обнаружены. Ручной ввод пути доступен всегда.");
            return;
        }

        reporter.Info("Обнаруженные кандидаты:");
        projectPresenter.PrintDiscoveredProjects(discovered);
    }

    private static string CreateUniqueProjectId(DiscoveredProject discovered, IReadOnlyList<ProjectProfile> existingProjects)
    {
        var baseId = Slug(Path.GetFileName(discovered.ProjectDir?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? discovered.SuggestedId
            ?? discovered.DisplayName);
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "project";
        }

        var candidate = baseId;
        var suffix = 2;
        while (existingProjects.Any(project => string.Equals(project.Id.Value, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseId}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(ch => ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9' ? ch : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task RunQueueFromMenuAsync(string configPath, ProjectProfile? project, bool hasActiveRun, bool once, CancellationToken cancellationToken)
    {
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран. Доступны выбор проекта, добавление проекта, диагностика и выход.");
            return;
        }

        if (hasActiveRun)
        {
            reporter.Warning("Новый запуск заблокирован: есть active run. Используйте восстановление, статус или abort.");
            return;
        }

        var result = await queueUseCases.RunQueueAsync(configPath, project.Id.Value, once, cancellationToken);
        operationResultPrinter.Print(result);
    }

    private async Task ShowStatusAsync(string configPath, ProjectProfile? project, CancellationToken cancellationToken)
    {
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return;
        }

        var result = await queueUseCases.GetStatusAsync(configPath, project.Id.Value, cancellationToken);
        if (result.Project is not null && result.Discovery is not null)
        {
            projectPresenter.PrintStatus(result.Project, result.Discovery, result.State, result.Manifest);
        }

        operationResultPrinter.Print(result);
    }

    private void ShowPromptList(ProjectProfile? project, PromptDiscoveryResult discovery)
    {
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return;
        }

        projectPresenter.PrintPromptList(project, discovery);
    }

    private void ShowDiagnostics(ProjectProfile? project)
    {
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return;
        }

        projectPresenter.PrintDiagnostics(project);
    }

    private void ShowSettings(ProjectProfile? project)
    {
        if (project is null)
        {
            reporter.Warning("Активный проект не выбран.");
            return;
        }

        reporter.Info($"Id: {project.Id}");
        reporter.Info($"Название: {project.DisplayName ?? project.Id.Value}");
        reporter.Info($"OpenCode: {project.OpenCodeOverrides.Redacted()}");
    }

}
