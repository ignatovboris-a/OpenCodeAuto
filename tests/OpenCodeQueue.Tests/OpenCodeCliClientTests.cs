using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Infrastructure.OpenCode;

namespace OpenCodeQueue.Tests;

public sealed class OpenCodeCliClientTests
{
    [Fact]
    public async Task SendPromptAsync_Inline_UsesWorkingDirectoryDirAndSessionWithoutContinue()
    {
        var root = NewProjectDir("Проект с пробелами");
        var runner = new FakeProcessRunner(new ProcessRunResult(0, "{\"messageId\":\"msg-1\"}", string.Empty));
        var client = new OpenCodeCliClient(runner);
        var content = "# Prompt\nТекст с Unicode";

        var result = await client.SendPromptAsync(Project(root), "ses-1", new PromptPayload
        {
            Content = content,
            SourcePath = Path.Combine(root, "prompts", "01.md"),
            MessageId = "msg-1",
            Transport = PromptTransport.Inline,
            RunId = "run-1",
            StepId = "task"
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(root, request.WorkingDirectory);
        Assert.Equal("opencode-test", request.Executable);
        Assert.Equal(new[] { "run", "--dir", root, "--session", "ses-1", "--format", "json", content }, request.Arguments);
        Assert.DoesNotContain("--continue", request.Arguments);
        Assert.True(File.Exists(Path.Combine(root, ".queue", "runs", "run-1", "logs", "task.stdout.log")));
        Assert.True(File.Exists(Path.Combine(root, ".queue", "runs", "run-1", "logs", "task.stderr.log")));
    }

    [Fact]
    public async Task SendPromptAsync_FileAttachment_UsesFileArgumentAndWrapperTextOnly()
    {
        var root = NewProjectDir("space dir");
        var source = Path.Combine(root, "prompts", "01 big.md");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, new string('x', 100), CancellationToken.None);
        var runner = new FakeProcessRunner(new ProcessRunResult(0, "{}", string.Empty));
        var client = new OpenCodeCliClient(runner);

        await client.SendPromptAsync(Project(root), "ses-2", new PromptPayload
        {
            Content = "large prompt text must not be placed inline",
            SourcePath = source,
            MessageId = "msg-2",
            Transport = PromptTransport.Auto,
            MaxInlinePromptChars = 5
        }, CancellationToken.None);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(new[]
        {
            "run", "--dir", root, "--session", "ses-2", "--format", "json", "--file", source, "Выполни инструкции из прикреплённого Markdown-файла."
        }, request.Arguments);
        Assert.DoesNotContain("large prompt text must not be placed inline", request.Arguments);
    }

    [Fact]
    public async Task SendPromptAsync_MissingSessionId_FailsWithoutContinue()
    {
        var root = NewProjectDir();
        var runner = new FakeProcessRunner(new ProcessRunResult(0, "{}", string.Empty));
        var client = new OpenCodeCliClient(runner);

        var exception = await Assert.ThrowsAsync<OpenCodeClientException>(() => client.SendPromptAsync(Project(root), string.Empty, new PromptPayload
        {
            Content = "prompt",
            SourcePath = Path.Combine(root, "prompts", "01.md"),
            MessageId = "msg-1"
        }, CancellationToken.None));

        Assert.Contains("без известного sessionId", exception.Message);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task CreateSessionAsync_UsesTitleDirJsonAndReadsSessionId()
    {
        var root = NewProjectDir();
        var runner = new FakeProcessRunner(new ProcessRunResult(0, "{\"sessionId\":\"ses-new\"}", string.Empty));
        var client = new OpenCodeCliClient(runner);

        var session = await client.CreateSessionAsync(Project(root), "run-1 project-a 01.md", CancellationToken.None);

        Assert.Equal("ses-new", session.SessionId);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(root, request.WorkingDirectory);
        Assert.Equal("run", request.Arguments[0]);
        Assert.Equal("--dir", request.Arguments[1]);
        Assert.Equal(root, request.Arguments[2]);
        Assert.Contains("--title", request.Arguments);
        Assert.Contains("--format", request.Arguments);
        Assert.DoesNotContain("--continue", request.Arguments);
    }

    [Fact]
    public async Task CreateSessionAsync_WhenJsonHasNoSessionId_FindsUniqueSessionByTitle()
    {
        var root = NewProjectDir();
        var runner = new FakeProcessRunner(
            new ProcessRunResult(0, "{}", string.Empty),
            new ProcessRunResult(0, "[{\"id\":\"ses-found\",\"title\":\"run-2 project-a 02.md\"}]", string.Empty));
        var client = new OpenCodeCliClient(runner);

        var session = await client.CreateSessionAsync(Project(root), "run-2 project-a 02.md", CancellationToken.None);

        Assert.Equal("ses-found", session.SessionId);
        Assert.Equal(new[] { "session", "list", "--format", "json" }, runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task CreateSessionAsync_WhenSessionIdCannotBeFound_StopsSafely()
    {
        var root = NewProjectDir();
        var runner = new FakeProcessRunner(
            new ProcessRunResult(0, "{}", string.Empty),
            new ProcessRunResult(0, "[]", string.Empty));
        var client = new OpenCodeCliClient(runner);

        var exception = await Assert.ThrowsAsync<OpenCodeClientException>(() => client.CreateSessionAsync(Project(root), "run-3", CancellationToken.None));

        Assert.Contains("Workflow остановлен безопасно", exception.Message);
    }

    private static ProjectProfile Project(string root)
    {
        return new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = root,
            OpenCodeOverrides = new OpenCodeSettings
            {
                OpenCodeMode = OpenCodeMode.Cli,
                OpenCodeExecutable = "opencode-test"
            }
        };
    }

    private static string NewProjectDir(string? name = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", name ?? Guid.NewGuid().ToString("N"));
        if (Directory.Exists(path))
        {
            path = Path.Combine(path, Guid.NewGuid().ToString("N"));
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeProcessRunner(params ProcessRunResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessRunResult> results = new(results);

        public List<ProcessRunRequest> Requests { get; } = [];

        public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var result = results.Count == 0 ? new ProcessRunResult(0, "{}", string.Empty) : results.Dequeue();
            if (request.OnStdoutLine is not null)
            {
                await request.OnStdoutLine("stdout", cancellationToken);
            }

            if (request.OnStderrLine is not null)
            {
                await request.OnStderrLine("stderr", cancellationToken);
            }

            return result;
        }
    }
}
