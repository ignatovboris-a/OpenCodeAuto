namespace OpenCodeQueue.Core.Workflow;

public interface IQueueUseCases
{
    Task<QueueOperationResult> RunQueueAsync(string configPath, string? projectId, bool once, CancellationToken cancellationToken);

    Task<QueueOperationResult> ResumeAsync(string configPath, string? projectId, CancellationToken cancellationToken);

    Task<QueueOperationResult> GetStatusAsync(string configPath, string? projectId, CancellationToken cancellationToken);

    Task<QueueOperationResult> ListPromptsAsync(string configPath, string? projectId, CancellationToken cancellationToken);

    Task<QueueOperationResult> AbortRunAsync(string configPath, string? projectId, CancellationToken cancellationToken);
}
