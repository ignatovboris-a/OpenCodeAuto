using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Registry;

namespace OpenCodeQueue.Infrastructure.Configuration;

public sealed class JsonProjectRegistry(IAppConfigStore configStore) : IProjectRegistry
{
    public async Task<IReadOnlyList<ProjectProfile>> ListAsync(string configPath, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        return config.Projects.OrderBy(project => project.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ProjectProfile?> GetActiveAsync(string configPath, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        return config.ActiveProjectId is null
            ? null
            : config.Projects.FirstOrDefault(project => string.Equals(project.Id, config.ActiveProjectId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ProjectProfile?> GetByIdAsync(string configPath, string projectId, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        return config.Projects.FirstOrDefault(project => string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ProjectRegistryResult> AddOrUpdateAsync(string configPath, ProjectProfile project, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        var projects = config.Projects.Where(existing => !string.Equals(existing.Id, project.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        projects.Add(project);

        var activeProjectId = config.ActiveProjectId ?? project.Id;
        await configStore.SaveAsync(configPath, config with { ActiveProjectId = activeProjectId, Projects = projects }, cancellationToken);
        return ProjectRegistryResult.Success("Проект сохранён.", project);
    }

    public async Task<ProjectRegistryResult> RemoveAsync(string configPath, string projectId, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        var projects = config.Projects.Where(project => !string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (projects.Length == config.Projects.Count)
        {
            return ProjectRegistryResult.Failure("Проект с таким id не найден.");
        }

        var activeProjectId = string.Equals(config.ActiveProjectId, projectId, StringComparison.OrdinalIgnoreCase) ? null : config.ActiveProjectId;
        await configStore.SaveAsync(configPath, config with { ActiveProjectId = activeProjectId, Projects = projects }, cancellationToken);
        return ProjectRegistryResult.Success("Проект удалён.");
    }

    public async Task<ProjectRegistryResult> SelectAsync(string configPath, string projectId, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        var project = config.Projects.FirstOrDefault(existing => string.Equals(existing.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return ProjectRegistryResult.Failure("Проект с таким id не найден.");
        }

        await configStore.SaveAsync(configPath, config with { ActiveProjectId = project.Id }, cancellationToken);
        return ProjectRegistryResult.Success("Активный проект выбран.", project);
    }

    private async Task<AppConfig> LoadOrEmptyAsync(string configPath, CancellationToken cancellationToken)
    {
        return await configStore.LoadAsync(configPath, cancellationToken) ?? new AppConfig();
    }
}
