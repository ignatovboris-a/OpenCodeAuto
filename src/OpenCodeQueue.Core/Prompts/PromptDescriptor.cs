namespace OpenCodeQueue.Core.Prompts;

public sealed record PromptDescriptor(
    string Path,
    string FileName,
    NumericPrefix Prefix,
    string ContentHash,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    PromptKind Kind);
