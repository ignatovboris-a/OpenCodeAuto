namespace OpenCodeQueue.Core.Configuration;

public static class AppConfigValidator
{
    public static IReadOnlyList<string> Validate(AppConfig config)
    {
        var errors = new List<string>();

        if (config.SchemaVersion <= 0)
        {
            errors.Add("schemaVersion должен быть положительным числом.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in config.Projects)
        {
            errors.AddRange(ValidateProject(project));
            if (!string.IsNullOrWhiteSpace(project.Id.Value) && !ids.Add(project.Id.Value))
            {
                errors.Add($"Id проекта '{project.Id}' повторяется.");
            }
        }

        if (config.ActiveProjectId is not null && !ids.Contains(config.ActiveProjectId.Value.Value))
        {
            errors.Add($"Активный проект '{config.ActiveProjectId}' не найден в списке projects.");
        }

        if (config.Defaults.MaxInlinePromptChars <= 0)
        {
            errors.Add("defaults.maxInlinePromptChars должен быть больше нуля.");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateProject(ProjectProfile project)
    {
        var errors = new List<string>();
        var projectName = string.IsNullOrWhiteSpace(project.Id.Value) ? "<без id>" : project.Id.Value;

        if (!project.Id.IsValid)
        {
            errors.Add($"Проект '{projectName}': id должен быть стабильным slug без пробелов.");
        }

        RequirePath(project.ProjectDir, $"Проект '{projectName}': projectDir обязателен.", errors);

        if (project.OpenCodeOverrides.MaxInlinePromptChars <= 0)
        {
            errors.Add($"Проект '{projectName}': openCodeOverrides.maxInlinePromptChars должен быть больше нуля.");
        }

        return errors;
    }

    private static void RequirePath(string? path, string message, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(message);
        }
    }
}
