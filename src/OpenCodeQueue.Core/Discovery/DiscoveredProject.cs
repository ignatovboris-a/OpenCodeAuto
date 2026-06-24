namespace OpenCodeQueue.Core.Discovery;

public sealed record DiscoveredProject(
    string Source,
    string ProjectDir,
    string? SuggestedId,
    string? DisplayName);
