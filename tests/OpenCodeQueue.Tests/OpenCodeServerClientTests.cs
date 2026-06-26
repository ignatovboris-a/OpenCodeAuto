using System.Net;
using System.Text.Json;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Prompts;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Infrastructure.OpenCode;

namespace OpenCodeQueue.Tests;

public sealed class OpenCodeServerClientTests
{
    [Fact]
    public async Task EnsureReadyAsync_ManagedServer_StartsInSelectedProjectDir()
    {
        var root = NewProjectDir();
        var project = new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = root,
            OpenCodeOverrides = new OpenCodeSettings { ManageOpenCodeServer = true, ServerUrl = "http://127.0.0.1:50123" }
        };
        var processFactory = new FakeProcessFactory();
        using var httpClient = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/global/health" => Json(new { healthy = true, version = "test" }),
            "/path" => Json(new { worktree = root, directory = root }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new OpenCodeServerClient(httpClient, processFactory);

        await client.EnsureReadyAsync(project, CancellationToken.None);

        Assert.Equal(root, processFactory.ProjectDir);
        Assert.Equal(50123, processFactory.Port);
        Assert.True(File.Exists(Path.Combine(root, ".queue", "opencode-server.json")));
    }

    [Fact]
    public async Task EnsureReadyAsync_ExternalServerMismatch_ThrowsRussianProjectMismatch()
    {
        var selected = NewProjectDir();
        var server = NewProjectDir();
        var project = new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = selected,
            OpenCodeOverrides = new OpenCodeSettings { ManageOpenCodeServer = false, ServerUrl = "http://127.0.0.1:50124" }
        };
        using var httpClient = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/global/health" => Json(new { healthy = true, version = "test" }),
            "/path" => Json(new { worktree = server, directory = server }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new OpenCodeServerClient(httpClient, new FakeProcessFactory());

        var exception = await Assert.ThrowsAsync<OpenCodeProjectMismatchException>(() => client.EnsureReadyAsync(project, CancellationToken.None));

        Assert.Contains("В registry выбран projectDir", exception.Message);
        Assert.Contains(selected, exception.Message);
        Assert.Contains(server, exception.Message);
    }

    [Fact]
    public async Task EnsureReadyAsync_ManagedServerRestartsForDifferentProjectDir()
    {
        var first = NewProjectDir();
        var second = NewProjectDir();
        var currentServerRoot = first;
        var processFactory = new FakeProcessFactory();
        using var httpClient = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/global/health" => Json(new { healthy = true, version = "test" }),
            "/path" => Json(new { worktree = currentServerRoot, directory = currentServerRoot }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new OpenCodeServerClient(httpClient, processFactory);

        await client.EnsureReadyAsync(ManagedProject(first), CancellationToken.None);
        currentServerRoot = second;
        await client.EnsureReadyAsync(ManagedProject(second), CancellationToken.None);

        Assert.Equal(2, processFactory.StartCount);
        Assert.Equal(1, processFactory.DisposedCount);
        Assert.Equal(second, processFactory.ProjectDir);
    }

    [Fact]
    public async Task EnsureReadyAsync_SwitchToExternalServer_DisposesManagedProcess()
    {
        var root = NewProjectDir();
        var processFactory = new FakeProcessFactory();
        using var httpClient = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/global/health" => Json(new { healthy = true, version = "test" }),
            "/path" => Json(new { worktree = root, directory = root }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new OpenCodeServerClient(httpClient, processFactory);

        await client.EnsureReadyAsync(ManagedProject(root), CancellationToken.None);
        await client.EnsureReadyAsync(ExternalProject(root), CancellationToken.None);

        Assert.Equal(1, processFactory.StartCount);
        Assert.Equal(1, processFactory.DisposedCount);
    }

    [Fact]
    public async Task StartSessionAsync_CreatesSessionWithoutSendingPrompt()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root);
        using var httpClient = new HttpClient(new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/ses-1/message")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                "/session" when request.Method == HttpMethod.Post => Json(new { id = "ses-1", directory = root, title = "run-1 project-a 01.md" }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var client = new OpenCodeServerClient(httpClient, new FakeProcessFactory());

        var session = await client.StartSessionAsync(project, "run-1 project-a 01.md", CancellationToken.None);

        Assert.Equal("ses-1", session.SessionId);
        Assert.Equal(root, session.ProjectDir);
    }

    [Fact]
    public async Task SendPromptAsync_Inline_SendsStableMessageIdAndOriginalContent()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root) with { OpenCodeOverrides = ExternalProject(root).OpenCodeOverrides with { PromptTransport = PromptTransport.Inline } };
        string? body = null;
        using var httpClient = new HttpClient(new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/ses-1/message")
            {
                body = await request.Content!.ReadAsStringAsync();
                return Json(new { info = new { id = "msg-1", role = "assistant", time = new { created = 1, completed = 2 } }, parts = Array.Empty<object>() });
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var client = new OpenCodeServerClient(httpClient, new FakeProcessFactory());
        var content = "# Заголовок\n\nDo not rewrite this prompt.";

        var result = await client.SendPromptAsync(project, "ses-1", new PromptPayload
        {
            Content = content,
            SourcePath = Path.Combine(root, "prompts", "01.md"),
            MessageId = "msg-1",
            Transport = PromptTransport.Inline
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("msg-1", document.RootElement.GetProperty("messageID").GetString());
        Assert.Equal(content, document.RootElement.GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.False(document.RootElement.TryGetProperty("model", out _));
        Assert.False(document.RootElement.TryGetProperty("agent", out _));
    }

    [Fact]
    public async Task SendPromptAsync_AutoLargePrompt_UsesFileAttachmentWithoutPromptText()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root);
        var sourcePath = Path.Combine(root, "prompts", "01.md");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "original");
        string? body = null;
        using var httpClient = new HttpClient(new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/ses-1/message")
            {
                body = await request.Content!.ReadAsStringAsync();
                return Json(new { info = new { id = "msg-2", role = "assistant", time = new { created = 1, completed = 2 } }, parts = Array.Empty<object>() });
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var client = new OpenCodeServerClient(httpClient, new FakeProcessFactory());

        await client.SendPromptAsync(project, "ses-1", new PromptPayload
        {
            Content = "1234567890",
            SourcePath = sourcePath,
            MessageId = "msg-2",
            Transport = PromptTransport.Auto,
            MaxInlinePromptChars = 5
        }, CancellationToken.None);

        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body!);
        var parts = document.RootElement.GetProperty("parts");
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("file", parts[1].GetProperty("type").GetString());
        Assert.DoesNotContain("1234567890", body!);
        Assert.Equal("01.md", parts[1].GetProperty("filename").GetString());
    }

    [Fact]
    public async Task TryRecoverStepAsync_DoesNotCompleteFromUnrelatedAssistantMessage()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root);
        var recoverySent = false;
        using var httpClient = new HttpClient(new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/ses-1/message" && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync();
                recoverySent = body.Contains("msg-1-recover", StringComparison.Ordinal);
                return Json(new { info = new { id = "msg-1-recover", role = "assistant", time = new { created = 3, completed = 4 } }, parts = Array.Empty<object>() });
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                "/session/ses-1" => Json(new { id = "ses-1", directory = root, title = "test" }),
                "/session/status" => Json(new Dictionary<string, object> { ["ses-1"] = new { type = "idle" } }),
                "/session/ses-1/message" => Json(new object[]
                {
                    new { info = new { id = "msg-1", role = "user", time = new { created = 1 } } },
                    new { info = new { id = "assistant-old", role = "assistant", parentID = "other", time = new { created = 1, completed = 2 } } }
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var client = new OpenCodeServerClient(httpClient, new FakeProcessFactory());
        var manifest = new RunManifest
        {
            RunId = "run-1",
            ProjectId = project.Id,
            SessionId = "ses-1",
            ProjectDirSnapshot = root
        };
        var step = new WorkflowStep
        {
            Id = WorkflowStepId.Task,
            Kind = PromptKind.Task,
            SourcePath = Path.Combine(root, "prompts", "01.md"),
            SessionMessageId = "msg-1"
        };

        var result = await client.TryRecoverStepAsync(project, manifest, step, CancellationToken.None);

        Assert.Equal(StepRecoveryOutcome.ConservativeContinueSent, result.Outcome);
        Assert.True(recoverySent);
    }

    [Fact]
    public async Task TryRecoverStepAsync_WhenRecoveryPromptFails_ReturnsFailed()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root);
        using var httpClient = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/ses-1/message" && request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Json(new { info = new { id = "msg-1-recover", error = new { message = "recovery failed" } } }));
            }

            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                "/session/ses-1" => Json(new { id = "ses-1", directory = root, title = "test" }),
                "/session/status" => Json(new Dictionary<string, object> { ["ses-1"] = new { type = "idle" } }),
                "/session/ses-1/message" => Json(new object[] { new { info = new { id = "msg-1", role = "user", time = new { created = 1 } } } }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        }));
        var client = new OpenCodeServerClient(httpClient, new FakeProcessFactory());
        var manifest = new RunManifest { RunId = "run-1", ProjectId = project.Id, SessionId = "ses-1", ProjectDirSnapshot = root };
        var step = new WorkflowStep { Id = WorkflowStepId.Task, Kind = PromptKind.Task, SourcePath = Path.Combine(root, "prompts", "01.md"), SessionMessageId = "msg-1" };

        var result = await client.TryRecoverStepAsync(project, manifest, step, CancellationToken.None);

        Assert.Equal(StepRecoveryOutcome.Failed, result.Outcome);
        Assert.Contains("recovery failed", result.Message, StringComparison.Ordinal);
    }

    private static ProjectProfile ManagedProject(string root)
    {
        return new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = root,
            OpenCodeOverrides = new OpenCodeSettings { ManageOpenCodeServer = true, ServerUrl = "http://127.0.0.1:50123" }
        };
    }

    private static ProjectProfile ExternalProject(string root)
    {
        return new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = root,
            OpenCodeOverrides = new OpenCodeSettings { ManageOpenCodeServer = false, ServerUrl = "http://127.0.0.1:50125" }
        };
    }

    private static string NewProjectDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static HttpResponseMessage Json(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handle)
            : this(request => Task.FromResult(handle(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handle(request);
        }
    }

    private sealed class FakeProcessFactory : IOpenCodeServerProcessFactory
    {
        public string? ProjectDir { get; private set; }

        public int Port { get; private set; }

        public int StartCount { get; private set; }

        public int DisposedCount { get; private set; }

        public IOpenCodeServerProcess Start(ProjectProfile project, int port)
        {
            ProjectDir = project.ProjectDir;
            Port = port;
            StartCount++;
            return new FakeProcess(() => DisposedCount++);
        }
    }

    private sealed class FakeProcess(Action onDispose) : IOpenCodeServerProcess
    {
        public bool HasExited => false;

        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
