namespace OpenCodeQueue.Core.Discovery;

public enum DiscoveryConfidence
{
    Low,
    Medium,
    High
}

public sealed record DiscoveredProject(
    string Source,
    string DisplayName,
    string? ProjectDir,
    string? SuggestedId,
    DiscoveryConfidence Confidence,
    IReadOnlyList<string> Warnings,
    bool CanSelectDirectly);
