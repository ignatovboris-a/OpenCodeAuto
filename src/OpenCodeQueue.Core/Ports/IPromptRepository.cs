using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Core.Ports;

public interface IPromptRepository
{
    Task<IReadOnlyList<PromptFile>> GetTaskPromptsAsync(ProjectProfile project, CancellationToken cancellationToken);

    Task<IReadOnlyList<PromptFile>> GetQualityPromptsAsync(ProjectProfile project, CancellationToken cancellationToken);

    Task<string> ReadPromptTextAsync(PromptFile prompt, CancellationToken cancellationToken);
}
