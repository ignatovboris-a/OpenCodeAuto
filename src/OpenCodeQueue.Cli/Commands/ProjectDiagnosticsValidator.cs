using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Prompts;
using OpenCodeQueue.Infrastructure;

namespace OpenCodeQueue.Cli.Commands;

public sealed record ProjectDiagnosticsResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed class ProjectDiagnosticsValidator
{
    public ProjectDiagnosticsResult Validate(ProjectProfile project, PromptDiscoveryResult discovery)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!Directory.Exists(project.ProjectDir))
        {
            errors.Add($"projectDir не существует: {project.ProjectDir}");
        }

        RequireDirectory(ProjectPaths.PromptsDir(project), "promptsDir", errors);
        RequireDirectory(ProjectPaths.QualityDir(project), "qualityDir", errors);
        RequireWritableDirectory(ProjectPaths.StateDir(project), "stateDir", errors);
        RequireWritableDirectory(ProjectPaths.CompletedDir(project), "completedDir", errors);

        if (discovery.TaskPrompts.Count == 0)
        {
            warnings.Add("promptsDir не содержит pending task prompts с числовым префиксом.");
        }

        if (discovery.QualityPrompts.Count == 0)
        {
            warnings.Add("qualityDir не содержит quality prompts с числовым префиксом.");
        }

        if (string.IsNullOrWhiteSpace(project.OpenCodeOverrides.OpenCodeExecutable))
        {
            errors.Add("openCodeExecutable пустой.");
        }

        if (!Uri.TryCreate(project.OpenCodeOverrides.ServerUrl, UriKind.Absolute, out _))
        {
            errors.Add("serverUrl должен быть абсолютным URI.");
        }

        return new ProjectDiagnosticsResult(errors, warnings);
    }

    private static void RequireDirectory(string path, string label, List<string> errors)
    {
        if (!Directory.Exists(path))
        {
            errors.Add($"{label} не существует или недоступен: {path}");
        }
    }

    private static void RequireWritableDirectory(string path, string label, List<string> errors)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            errors.Add($"{label} недоступен для записи: {path}. {exception.Message}");
        }
    }
}
