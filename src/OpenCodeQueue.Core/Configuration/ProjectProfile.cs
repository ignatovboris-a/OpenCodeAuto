namespace OpenCodeQueue.Core.Configuration;

public sealed record ProjectProfile
{
    public required ProjectId Id { get; init; }

    public required string ProjectDir { get; init; }

    public string PromptsDir { get; init; } = "prompts";

    public string? QualityDir { get; init; }

    public string StateDir { get; init; } = ".queue";

    public string? DisplayName { get; init; }

    public string? ReviewsDir { get; init; }

    public string CompletedDir { get; init; } = string.Empty;

    public bool StopOnQualityFailure { get; init; } = true;

    public OpenCodeSettings OpenCodeOverrides { get; init; } = new();
}
