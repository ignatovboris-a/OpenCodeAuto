using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;

namespace OpenCodeQueue.Infrastructure.OpenCode;

public sealed class OpenCodeFallbackClient(IOpenCodeClient serverClient, IOpenCodeClient cliClient) : IOpenCodeClient
{
    private readonly List<string> cliFallbackProjectDirs = [];
    private readonly object gate = new();

    public Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken) => SelectWithFallbackAsync(project, client => client.EnsureReadyAsync(project, cancellationToken));

    public Task<OpenCodeSession> StartSessionAsync(ProjectProfile project, string title, CancellationToken cancellationToken) => SelectWithFallbackAsync(project, client => client.StartSessionAsync(project, title, cancellationToken));

    public Task<OpenCodeSessionDetails> GetSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken) => SelectPinnedAsync(project, client => client.GetSessionAsync(project, sessionId, cancellationToken));

    public Task<OpenCodeMessageResult> SendPromptAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken) => SelectPinnedAsync(project, client => client.SendPromptAsync(project, sessionId, payload, cancellationToken));

    public Task<OpenCodeSessionStatus> GetSessionStatusAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken) => SelectPinnedAsync(project, client => client.GetSessionStatusAsync(project, sessionId, cancellationToken));

    public Task<StepRecoveryResult> TryRecoverStepAsync(ProjectProfile project, RunManifest manifest, WorkflowStep step, CancellationToken cancellationToken) => SelectPinnedAsync(project, client => client.TryRecoverStepAsync(project, manifest, step, cancellationToken));

    public Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken) => SelectPinnedAsync(project, client => client.AbortSessionAsync(project, sessionId, cancellationToken));

    private Task SelectPinnedAsync(ProjectProfile project, Func<IOpenCodeClient, Task> action)
    {
        return action(ShouldUseCli(project) ? cliClient : serverClient);
    }

    private Task<T> SelectPinnedAsync<T>(ProjectProfile project, Func<IOpenCodeClient, Task<T>> action)
    {
        return action(ShouldUseCli(project) ? cliClient : serverClient);
    }

    private async Task SelectWithFallbackAsync(ProjectProfile project, Func<IOpenCodeClient, Task> action)
    {
        if (ShouldUseCli(project))
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
            RememberCliFallback(project);
            await action(cliClient);
        }
    }

    private async Task<T> SelectWithFallbackAsync<T>(ProjectProfile project, Func<IOpenCodeClient, Task<T>> action)
    {
        if (ShouldUseCli(project))
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
            RememberCliFallback(project);
            return await action(cliClient);
        }
    }

    private bool ShouldUseCli(ProjectProfile project)
    {
        if (project.OpenCodeOverrides.OpenCodeMode == OpenCodeMode.Cli)
        {
            return true;
        }

        lock (gate)
        {
            return cliFallbackProjectDirs.Any(projectDir => PathResolver.AreSamePath(projectDir, project.ProjectDir));
        }
    }

    private void RememberCliFallback(ProjectProfile project)
    {
        lock (gate)
        {
            if (!cliFallbackProjectDirs.Any(projectDir => PathResolver.AreSamePath(projectDir, project.ProjectDir)))
            {
                cliFallbackProjectDirs.Add(project.ProjectDir);
            }
        }
    }
}
