using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Registry;

namespace OpenCodeQueue.Core.Ports;

public interface IProjectRegistry
{
    Task<IReadOnlyList<ProjectProfile>> ListAsync(string configPath, CancellationToken cancellationToken);

    Task<ProjectProfile?> GetActiveAsync(string configPath, CancellationToken cancellationToken);

    Task<ProjectProfile?> GetByIdAsync(string configPath, string projectId, CancellationToken cancellationToken);

    Task<ProjectRegistryResult> AddOrUpdateAsync(string configPath, ProjectProfile project, CancellationToken cancellationToken);

    Task<ProjectRegistryResult> RemoveAsync(string configPath, string projectId, bool confirmedActiveRemoval, CancellationToken cancellationToken);

    Task<ProjectRegistryResult> SelectAsync(string configPath, string projectId, CancellationToken cancellationToken);
}
