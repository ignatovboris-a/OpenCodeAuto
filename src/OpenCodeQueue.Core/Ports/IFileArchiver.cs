using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Core.Ports;

public interface IFileArchiver
{
    Task ArchiveCompletedTaskAsync(ProjectProfile project, PromptFile taskPrompt, CancellationToken cancellationToken);
}
