using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Discovery;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Prompts;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Infrastructure;

namespace OpenCodeQueue.Cli.ConsoleUi;

public sealed class ProjectConsolePresenter(IConsoleReporter reporter)
{
    public void PrintStatus(ProjectProfile project, int taskCount, int qualityCount)
    {
        reporter.Info($"Проект: {project.Id}");
        reporter.Info($"Название: {project.DisplayName ?? project.Id.Value}");
        reporter.Info($"projectDir: {project.ProjectDir}");
        reporter.Info($"promptsDir: {ProjectPaths.PromptsDir(project)}");
        reporter.Info($"qualityDir: {ProjectPaths.QualityDir(project)}");
        reporter.Info($"stateDir: {ProjectPaths.StateDir(project)}");
        reporter.Info($"Очередь задач: prompts = {taskCount}, quality = {qualityCount}");
    }

    public void PrintStatus(ProjectProfile project, PromptDiscoveryResult discovery, QueueState? state, RunManifest? manifest)
    {
        PrintStatus(project, discovery.TaskPrompts.Count, discovery.QualityPrompts.Count);
        reporter.Info(string.IsNullOrWhiteSpace(state?.ActiveRunId) ? "Активный run: нет активного run" : $"Активный run: {state.ActiveRunId}");
        reporter.Info($"manifest: {(manifest is null ? "нет" : ProjectPaths.RunManifestFile(project, manifest.RunId))}");
        reporter.Info($"журнал событий: {ProjectPaths.EventsFile(project)}");
        if (manifest is null)
        {
            return;
        }

        reporter.Info($"статус run: {manifest.Status}");
        reporter.Info($"session id: {manifest.SessionId ?? "нет"}");
        reporter.Info($"текущий step: {CurrentStepText(manifest)}");
        reporter.Info("Steps:");
        foreach (var step in manifest.Steps.OrderBy(step => step.Order))
        {
            reporter.Info($"  {step.Order}. {step.Id} тип={step.Kind} статус={step.Status} попыток={step.AttemptCount} начат={FormatTime(step.StartedAt)} завершён={FormatTime(step.CompletedAt)}");
        }

        if (!string.IsNullOrWhiteSpace(manifest.LastError))
        {
            reporter.Warning("Последняя ошибка: " + manifest.LastError);
        }

        reporter.Info($"логи: {Path.Combine(ProjectPaths.RunDir(project, manifest.RunId), "logs")}");
    }

    public void PrintPromptList(ProjectProfile project, PromptDiscoveryResult discovery)
    {
        reporter.Info($"Проект: {project.Id}");
        reporter.Info($"Путь: {project.ProjectDir}");
        reporter.Info("Ожидающие task prompts в порядке выполнения:");
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

        reporter.Info($"Итого: task prompts = {discovery.TaskPrompts.Count}, quality prompts = {discovery.QualityPrompts.Count}, предупреждений = {discovery.Warnings.Count}.");
    }

    public void PrintDiagnostics(ProjectProfile project)
    {
        reporter.PrintDirectoryStatus("projectDir", project.ProjectDir);
        reporter.PrintDirectoryStatus("promptsDir", ProjectPaths.PromptsDir(project));
        reporter.PrintDirectoryStatus("qualityDir", ProjectPaths.QualityDir(project));
        reporter.PrintDirectoryStatus("stateDir", ProjectPaths.StateDir(project));
    }

    public void PrintManifest(ProjectProfile project, RunManifest manifest)
    {
        reporter.Info($"run id: {manifest.RunId}");
        reporter.Info($"статус run: {manifest.Status}");
        reporter.Info($"session id: {manifest.SessionId ?? "не создана"}");
        reporter.Info($"текущий step: {CurrentStepText(manifest)}");
        reporter.Info($"manifest: {ProjectPaths.RunManifestFile(project, manifest.RunId)}");
        reporter.Info($"логи: {Path.Combine(ProjectPaths.RunDir(project, manifest.RunId), "logs")}");
        if (!string.IsNullOrWhiteSpace(manifest.LastError))
        {
            reporter.Warning("Последняя ошибка: " + manifest.LastError);
        }
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

    private static string CurrentStepText(RunManifest manifest)
    {
        if (manifest.CurrentStepIndex >= 0 && manifest.CurrentStepIndex < manifest.Steps.Count)
        {
            var step = manifest.Steps[manifest.CurrentStepIndex];
            return $"{step.Id} ({step.Status})";
        }

        return "нет";
    }

    private static string FormatTime(DateTimeOffset? value) => value?.ToString("u") ?? "нет";
}
