using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.State;

namespace OpenCodeQueue.Core.OpenCode;

public sealed record OpenCodeSession(string SessionId, string ProjectDir, string? Title = null);

public enum OpenCodeSessionState
{
    Unknown,
    Idle,
    Busy,
    Retry,
    Failed,
    Aborted
}

public sealed record OpenCodeSessionStatus(OpenCodeSessionState State, string? Message = null);

public enum OpenCodeStepOutcomeKind
{
    Completed,
    Busy,
    BuiltInRetryInProgress,
    RecoverableToolAbort,
    RecoverableTransportError,
    RecoverableInterruption,
    PermissionRequest,
    QuestionRequest,
    NeedsManualIntervention,
    FatalFailure,
    NonRecoverableError,
    Unknown
}

public sealed record OpenCodeMessageResult(
    bool IsSuccess,
    string? MessageId = null,
    bool HasAssistantResponse = false,
    string? ErrorMessage = null,
    int? ExitCode = null,
    string? Stdout = null,
    string? Stderr = null,
    string? LastAssistantText = null,
    bool IsTransportError = false,
    bool IsTimeout = false,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    string? StdoutLogPath = null,
    string? StderrLogPath = null,
    string? MessageLogPath = null);

public sealed record StepClassification(OpenCodeStepOutcomeKind Kind, string? Signature = null, string? Message = null);

public sealed record OpenCodeMessage(string Id, string Role, bool IsCompleted, bool IsFailed, string? ParentId = null, string? ErrorMessage = null, string? Text = null);

public sealed record OpenCodeSessionDetails(OpenCodeSession Session, OpenCodeSessionStatus Status, IReadOnlyList<OpenCodeMessage> Messages);

public sealed record PromptPayload
{
    public required string Content { get; init; }

    public required string SourcePath { get; init; }

    public required string MessageId { get; init; }

    public PromptTransport Transport { get; init; } = PromptTransport.Auto;

    public int MaxInlinePromptChars { get; init; } = 24_000;

    public string? RunId { get; init; }

    public string? StepId { get; init; }
}

public class OpenCodeClientException : Exception
{
    public OpenCodeClientException(string message) : base(message)
    {
    }

    public OpenCodeClientException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class OpenCodeProjectMismatchException : OpenCodeClientException
{
    public OpenCodeProjectMismatchException(string selectedProjectDir, string serverProjectDir)
        : base($"OpenCode server смотрит на другой проект. В registry выбран projectDir: {selectedProjectDir}. Server видит project/path: {serverProjectDir}. Workflow не запущен автоматически.")
    {
        SelectedProjectDir = selectedProjectDir;
        ServerProjectDir = serverProjectDir;
    }

    public string SelectedProjectDir { get; }

    public string ServerProjectDir { get; }
}
