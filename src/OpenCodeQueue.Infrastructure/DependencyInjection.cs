using Microsoft.Extensions.DependencyInjection;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Workflow;
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
        services.AddSingleton<IOpenCodeServerProcessFactory, DefaultOpenCodeServerProcessFactory>();
        services.AddSingleton<IProcessRunner, DefaultProcessRunner>();
        services.AddSingleton<OpenCodeServerClient>(provider => new OpenCodeServerClient(
            new HttpClient { Timeout = TimeSpan.FromMinutes(20) },
            provider.GetRequiredService<IOpenCodeServerProcessFactory>()));
        services.AddSingleton<OpenCodeCliClient>();
        services.AddSingleton<IOpenCodeClient>(provider => new OpenCodeFallbackClient(
            provider.GetRequiredService<OpenCodeServerClient>(),
            provider.GetRequiredService<OpenCodeCliClient>()));
        services.AddSingleton<IPromptRepository, FileSystemPromptRepository>();
        services.AddSingleton<IStateStore, JsonStateStore>();
        services.AddSingleton<IRunLock, FileRunLock>();
        services.AddSingleton<IRunWorkspace, RunWorkspace>();
        services.AddSingleton<IFileArchiver, FileSystemArchiver>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IOpenCodeRunClassifier, OpenCodeStepResultClassifier>();
        services.AddSingleton<IQueueUseCases, QueueUseCases>();
        return services;
    }
}
