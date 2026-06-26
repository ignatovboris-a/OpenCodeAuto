namespace OpenCodeQueue.Core.Configuration;

public sealed record AppConfig
{
    public int SchemaVersion { get; init; } = 1;

    public ProjectId? ActiveProjectId { get; init; }

    public OpenCodeSettings Defaults { get; init; } = new();

    public IReadOnlyList<ProjectProfile> Projects { get; init; } = [];
}
