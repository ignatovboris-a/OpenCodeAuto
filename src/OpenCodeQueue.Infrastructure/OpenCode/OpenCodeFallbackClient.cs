using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;

namespace OpenCodeQueue.Infrastructure.OpenCode;

public sealed class OpenCodeFallbackClient(IOpenCodeClient serverClient, IOpenCodeClient cliClient) : IOpenCodeClient
{
    public Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken) => SelectAsync(project, client => client.EnsureReadyAsync(project, cancellationToken));

    public Task<OpenCodeSession> CreateSessionAsync(ProjectProfile project, string title, CancellationToken cancellationToken) => SelectAsync(project, client => client.CreateSessionAsync(project, title, cancellationToken));

    public Task<OpenCodeSessionDetails> GetSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken) => SelectAsync(project, client => client.GetSessionAsync(project, sessionId, cancellationToken));

    public Task<OpenCodeMessageResult> SendPromptAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken) => SelectAsync(project, client => client.SendPromptAsync(project, sessionId, payload, cancellationToken));

    public Task<OpenCodeSessionStatus> GetSessionStatusAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken) => SelectAsync(project, client => client.GetSessionStatusAsync(project, sessionId, cancellationToken));

    public Task<StepRecoveryResult> TryRecoverStepAsync(ProjectProfile project, RunManifest manifest, WorkflowStep step, CancellationToken cancellationToken) => SelectAsync(project, client => client.TryRecoverStepAsync(project, manifest, step, cancellationToken));

    public Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken) => SelectAsync(project, client => client.AbortSessionAsync(project, sessionId, cancellationToken));

    private async Task SelectAsync(ProjectProfile project, Func<IOpenCodeClient, Task> action)
    {
        if (project.OpenCodeOverrides.OpenCodeMode == OpenCodeMode.Cli)
        {
            await action(cliClient);
            return;
        }

        try
        {
            await action(serverClient);
        }
        catch (OpenCodeProjectMismatchException)
        {
            throw;
        }
        catch (OpenCodeClientException)
        {
            await action(cliClient);
        }
    }

    private async Task<T> SelectAsync<T>(ProjectProfile project, Func<IOpenCodeClient, Task<T>> action)
    {
        if (project.OpenCodeOverrides.OpenCodeMode == OpenCodeMode.Cli)
        {
            return await action(cliClient);
        }

        try
        {
            return await action(serverClient);
        }
        catch (OpenCodeProjectMismatchException)
        {
            throw;
        }
        catch (OpenCodeClientException)
        {
            return await action(cliClient);
        }
    }
}
