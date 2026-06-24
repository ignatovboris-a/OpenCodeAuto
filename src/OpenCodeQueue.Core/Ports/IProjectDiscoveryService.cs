using OpenCodeQueue.Core.Discovery;

namespace OpenCodeQueue.Core.Ports;

public interface IProjectDiscoveryService
{
    Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(CancellationToken cancellationToken);
}
