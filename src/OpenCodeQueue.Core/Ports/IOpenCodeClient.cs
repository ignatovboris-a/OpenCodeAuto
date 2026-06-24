using OpenCodeQueue.Core.OpenCode;

namespace OpenCodeQueue.Core.Ports;

public interface IOpenCodeClient
{
    Task<OpenCodeServerInfo?> GetServerInfoAsync(CancellationToken cancellationToken);

    Task<OpenCodeSession> CreateSessionAsync(string projectDir, CancellationToken cancellationToken);

    Task<OpenCodeSession> RestoreSessionAsync(string projectDir, string sessionId, CancellationToken cancellationToken);

    Task<OpenCodeMessageResult> SendPromptAsync(OpenCodeSession session, string promptText, CancellationToken cancellationToken);
}
