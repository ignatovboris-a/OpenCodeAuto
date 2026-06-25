using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Core.Ports;

public interface IFileArchiver
{
    Task ArchiveCompletedTaskAsync(ProjectProfile project, PromptDescriptor taskPrompt, CancellationToken cancellationToken);
}
