using System.Text.Json.Serialization;

namespace OpenCodeQueue.Core.Configuration;

public sealed record ProjectProfile
{
    public required ProjectId Id { get; init; }

    public required string ProjectDir { get; init; }

    [JsonIgnore]
    public string PromptsDir { get; init; } = string.Empty;

    [JsonIgnore]
    public string? QualityDir { get; init; }

    [JsonIgnore]
    public string StateDir { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    [JsonIgnore]
    public string? ReviewsDir { get; init; }

    [JsonIgnore]
    public string CompletedDir { get; init; } = string.Empty;

    public bool StopOnQualityFailure { get; init; } = true;

    public OpenCodeSettings OpenCodeOverrides { get; init; } = new();
}
