using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Cli.ConsoleUi;

internal static class ConsoleInteraction
{
    public static bool Confirm(this IConsoleReporter reporter, string prompt)
    {
        var answer = reporter.ReadLine(prompt);
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "д", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "да", StringComparison.OrdinalIgnoreCase);
    }

    public static string AskDirectory(this IConsoleReporter reporter, string label, string defaultPath, string baseDir)
    {
        var value = reporter.ReadLine($"{label} [{defaultPath}]: ");
        var path = string.IsNullOrWhiteSpace(value) ? defaultPath : ResolvePath(value.Trim(), baseDir);
        if (!Directory.Exists(path) && reporter.Confirm($"Папка не существует: {path}. Создать? [y/N]: "))
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    public static string ResolvePath(string path, string baseDir)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));
    }

    public static void PrintDirectoryStatus(this IConsoleReporter reporter, string name, string path)
    {
        reporter.Info(Directory.Exists(path) ? $"{name}: OK ({path})" : $"{name}: отсутствует ({path})");
    }

    public static void PrintProjectForConfirmation(this IConsoleReporter reporter, ProjectProfile project)
    {
        reporter.Info("Будет сохранён проект:");
        reporter.Info($"  id: {project.Id}");
        reporter.Info($"  projectDir: {project.ProjectDir}");
        reporter.Info($"  OpenCode provider/model: {project.OpenCodeOverrides.Provider}/{project.OpenCodeOverrides.Model}");
    }
}
