using OpenCodeQueue.Core.Configuration;

namespace OpenCodeQueue.Core.Ports;

public interface IRunLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(ProjectProfile project, CancellationToken cancellationToken);
}
