using OpenCodeQueue.Core.Configuration;

namespace OpenCodeQueue.Core.Ports;

public interface IAppConfigStore
{
    Task<AppConfig?> LoadAsync(string configPath, CancellationToken cancellationToken);

    Task SaveAsync(string configPath, AppConfig config, CancellationToken cancellationToken);
}
