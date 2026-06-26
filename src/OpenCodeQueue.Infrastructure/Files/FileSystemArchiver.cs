using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Infrastructure.Files;

public sealed class FileSystemArchiver : IFileArchiver
{
    public async Task<ArchiveCompletedTaskResult> ArchiveCompletedTaskAsync(ProjectProfile project, PromptDescriptor taskPrompt, string expectedContentHash, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(taskPrompt.Path))
        {
            return new ArchiveCompletedTaskResult(false, ErrorMessage: $"Source task prompt отсутствует, архивирование остановлено: {taskPrompt.Path}");
        }

        var currentHash = await FileHash.ComputeSha256Async(taskPrompt.Path, cancellationToken);
        if (!string.Equals(currentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return new ArchiveCompletedTaskResult(false, ErrorMessage: "Source task prompt изменился после snapshot. Файл не перемещён; проверьте вручную.");
        }

        var completedDir = ProjectPaths.CompletedDir(project);
        Directory.CreateDirectory(completedDir);
        var destination = GetUniqueDestination(completedDir, completedAt, taskPrompt.FileName);
        File.Move(taskPrompt.Path, destination, overwrite: false);
        return new ArchiveCompletedTaskResult(true, destination);
    }

    private static string GetUniqueDestination(string completedDir, DateTimeOffset completedAt, string fileName)
    {
        var prefix = completedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var destination = Path.Combine(completedDir, $"{prefix}_{fileName}");
        for (var index = 1; File.Exists(destination); index++)
        {
            destination = Path.Combine(completedDir, $"{prefix}_{baseName}-{index}{extension}");
        }

        return destination;
    }
}
