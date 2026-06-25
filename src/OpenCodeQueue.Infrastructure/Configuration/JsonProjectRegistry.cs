using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Registry;

namespace OpenCodeQueue.Infrastructure.Configuration;

public sealed class JsonProjectRegistry(IAppConfigStore configStore) : IProjectRegistry
{
    public async Task<IReadOnlyList<ProjectProfile>> ListAsync(string configPath, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        return config.Projects.OrderBy(project => project.Id.Value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ProjectProfile?> GetActiveAsync(string configPath, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        return config.ActiveProjectId is null
            ? null
            : config.Projects.FirstOrDefault(project => string.Equals(project.Id.Value, config.ActiveProjectId.Value.Value, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ProjectProfile?> GetByIdAsync(string configPath, string projectId, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        return config.Projects.FirstOrDefault(project => string.Equals(project.Id.Value, projectId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ProjectRegistryResult> AddOrUpdateAsync(string configPath, ProjectProfile project, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        var projects = config.Projects.Where(existing => !string.Equals(existing.Id.Value, project.Id.Value, StringComparison.OrdinalIgnoreCase)).ToList();
        projects.Add(project);

        var activeProjectId = config.ActiveProjectId ?? project.Id;
        var updatedConfig = config with { ActiveProjectId = activeProjectId, Projects = projects };
        var errors = AppConfigValidator.Validate(updatedConfig);
        if (errors.Count > 0)
        {
            return ProjectRegistryResult.Failure("Проект не сохранён: " + string.Join(" ", errors));
        }

        await configStore.SaveAsync(configPath, updatedConfig, cancellationToken);
        return ProjectRegistryResult.Success("Проект сохранён.", project);
    }

    public async Task<ProjectRegistryResult> RemoveAsync(string configPath, string projectId, bool confirmedActiveRemoval, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        var removesActiveProject = config.ActiveProjectId is not null && string.Equals(config.ActiveProjectId.Value.Value, projectId, StringComparison.OrdinalIgnoreCase);
        if (removesActiveProject && !confirmedActiveRemoval)
        {
            return ProjectRegistryResult.Failure("Нельзя удалить активный проект без подтверждения.");
        }

        var projects = config.Projects.Where(project => !string.Equals(project.Id.Value, projectId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (projects.Length == config.Projects.Count)
        {
            return ProjectRegistryResult.Failure("Проект с таким id не найден.");
        }

        var activeProjectId = removesActiveProject ? null : config.ActiveProjectId;
        await configStore.SaveAsync(configPath, config with { ActiveProjectId = activeProjectId, Projects = projects }, cancellationToken);
        return ProjectRegistryResult.Success("Проект удалён.");
    }

    public async Task<ProjectRegistryResult> SelectAsync(string configPath, string projectId, CancellationToken cancellationToken)
    {
        var config = await LoadOrEmptyAsync(configPath, cancellationToken);
        if (!new ProjectId(projectId).IsValid)
        {
            return ProjectRegistryResult.Failure("Id проекта должен быть стабильным slug без пробелов.");
        }

        var project = config.Projects.FirstOrDefault(existing => string.Equals(existing.Id.Value, projectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            return ProjectRegistryResult.Failure("Проект с таким id не найден.");
        }

        if (!Directory.Exists(project.ProjectDir))
        {
            return ProjectRegistryResult.Failure($"Нельзя выбрать проект: projectDir не существует: {project.ProjectDir}");
        }

        await configStore.SaveAsync(configPath, config with { ActiveProjectId = project.Id }, cancellationToken);
        return ProjectRegistryResult.Success("Активный проект выбран.", project);
    }

    private async Task<AppConfig> LoadOrEmptyAsync(string configPath, CancellationToken cancellationToken)
    {
        return await configStore.LoadOrCreateDefaultAsync(configPath, cancellationToken);
    }
}
