using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.State;

namespace OpenCodeQueue.Core.Ports;

public interface IStateStore
{
    Task<RunManifest?> LoadActiveRunAsync(ProjectProfile project, CancellationToken cancellationToken);

    Task SaveActiveRunAsync(ProjectProfile project, RunManifest manifest, CancellationToken cancellationToken);

    Task AppendEventAsync(ProjectProfile project, string eventJson, CancellationToken cancellationToken);
}
