using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.State;

namespace OpenCodeQueue.Core.Ports;

public interface IOpenCodeClient
{
    Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken);

    Task<OpenCodeSession> CreateSessionAsync(ProjectProfile project, string title, CancellationToken cancellationToken);

    Task<OpenCodeSessionDetails> GetSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken);

    Task<OpenCodeMessageResult> SendPromptAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken);

    Task<OpenCodeSessionStatus> GetSessionStatusAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken);

    Task<StepRecoveryResult> TryRecoverStepAsync(ProjectProfile project, RunManifest manifest, WorkflowStep step, CancellationToken cancellationToken);

    Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken);
}
