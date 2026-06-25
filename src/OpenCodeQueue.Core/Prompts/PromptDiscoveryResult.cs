namespace OpenCodeQueue.Core.Prompts;

public sealed record PromptDiscoveryResult(
    IReadOnlyList<PromptDescriptor> TaskPrompts,
    IReadOnlyList<PromptDescriptor> QualityPrompts,
    IReadOnlyList<string> Warnings);
