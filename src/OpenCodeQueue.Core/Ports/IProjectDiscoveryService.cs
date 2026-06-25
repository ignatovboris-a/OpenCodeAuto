using OpenCodeQueue.Core.Discovery;

namespace OpenCodeQueue.Core.Ports;

public interface IProjectDiscoveryService
{
    Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(string configPath, CancellationToken cancellationToken);
}
