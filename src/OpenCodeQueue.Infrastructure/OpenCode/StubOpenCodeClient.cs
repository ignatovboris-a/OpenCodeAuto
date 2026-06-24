using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Infrastructure.OpenCode;

public sealed class StubOpenCodeClient : IOpenCodeClient
{
    public Task<OpenCodeServerInfo?> GetServerInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<OpenCodeServerInfo?>(null);
    }

    public Task<OpenCodeSession> CreateSessionAsync(string projectDir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("OpenCode API будет реализован на следующих шагах.");
    }

    public Task<OpenCodeSession> RestoreSessionAsync(string projectDir, string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("Восстановление OpenCode-сессии будет реализовано на следующих шагах.");
    }

    public Task<OpenCodeMessageResult> SendPromptAsync(OpenCodeSession session, string promptText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("Отправка prompt в OpenCode будет реализована на следующих шагах.");
    }
}
