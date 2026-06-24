using OpenCodeQueue.Core.Discovery;
using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Infrastructure.Discovery;

public sealed class CompositeProjectDiscoveryService : IProjectDiscoveryService
{
    public Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DiscoveredProject> result = [];
        return Task.FromResult(result);
    }
}
