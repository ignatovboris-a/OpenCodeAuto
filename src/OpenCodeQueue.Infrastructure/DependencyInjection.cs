using Microsoft.Extensions.DependencyInjection;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Infrastructure.Configuration;
using OpenCodeQueue.Infrastructure.Discovery;
using OpenCodeQueue.Infrastructure.Files;
using OpenCodeQueue.Infrastructure.OpenCode;
using OpenCodeQueue.Infrastructure.Prompts;
using OpenCodeQueue.Infrastructure.State;

namespace OpenCodeQueue.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOpenCodeQueueInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IAppConfigStore, JsonAppConfigStore>();
        services.AddSingleton<IProjectRegistry, JsonProjectRegistry>();
        services.AddSingleton<IProjectDiscoveryService, CompositeProjectDiscoveryService>();
        services.AddSingleton<IOpenCodeClient, StubOpenCodeClient>();
        services.AddSingleton<IPromptRepository, FileSystemPromptRepository>();
        services.AddSingleton<IStateStore, JsonStateStore>();
        services.AddSingleton<IRunLock, FileRunLock>();
        services.AddSingleton<IFileArchiver, FileSystemArchiver>();
        services.AddSingleton<IClock, SystemClock>();
        return services;
    }
}
