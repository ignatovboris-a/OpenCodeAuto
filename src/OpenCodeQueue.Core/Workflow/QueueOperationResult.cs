using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Prompts;
using OpenCodeQueue.Core.State;

namespace OpenCodeQueue.Core.Workflow;

public sealed record QueueOperationResult
{
    public bool IsSuccess { get; init; } = true;

    public int ExitCode { get; init; }

    public IReadOnlyList<string> Messages { get; init; } = [];

    public ProjectProfile? Project { get; init; }

    public QueueState? State { get; init; }

    public RunManifest? Manifest { get; init; }

    public PromptDiscoveryResult? Discovery { get; init; }

    public static QueueOperationResult Success(params string[] messages) => new() { Messages = messages };

    public static QueueOperationResult Failure(int exitCode, params string[] messages) => new()
    {
        IsSuccess = false,
        ExitCode = exitCode,
        Messages = messages
    };
}
