using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Core.Ports;

public interface IPromptRepository
{
    Task<PromptDiscoveryResult> DiscoverAsync(ProjectProfile project, CancellationToken cancellationToken);

    Task<string> ReadPromptTextAsync(PromptDescriptor prompt, CancellationToken cancellationToken);
}
