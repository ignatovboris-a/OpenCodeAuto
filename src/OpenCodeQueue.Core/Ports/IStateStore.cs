using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.State;

namespace OpenCodeQueue.Core.Ports;

public interface IStateStore
{
    Task<QueueState?> LoadQueueStateAsync(ProjectProfile project, CancellationToken cancellationToken);

    Task SaveQueueStateAsync(ProjectProfile project, QueueState state, CancellationToken cancellationToken);

    Task<RunManifest?> LoadRunManifestAsync(ProjectProfile project, string runId, CancellationToken cancellationToken);

    Task SaveRunManifestAsync(ProjectProfile project, RunManifest manifest, CancellationToken cancellationToken);

    Task AppendEventAsync(ProjectProfile project, QueueEvent queueEvent, CancellationToken cancellationToken);
}
