using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Discovery;
using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Cli.ConsoleUi;

public sealed class ProjectProfilePrompt(IConsoleReporter reporter)
{
    public ProjectProfile? ReadNewProject(bool askOpenCodeOverrides)
    {
        var id = reporter.ReadLine("Id проекта: ");
        var displayName = reporter.ReadLine("Название проекта [как id]: ");
        var projectDir = reporter.ReadLine("Путь к projectDir: ");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(projectDir))
        {
            reporter.Warning("Id и projectDir обязательны. Изменения не сохранены.");
            return null;
        }

        var fullProjectDir = Path.GetFullPath(projectDir.Trim());
        if (!Directory.Exists(fullProjectDir) && !reporter.Confirm($"projectDir не существует: {fullProjectDir}. Создать? [y/N]: "))
        {
            reporter.Warning("Изменения не сохранены.");
            return null;
        }

        Directory.CreateDirectory(fullProjectDir);
        var promptsDir = reporter.AskDirectory("Папка prompts", Path.Combine(fullProjectDir, "prompts"), fullProjectDir);
        var qualityDir = reporter.AskDirectory("Папка quality/reviews", Path.Combine(fullProjectDir, "quality"), fullProjectDir);
        var stateDir = reporter.AskDirectory("Папка состояния", Path.Combine(fullProjectDir, ".queue"), fullProjectDir);

        var settings = new OpenCodeSettings();
        if (askOpenCodeOverrides)
        {
            var serverUrl = reporter.ReadLine($"OpenCode serverUrl [{settings.ServerUrl}]: ");
            var executable = reporter.ReadLine($"OpenCode executable [{settings.OpenCodeExecutable}]: ");
            settings = settings with
            {
                ServerUrl = string.IsNullOrWhiteSpace(serverUrl) ? settings.ServerUrl : serverUrl.Trim(),
                OpenCodeExecutable = string.IsNullOrWhiteSpace(executable) ? settings.OpenCodeExecutable : executable.Trim()
            };
        }

        return new ProjectProfile
        {
            Id = id.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? id.Trim() : displayName.Trim(),
            ProjectDir = fullProjectDir,
            PromptsDir = promptsDir,
            QualityDir = qualityDir,
            StateDir = stateDir,
            OpenCodeOverrides = settings
        };
    }

    public ProjectProfile ReadDiscoveredProject(DiscoveredProject discovered, string projectId, OpenCodeSettings defaults)
    {
        var projectDir = Path.GetFullPath(discovered.ProjectDir ?? throw new InvalidOperationException("projectDir не найден."));
        var displayName = reporter.ReadLine($"Название проекта [{discovered.DisplayName}]: ");
        var promptsDir = reporter.AskDirectory("Папка prompts", Path.Combine(projectDir, "prompts"), projectDir);
        var qualityDir = reporter.AskDirectory("Папка quality/reviews", Path.Combine(projectDir, "quality"), projectDir);
        var stateDir = reporter.AskDirectory("Папка состояния", Path.Combine(projectDir, ".queue"), projectDir);

        return new ProjectProfile
        {
            Id = projectId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? discovered.DisplayName : displayName.Trim(),
            ProjectDir = projectDir,
            PromptsDir = promptsDir,
            QualityDir = qualityDir,
            StateDir = stateDir,
            OpenCodeOverrides = defaults
        };
    }

    public ProjectProfile ReadUpdatedProject(ProjectProfile current)
    {
        var displayName = reporter.ReadLine($"Название проекта [{current.DisplayName ?? current.Id.Value}]: ");
        var projectDir = reporter.ReadLine($"Путь к projectDir [{current.ProjectDir}]: ");
        var promptsDir = reporter.ReadLine($"Папка prompts [{current.PromptsDir}]: ");
        var qualityDir = reporter.ReadLine($"Папка quality/reviews [{current.QualityDir ?? "quality"}]: ");
        var stateDir = reporter.ReadLine($"Папка состояния [{current.StateDir}]: ");
        var serverUrl = reporter.ReadLine($"OpenCode serverUrl [{current.OpenCodeOverrides.ServerUrl}]: ");
        var executable = reporter.ReadLine($"OpenCode executable [{current.OpenCodeOverrides.OpenCodeExecutable}]: ");

        var updatedProjectDir = string.IsNullOrWhiteSpace(projectDir) ? current.ProjectDir : Path.GetFullPath(projectDir.Trim());

        return current with
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? current.DisplayName : displayName.Trim(),
            ProjectDir = updatedProjectDir,
            PromptsDir = string.IsNullOrWhiteSpace(promptsDir) ? current.PromptsDir : ConsoleInteraction.ResolvePath(promptsDir.Trim(), updatedProjectDir),
            QualityDir = string.IsNullOrWhiteSpace(qualityDir) ? current.QualityDir : ConsoleInteraction.ResolvePath(qualityDir.Trim(), updatedProjectDir),
            StateDir = string.IsNullOrWhiteSpace(stateDir) ? current.StateDir : ConsoleInteraction.ResolvePath(stateDir.Trim(), updatedProjectDir),
            OpenCodeOverrides = current.OpenCodeOverrides with
            {
                ServerUrl = string.IsNullOrWhiteSpace(serverUrl) ? current.OpenCodeOverrides.ServerUrl : serverUrl.Trim(),
                OpenCodeExecutable = string.IsNullOrWhiteSpace(executable) ? current.OpenCodeOverrides.OpenCodeExecutable : executable.Trim()
            }
        };
    }
}
