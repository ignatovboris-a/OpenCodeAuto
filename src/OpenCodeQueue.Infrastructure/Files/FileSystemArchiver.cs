using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Infrastructure.Files;

public sealed class FileSystemArchiver : IFileArchiver
{
    public Task ArchiveCompletedTaskAsync(ProjectProfile project, PromptDescriptor taskPrompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completedDir = ProjectPaths.CompletedDir(project);
        Directory.CreateDirectory(completedDir);
        File.Move(taskPrompt.Path, Path.Combine(completedDir, taskPrompt.FileName), overwrite: false);
        return Task.CompletedTask;
    }
}
