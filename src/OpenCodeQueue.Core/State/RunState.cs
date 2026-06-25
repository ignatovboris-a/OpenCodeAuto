using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Core.State;

public enum RunStatus
{
    Pending,
    Running,
    Failed,
    CompletedPendingArchive,
    Completed,
    Aborted,
    NeedsManualIntervention
}

public enum ArchiveStatus
{
    NotStarted,
    Pending,
    Completed,
    Failed
}

public enum RecoveryStrategy
{
    ConservativeContinue
}

public enum WorkflowStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Recovering,
    Skipped
}

public readonly record struct WorkflowStepId(string Value)
{
    public override string ToString() => Value;

    public static WorkflowStepId Task => new("task");

    public static WorkflowStepId Quality(int order) => new($"quality-{order:00}");
}

public sealed record WorkflowStep
{
    public required WorkflowStepId Id { get; init; }

    public required PromptKind Kind { get; init; }

    public required string SourcePath { get; init; }

    public string? SnapshotPath { get; init; }

    public string? ContentHash { get; init; }

    public int Order { get; init; }

    public WorkflowStepStatus Status { get; init; } = WorkflowStepStatus.Pending;

    public string? SessionMessageId { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int AttemptCount { get; init; }
}

public sealed record RunManifest
{
    public required string RunId { get; init; }

    public required ProjectId ProjectId { get; init; }

    public string? SessionId { get; init; }

    public RunStatus Status { get; init; } = RunStatus.Pending;

    public required string ProjectDirSnapshot { get; init; }

    public string? PromptsDirSnapshot { get; init; }

    public string? QualityDirSnapshot { get; init; }

    public OpenCodeSettings OpenCodeSettingsSnapshot { get; init; } = new();

    public PromptDescriptor? TaskDescriptor { get; init; }

    public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];

    public int CurrentStepIndex { get; init; }

    public string? LastError { get; init; }

    public ArchiveStatus ArchiveStatus { get; init; } = ArchiveStatus.NotStarted;

    public int RecoveryAttempts { get; init; }

    public RecoveryStrategy RecoveryStrategy { get; init; } = RecoveryStrategy.ConservativeContinue;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }
}

public sealed record QueueState
{
    public int SchemaVersion { get; init; } = 1;

    public required ProjectId ProjectId { get; init; }

    public required string ProjectDirSnapshot { get; init; }

    public string? ActiveRunId { get; init; }

    public string? LastCompletedRunId { get; init; }

    public string? LastCompletedTaskFile { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

}

public sealed record QueueEvent
{
    public required string Type { get; init; }

    public required ProjectId ProjectId { get; init; }

    public string? RunId { get; init; }

    public string? StepId { get; init; }

    public string? SessionId { get; init; }

    public string? TaskFile { get; init; }

    public string? Message { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public static class QueueEventTypes
{
    public const string ProjectSelected = nameof(ProjectSelected);
    public const string RunCreated = nameof(RunCreated);
    public const string SessionCreated = nameof(SessionCreated);
    public const string StepStarted = nameof(StepStarted);
    public const string StepCompleted = nameof(StepCompleted);
    public const string StepFailed = nameof(StepFailed);
    public const string RecoveryStarted = nameof(RecoveryStarted);
    public const string RecoveryCompleted = nameof(RecoveryCompleted);
    public const string TaskArchived = nameof(TaskArchived);
    public const string RunCompleted = nameof(RunCompleted);
    public const string RunAborted = nameof(RunAborted);
}
