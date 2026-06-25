namespace OpenCodeQueue.Core.Configuration;

public sealed record AppConfig
{
    public int SchemaVersion { get; init; } = 1;

    public ProjectId? ActiveProjectId { get; init; }

    public OpenCodeSettings Defaults { get; init; } = new();

    public IReadOnlyList<ProjectProfile> Projects { get; init; } = [];

    public ProjectProfile GetActiveProjectOrThrow()
    {
        if (ActiveProjectId is null || string.IsNullOrWhiteSpace(ActiveProjectId.Value.Value))
        {
            throw new InvalidOperationException("Активный проект не выбран.");
        }

        return Projects.FirstOrDefault(project => string.Equals(project.Id.Value, ActiveProjectId.Value.Value, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Активный проект '{ActiveProjectId}' не найден в registry.");
    }
}
