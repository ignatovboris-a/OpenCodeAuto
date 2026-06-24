using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Cli.ConsoleUi;

public sealed class InteractiveMenu(IConsoleReporter reporter, IProjectRegistry projectRegistry)
{
    public async Task<int> RunAsync(string configPath, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var activeProject = await projectRegistry.GetActiveAsync(configPath, cancellationToken);
            reporter.Info("");
            reporter.Info("OpenCodeQueue");
            reporter.Info(activeProject is null ? "Текущий проект: не выбран" : $"Текущий проект: {activeProject.Id} ({activeProject.ProjectDir})");
            reporter.Info("1. Запустить очередь");
            reporter.Info("2. Запустить одну задачу");
            reporter.Info("3. Восстановить активный run");
            reporter.Info("4. Статус");
            reporter.Info("5. Список задач");
            reporter.Info("6. Выбор/смена проекта");
            reporter.Info("7. Добавить проект");
            reporter.Info("8. Диагностика");
            reporter.Info("0. Выход");

            var choice = reporter.ReadLine("Выберите действие: ");
            switch (choice)
            {
                case "1":
                    reporter.Warning("Запуск очереди пока не реализован. Используйте команду run для проверки composition root.");
                    break;
                case "2":
                    reporter.Warning("Запуск одной задачи пока не реализован.");
                    break;
                case "3":
                    reporter.Warning("Восстановление run пока не реализовано.");
                    break;
                case "4":
                    reporter.Warning("Статус workflow пока не реализован.");
                    break;
                case "5":
                    reporter.Warning("Список задач доступен через команду list; интеграция меню будет расширена позже.");
                    break;
                case "6":
                    await SelectProjectAsync(configPath, cancellationToken);
                    break;
                case "7":
                    reporter.Warning("Добавление проекта из меню будет расширено позже. Сейчас используйте: project add --config <file>.");
                    break;
                case "8":
                    reporter.Warning("Диагностика пока не реализована.");
                    break;
                case "0":
                    reporter.Info("Выход.");
                    return 0;
                default:
                    reporter.Warning("Неизвестный пункт меню.");
                    break;
            }
        }

        return 130;
    }

    private async Task SelectProjectAsync(string configPath, CancellationToken cancellationToken)
    {
        var projects = await projectRegistry.ListAsync(configPath, cancellationToken);
        if (projects.Count == 0)
        {
            reporter.Warning("В registry пока нет проектов. Добавьте проект через project add.");
            return;
        }

        foreach (var project in projects)
        {
            reporter.Info($"{project.Id}: {project.ProjectDir}");
        }

        var id = reporter.ReadLine("Введите id проекта: ");
        if (string.IsNullOrWhiteSpace(id))
        {
            reporter.Warning("Id проекта не указан.");
            return;
        }

        var result = await projectRegistry.SelectAsync(configPath, id.Trim(), cancellationToken);
        if (result.IsSuccess)
        {
            reporter.Info(result.Message ?? "Активный проект выбран.");
        }
        else
        {
            reporter.Warning(result.Message ?? "Проект не выбран.");
        }
    }
}
