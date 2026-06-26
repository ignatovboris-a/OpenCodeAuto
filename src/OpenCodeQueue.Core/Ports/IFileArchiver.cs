using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Core.Ports;

public interface IFileArchiver
{
    Task<ArchiveCompletedTaskResult> ArchiveCompletedTaskAsync(ProjectProfile project, PromptDescriptor taskPrompt, string expectedContentHash, DateTimeOffset completedAt, CancellationToken cancellationToken);
}

public sealed record ArchiveCompletedTaskResult(bool IsSuccess, string? ArchivedPath = null, string? ErrorMessage = null);
