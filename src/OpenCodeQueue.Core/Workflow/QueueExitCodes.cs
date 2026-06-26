namespace OpenCodeQueue.Core.Workflow;

public static class QueueExitCodes
{
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int ValidationError = 2;
    public const int ActiveRunBlocksNewRun = 3;
    public const int OpenCodeUnavailableOrProjectMismatch = 4;
    public const int WorkflowStepFailed = 5;
    public const int Cancelled = 130;
}
