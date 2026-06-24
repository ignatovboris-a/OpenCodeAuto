namespace OpenCodeQueue.Core.Prompts;

public enum PromptKind
{
    Task,
    Quality
}

public sealed record PromptFile(
    string Path,
    string FileName,
    string NumberPrefix,
    PromptKind Kind);
