namespace OpenCodeQueue.Core.Configuration;

public sealed record ProjectProfile
{
    public required string Id { get; init; }

    public required string ProjectDir { get; init; }

    public string PromptsDir { get; init; } = "prompts";

    public string QualityDir { get; init; } = "quality";

    public string StateDir { get; init; } = ".queue";

    public string? DisplayName { get; init; }
}
