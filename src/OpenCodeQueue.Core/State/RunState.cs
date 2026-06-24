namespace OpenCodeQueue.Core.State;

public enum QueueRunStatus
{
    NotStarted,
    Running,
    WaitingForRecovery,
    Completed,
    Failed,
    Cancelled
}

public enum WorkflowStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}

public sealed record RunManifest
{
    public required string RunId { get; init; }

    public required string ProjectId { get; init; }

    public string? SessionId { get; init; }

    public QueueRunStatus Status { get; init; } = QueueRunStatus.NotStarted;

    public string? CurrentTaskPath { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
