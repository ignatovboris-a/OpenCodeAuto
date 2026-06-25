using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Discovery;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Cli.ConsoleUi;

public sealed class ProjectConsolePresenter(IConsoleReporter reporter)
{
    public void PrintStatus(ProjectProfile project, int taskCount, int qualityCount)
    {
        reporter.Info($"Проект: {project.Id}");
        reporter.Info($"Название: {project.DisplayName ?? project.Id.Value}");
        reporter.Info($"Путь: {project.ProjectDir}");
        reporter.Info($"Очередь задач: prompts = {taskCount}, quality = {qualityCount}");
        reporter.Info("Активный run: нет");
    }

    public void PrintPromptList(ProjectProfile project, PromptDiscoveryResult discovery)
    {
        reporter.Info($"Проект: {project.Id}");
        reporter.Info($"Путь: {project.ProjectDir}");
        reporter.Info("Pending task prompts в порядке выполнения:");
        if (discovery.TaskPrompts.Count == 0)
        {
            reporter.Info("Очередь задач пуста.");
        }
        else
        {
            foreach (var prompt in discovery.TaskPrompts)
            {
                reporter.Info($"  {prompt.Prefix}  {prompt.FileName}");
            }
        }

        reporter.Info("Quality prompts в порядке выполнения:");
        if (discovery.QualityPrompts.Count == 0)
        {
            reporter.Info("  Нет проверочных prompt-файлов.");
        }
        else
        {
            foreach (var prompt in discovery.QualityPrompts)
            {
                reporter.Info($"  {prompt.Prefix}  {prompt.FileName}");
            }
        }

        if (discovery.Warnings.Count > 0)
        {
            reporter.Info("Предупреждения discovery:");
            foreach (var warning in discovery.Warnings)
            {
                reporter.Warning(warning);
            }
        }
    }

    public void PrintDiagnostics(ProjectProfile project)
    {
        reporter.PrintDirectoryStatus("projectDir", project.ProjectDir);
        reporter.PrintDirectoryStatus("promptsDir", ConsoleInteraction.ResolvePath(project.PromptsDir, project.ProjectDir));
        reporter.PrintDirectoryStatus("qualityDir", ConsoleInteraction.ResolvePath(project.QualityDir ?? project.ReviewsDir ?? "quality", project.ProjectDir));
        reporter.PrintDirectoryStatus("stateDir", ConsoleInteraction.ResolvePath(project.StateDir, project.ProjectDir));
    }

    public void PrintDiscoveredProjects(IReadOnlyList<DiscoveredProject> projects)
    {
        foreach (var project in projects)
        {
            reporter.Info($"{project.Source}: {project.DisplayName}; path = {project.ProjectDir ?? "требуется ручной ввод"}; confidence = {project.Confidence}; selectable = {(project.CanSelectDirectly ? "да" : "нет")}");
            foreach (var warning in project.Warnings)
            {
                reporter.Warning(warning);
            }
        }
    }
}
