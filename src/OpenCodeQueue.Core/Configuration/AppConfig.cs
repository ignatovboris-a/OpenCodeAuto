namespace OpenCodeQueue.Core.Configuration;

public sealed record AppConfig
{
    public string? ActiveProjectId { get; init; }

    public IReadOnlyList<ProjectProfile> Projects { get; init; } = [];
}
