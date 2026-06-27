using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.State;

namespace OpenCodeQueue.Core.Ports;

public interface IOpenCodeClient
{
    Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken);

    Task<OpenCodeSession> StartSessionAsync(ProjectProfile project, string title, CancellationToken cancellationToken);

    Task<OpenCodeSessionDetails> GetSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken);

    Task<OpenCodeMessageResult> SendPromptAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken);

    Task<OpenCodeMessageResult> WaitForPromptAsync(ProjectProfile project, string sessionId, string messageId, CancellationToken cancellationToken);

    Task<OpenCodeSessionStatus> GetSessionStatusAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken);

    Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken);
}
