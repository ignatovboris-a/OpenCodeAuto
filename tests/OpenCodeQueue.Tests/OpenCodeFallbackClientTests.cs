using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Infrastructure.OpenCode;

namespace OpenCodeQueue.Tests;

public sealed class OpenCodeFallbackClientTests
{
    [Fact]
    public async Task ServerFailure_PinsProjectToCliForFollowingOperations()
    {
        var project = Project();
        var server = new FakeOpenCodeClient { EnsureReadyException = new OpenCodeClientException("server down") };
        var cli = new FakeOpenCodeClient { Session = new OpenCodeSession("cli-session", project.ProjectDir) };
        var client = new OpenCodeFallbackClient(server, cli);

        await client.EnsureReadyAsync(project, CancellationToken.None);
        var session = await client.StartSessionAsync(project, "run-1", CancellationToken.None);

        Assert.Equal("cli-session", session.SessionId);
        Assert.Equal(1, server.EnsureReadyCalls);
        Assert.Equal(0, server.StartSessionCalls);
        Assert.Equal(1, cli.EnsureReadyCalls);
        Assert.Equal(1, cli.StartSessionCalls);
    }

    [Fact]
    public async Task ProjectMismatch_DoesNotFallbackToCli()
    {
        var project = Project();
        var server = new FakeOpenCodeClient { EnsureReadyException = new OpenCodeProjectMismatchException(project.ProjectDir, Path.GetTempPath()) };
        var cli = new FakeOpenCodeClient();
        var client = new OpenCodeFallbackClient(server, cli);

        await Assert.ThrowsAsync<OpenCodeProjectMismatchException>(() => client.EnsureReadyAsync(project, CancellationToken.None));

        Assert.Equal(1, server.EnsureReadyCalls);
        Assert.Equal(0, cli.EnsureReadyCalls);
    }

    [Fact]
    public async Task SendPromptAsync_ServerFailure_DoesNotFallbackAndResendPromptThroughCli()
    {
        var project = Project();
        var server = new FakeOpenCodeClient { SendPromptException = new OpenCodeClientException("send failed") };
        var cli = new FakeOpenCodeClient();
        var client = new OpenCodeFallbackClient(server, cli);

        await Assert.ThrowsAsync<OpenCodeClientException>(() => client.SendPromptAsync(project, "server-session", new PromptPayload
        {
            Content = "prompt",
            SourcePath = Path.Combine(project.ProjectDir, "prompts", "01.md"),
            MessageId = "msg-1"
        }, CancellationToken.None));

        Assert.Equal(1, server.SendPromptCalls);
        Assert.Equal(0, cli.SendPromptCalls);
    }

    private static ProjectProfile Project()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new ProjectProfile { Id = "project-a", ProjectDir = root };
    }

    private sealed class FakeOpenCodeClient : IOpenCodeClient
    {
        public Exception? EnsureReadyException { get; init; }

        public Exception? SendPromptException { get; init; }

        public OpenCodeSession Session { get; init; } = new("session", "project");

        public int EnsureReadyCalls { get; private set; }

        public int StartSessionCalls { get; private set; }

        public int SendPromptCalls { get; private set; }

        public Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken)
        {
            EnsureReadyCalls++;
            if (EnsureReadyException is not null)
            {
                throw EnsureReadyException;
            }

            return Task.CompletedTask;
        }

        public Task<OpenCodeSession> StartSessionAsync(ProjectProfile project, string title, CancellationToken cancellationToken)
        {
            StartSessionCalls++;
            return Task.FromResult(Session);
        }

        public Task<OpenCodeSessionDetails> GetSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeSessionDetails(Session, new OpenCodeSessionStatus(OpenCodeSessionState.Unknown), []));
        }

        public Task<OpenCodeMessageResult> SendPromptAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken)
        {
            SendPromptCalls++;
            if (SendPromptException is not null)
            {
                throw SendPromptException;
            }

            return Task.FromResult(new OpenCodeMessageResult(true, payload.MessageId, true));
        }

        public Task<OpenCodeSessionStatus> GetSessionStatusAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeSessionStatus(OpenCodeSessionState.Unknown));
        }

        public Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
