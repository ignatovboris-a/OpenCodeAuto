using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Infrastructure;

namespace OpenCodeQueue.Cli.ConsoleUi;

internal static class ProjectStatusResetter
{
    public static async Task<int> ResetAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        await ProjectFileInitializer.EnsureProjectFilesAsync(project, cancellationToken);
        var restored = RestoreCompletedPrompts(project);
        DeleteFileIfExists(ProjectPaths.StateFile(project));
        DeleteFileIfExists(ProjectPaths.EventsFile(project));
        DeleteFileIfExists(ProjectPaths.RunLockFile(project));
        DeleteFileIfExists(Path.Combine(ProjectPaths.StateDir(project), "opencode-server.json"));
        if (Directory.Exists(ProjectPaths.RunsDir(project)))
        {
            Directory.Delete(ProjectPaths.RunsDir(project), recursive: true);
        }

        Directory.CreateDirectory(ProjectPaths.RunsDir(project));
        return restored;
    }

    private static int RestoreCompletedPrompts(ProjectProfile project)
    {
        var completedDir = ProjectPaths.CompletedDir(project);
        if (!Directory.Exists(completedDir))
        {
            return 0;
        }

        Directory.CreateDirectory(ProjectPaths.PromptsDir(project));
        var restored = 0;
        foreach (var file in Directory.EnumerateFiles(completedDir, "*.md", SearchOption.TopDirectoryOnly).ToArray())
        {
            var destination = Path.Combine(ProjectPaths.PromptsDir(project), StripArchivePrefix(Path.GetFileName(file)));
            destination = GetUniqueDestination(destination);
            File.Move(file, destination);
            restored++;
        }

        return restored;
    }

    private static string StripArchivePrefix(string fileName)
    {
        return fileName.Length > 16
            && char.IsDigit(fileName[0])
            && fileName[8] == '-'
            && fileName[15] == '_'
            ? fileName[16..]
            : fileName;
    }

    private static string GetUniqueDestination(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, $"{name}-{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
