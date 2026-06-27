using System.Net;
using System.Text.Json;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Prompts;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Infrastructure;
using OpenCodeQueue.Infrastructure.OpenCode;

namespace OpenCodeQueue.Tests;

public sealed class OpenCodeServerClientTests
{
    [Fact]
    public async Task EnsureReadyAsync_ServerMismatch_ThrowsRussianProjectMismatch()
    {
        var selected = NewProjectDir();
        var server = NewProjectDir();
        var project = new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = selected,
            OpenCodeOverrides = new OpenCodeSettings { ServerUrl = "http://127.0.0.1:50124" }
        };
        using var httpClient = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/global/health" => Json(new { healthy = true, version = "test" }),
            "/path" => Json(new { worktree = server, directory = server }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new OpenCodeServerClient(httpClient);

        var exception = await Assert.ThrowsAsync<OpenCodeProjectMismatchException>(() => client.EnsureReadyAsync(project, CancellationToken.None));

        Assert.Contains("В registry выбран projectDir", exception.Message);
        Assert.Contains(selected, exception.Message);
        Assert.Contains(server, exception.Message);
    }

    [Fact]
    public async Task StartSessionAsync_CreatesSessionWithoutSendingPrompt()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root);
        string? body = null;
        using var httpClient = new HttpClient(new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/ses-1/message")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (request.RequestUri!.AbsolutePath == "/session" && request.Method == HttpMethod.Post)
            {
                body = await request.Content!.ReadAsStringAsync();
                return Json(new { id = "ses-1", directory = root, title = "run-1 project-a 01.md" });
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var client = new OpenCodeServerClient(httpClient);

        var session = await client.StartSessionAsync(project, "run-1 project-a 01.md", CancellationToken.None);

        Assert.Equal("ses-1", session.SessionId);
        Assert.Equal(root, session.ProjectDir);
        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("run-1 project-a 01.md", document.RootElement.GetProperty("title").GetString());
        Assert.False(document.RootElement.TryGetProperty("model", out _));
        Assert.False(document.RootElement.TryGetProperty("providerID", out _));
    }

    [Fact]
    public async Task SendPromptAsync_Inline_SendsStableMessageIdAndOriginalContent()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root) with { OpenCodeOverrides = ExternalProject(root).OpenCodeOverrides with { PromptTransport = PromptTransport.Inline } };
        string? body = null;
        var posted = false;
        using var httpClient = new HttpClient(new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/ses-1/prompt_async" && request.Method == HttpMethod.Post)
            {
                body = await request.Content!.ReadAsStringAsync();
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                "/session/status" => Json(new Dictionary<string, object> { ["ses-1"] = new { type = "idle" } }),
                "/session/ses-1/message" when request.Method == HttpMethod.Get && !posted => Json(Array.Empty<object>()),
                "/session/ses-1/message" when request.Method == HttpMethod.Get => Json(new object[]
                {
                    new { info = new { id = "msg-1", role = "user", time = new { created = 1 }, parentID = (string?)null }, parts = Array.Empty<object>() },
                    new { info = new { id = "msg-assistant-1", role = "assistant", parentID = "msg-1", time = new { created = 1, completed = 2 } }, parts = Array.Empty<object>() }
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var client = new OpenCodeServerClient(httpClient);
        var content = "# Заголовок\n\nDo not rewrite this prompt.";

        var result = await client.SendPromptAsync(project, "ses-1", new PromptPayload
        {
            Content = content,
            SourcePath = Path.Combine(root, "prompts", "01.md"),
            MessageId = "msg-1",
            Transport = PromptTransport.Inline
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("msg-1", result.MessageId);
        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("msg-1", document.RootElement.GetProperty("messageID").GetString());
        Assert.Equal(content, document.RootElement.GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("openai", document.RootElement.GetProperty("providerID").GetString());
        Assert.Equal("openai", document.RootElement.GetProperty("model").GetProperty("providerID").GetString());
        Assert.Equal("gpt-5.5", document.RootElement.GetProperty("model").GetProperty("modelID").GetString());
        Assert.Equal("gpt-5.5", document.RootElement.GetProperty("modelID").GetString());
        Assert.Equal("high", document.RootElement.GetProperty("reasoningEffort").GetString());
        Assert.False(document.RootElement.TryGetProperty("agent", out _));
    }

    [Fact]
    public async Task SendPromptAsync_WhenAssistantTextIsReturned_ExposesLastAssistantText()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root) with { OpenCodeOverrides = ExternalProject(root).OpenCodeOverrides with { PromptTransport = PromptTransport.Inline } };
        var posted = false;
        using var httpClient = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                "/session/status" => Json(new Dictionary<string, object> { ["ses-1"] = new { type = "idle" } }),
                "/session/ses-1/message" when request.Method == HttpMethod.Get && !posted => Json(Array.Empty<object>()),
                "/session/ses-1/message" when request.Method == HttpMethod.Get => Json(new object[]
                {
                    new { info = new { id = "msg-1", role = "user", time = new { created = 1 }, parentID = (string?)null }, parts = Array.Empty<object>() },
                    new { info = new { id = "msg-assistant-1", role = "assistant", parentID = "msg-1", time = new { created = 1, completed = 2 } }, parts = new object[] { new { type = "text", text = "NEEDS_MANUAL_INTERVENTION: нужен токен" } } }
                }),
                "/session/ses-1/prompt_async" when request.Method == HttpMethod.Post => SetPostedNoContent(ref posted),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new OpenCodeServerClient(httpClient);

        var result = await client.SendPromptAsync(project, "ses-1", new PromptPayload
        {
            Content = "prompt",
            SourcePath = Path.Combine(root, "prompts", "01.md"),
            MessageId = "msg-1",
            Transport = PromptTransport.Inline
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("NEEDS_MANUAL_INTERVENTION", result.LastAssistantText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendPromptAsync_WhenSessionHasCompletedAssistant_SendsParentId()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root) with { OpenCodeOverrides = ExternalProject(root).OpenCodeOverrides with { PromptTransport = PromptTransport.Inline } };
        string? body = null;
        var posted = false;
        using var httpClient = new HttpClient(new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/ses-1/prompt_async" && request.Method == HttpMethod.Post)
            {
                body = await request.Content!.ReadAsStringAsync();
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                "/session/status" => Json(new Dictionary<string, object> { ["ses-1"] = new { type = "idle" } }),
                "/session/ses-1/message" when request.Method == HttpMethod.Get && !posted => Json(new object[]
                {
                    new { info = new { id = "msg-assistant-1", role = "assistant", time = new { created = 1, completed = 2 }, parentID = "msg-1" }, parts = Array.Empty<object>() }
                }),
                "/session/ses-1/message" when request.Method == HttpMethod.Get => Json(new object[]
                {
                    new { info = new { id = "msg-assistant-1", role = "assistant", time = new { created = 1, completed = 2 }, parentID = "msg-1" }, parts = Array.Empty<object>() },
                    new { info = new { id = "msg-2", role = "user", time = new { created = 3 }, parentID = (string?)null }, parts = Array.Empty<object>() },
                    new { info = new { id = "msg-assistant-2", role = "assistant", parentID = "msg-2", time = new { created = 3, completed = 4 } }, parts = Array.Empty<object>() }
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var client = new OpenCodeServerClient(httpClient);

        await client.SendPromptAsync(project, "ses-1", new PromptPayload
        {
            Content = "prompt",
            SourcePath = Path.Combine(root, "prompts", "02.md"),
            MessageId = "msg-2",
            Transport = PromptTransport.Inline
        }, CancellationToken.None);

        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("msg-assistant-1", document.RootElement.GetProperty("parentID").GetString());
    }

    [Fact]
    public async Task SendPromptAsync_WhenAssistantToolIsPending_ReturnsPermissionRequest()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root) with
        {
            OpenCodeOverrides = ExternalProject(root).OpenCodeOverrides with
            {
                PromptTransport = PromptTransport.Inline,
                Resilience = new ResilienceSettings { StepTimeoutMinutes = 1, IdleTimeoutMinutes = 1 }
            }
        };
        using var httpClient = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/global/health" => Json(new { healthy = true, version = "test" }),
            "/path" => Json(new { worktree = root, directory = root }),
            "/session/status" => Json(new Dictionary<string, object> { ["ses-1"] = new { type = "idle" } }),
            "/session/ses-1/prompt_async" when request.Method == HttpMethod.Post => new HttpResponseMessage(HttpStatusCode.NoContent),
            "/session/ses-1/message" when request.Method == HttpMethod.Get => Json(new object[]
            {
                new { info = new { id = "msg-1", role = "user", time = new { created = 1 }, parentID = (string?)null }, parts = Array.Empty<object>() },
                new
                {
                    info = new { id = "msg-2", role = "assistant", time = new { created = 2 }, parentID = "msg-1" },
                    parts = new object[] { new { type = "tool", tool = "edit", state = new { status = "pending", input = new { }, raw = "" } } }
                }
            }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new OpenCodeServerClient(httpClient);

        var result = await client.SendPromptAsync(project, "ses-1", new PromptPayload
        {
            Content = "prompt",
            SourcePath = Path.Combine(root, "prompts", "01.md"),
            MessageId = "msg-1",
            Transport = PromptTransport.Inline
        }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("permission request", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("edit", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendPromptAsync_WhenAssistantQuestionToolIsRunning_ReturnsQuestionRequest()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root) with
        {
            OpenCodeOverrides = ExternalProject(root).OpenCodeOverrides with
            {
                PromptTransport = PromptTransport.Inline,
                Resilience = new ResilienceSettings { StepTimeoutMinutes = 1, IdleTimeoutMinutes = 1 }
            }
        };
        using var httpClient = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/global/health" => Json(new { healthy = true, version = "test" }),
            "/path" => Json(new { worktree = root, directory = root }),
            "/session/status" => Json(new Dictionary<string, object> { ["ses-1"] = new { type = "idle" } }),
            "/session/ses-1/prompt_async" when request.Method == HttpMethod.Post => new HttpResponseMessage(HttpStatusCode.NoContent),
            "/session/ses-1/message" when request.Method == HttpMethod.Get => Json(new object[]
            {
                new { info = new { id = "msg-1", role = "user", time = new { created = 1 }, parentID = (string?)null }, parts = Array.Empty<object>() },
                new
                {
                    info = new { id = "msg-2", role = "assistant", time = new { created = 2 }, parentID = "msg-1" },
                    parts = new object[] { new { type = "tool", tool = "question", state = new { status = "running", input = new { }, raw = "" } } }
                }
            }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new OpenCodeServerClient(httpClient);

        var result = await client.SendPromptAsync(project, "ses-1", new PromptPayload
        {
            Content = "prompt",
            SourcePath = Path.Combine(root, "prompts", "01.md"),
            MessageId = "msg-1",
            Transport = PromptTransport.Inline
        }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("question:", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendPromptAsync_WhenAssistantToolIsPendingButSessionBusy_WaitsForCompletion()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root) with
        {
            OpenCodeOverrides = ExternalProject(root).OpenCodeOverrides with
            {
                PromptTransport = PromptTransport.Inline,
                Resilience = new ResilienceSettings { StepTimeoutMinutes = 1, IdleTimeoutMinutes = 1 }
            }
        };
        var posted = false;
        var messageRequestsAfterPost = 0;
        using var httpClient = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/ses-1/prompt_async" && request.Method == HttpMethod.Post)
            {
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                "/session/status" => Json(new Dictionary<string, object> { ["ses-1"] = new { type = messageRequestsAfterPost == 0 ? "busy" : "idle" } }),
                "/session/ses-1/message" when request.Method == HttpMethod.Get && !posted => Json(Array.Empty<object>()),
                "/session/ses-1/message" when request.Method == HttpMethod.Get && ++messageRequestsAfterPost == 1 => Json(new object[]
                {
                    new { info = new { id = "msg-1", role = "user", time = new { created = 1 }, parentID = (string?)null }, parts = Array.Empty<object>() },
                    new
                    {
                        info = new { id = "msg-2", role = "assistant", time = new { created = 2 }, parentID = "msg-1" },
                        parts = new object[] { new { type = "tool", tool = "edit", state = new { status = "pending", input = new { }, raw = "" } } }
                    }
                }),
                "/session/ses-1/message" when request.Method == HttpMethod.Get => Json(new object[]
                {
                    new { info = new { id = "msg-1", role = "user", time = new { created = 1 }, parentID = (string?)null }, parts = Array.Empty<object>() },
                    new { info = new { id = "msg-2", role = "assistant", time = new { created = 2, completed = 3 }, parentID = "msg-1" }, parts = new object[] { new { type = "text", text = "done" } } }
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var client = new OpenCodeServerClient(httpClient);

        var result = await client.SendPromptAsync(project, "ses-1", new PromptPayload
        {
            Content = "prompt",
            SourcePath = Path.Combine(root, "prompts", "01.md"),
            MessageId = "msg-1",
            Transport = PromptTransport.Inline
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("done", result.LastAssistantText);
    }

    [Fact]
    public async Task SendPromptAsync_WhenAssistantParentDiffers_UsesLastAssistantAfterUserMessage()
    {
        var root = NewProjectDir();
        var project = ExternalProject(root) with { OpenCodeOverrides = ExternalProject(root).OpenCodeOverrides with { PromptTransport = PromptTransport.Inline } };
        var posted = false;
        using var httpClient = new HttpClient(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/global/health" => Json(new { healthy = true, version = "test" }),
            "/path" => Json(new { worktree = root, directory = root }),
            "/session/status" => Json(new Dictionary<string, object> { ["ses-1"] = new { type = "idle" } }),
            "/session/ses-1/message" when request.Method == HttpMethod.Get && !posted => Json(new object[]
            {
                new { info = new { id = "previous-assistant", role = "assistant", time = new { created = 1, completed = 2 }, parentID = "root-user" }, parts = Array.Empty<object>() }
            }),
            "/session/ses-1/message" when request.Method == HttpMethod.Get => Json(new object[]
            {
                new { info = new { id = "previous-assistant", role = "assistant", time = new { created = 1, completed = 2 }, parentID = "root-user" }, parts = Array.Empty<object>() },
                new { info = new { id = "msg-quality", role = "user", time = new { created = 3 }, parentID = (string?)null }, parts = Array.Empty<object>() },
                new { info = new { id = "assistant-1", role = "assistant", time = new { created = 4, completed = 5 }, parentID = "root-user" }, parts = new object[] { new { type = "text", text = "intermediate" } } },
                new { info = new { id = "assistant-2", role = "assistant", time = new { created = 6, completed = 7 }, parentID = "root-user" }, parts = new object[] { new { type = "text", text = "final" } } }
            }),
            "/session/ses-1/prompt_async" when request.Method == HttpMethod.Post => SetPostedNoContent(ref posted),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }));
        var client = new OpenCodeServerClient(httpClient);

        var result = await client.SendPromptAsync(project, "ses-1", new PromptPayload
        {
            Content = "prompt",
            SourcePath = Path.Combine(root, "prompts", "01.md"),
            MessageId = "msg-quality",
            Transport = PromptTransport.Inline
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("final", result.LastAssistantText);
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
        var posted = false;
        using var httpClient = new HttpClient(new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/session/ses-1/prompt_async" && request.Method == HttpMethod.Post)
            {
                body = await request.Content!.ReadAsStringAsync();
                posted = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/global/health" => Json(new { healthy = true, version = "test" }),
                "/path" => Json(new { worktree = root, directory = root }),
                "/session/status" => Json(new Dictionary<string, object> { ["ses-1"] = new { type = "idle" } }),
                "/session/ses-1/message" when request.Method == HttpMethod.Get && !posted => Json(Array.Empty<object>()),
                "/session/ses-1/message" when request.Method == HttpMethod.Get => Json(new object[]
                {
                    new { info = new { id = "msg-2", role = "user", time = new { created = 1 }, parentID = (string?)null }, parts = Array.Empty<object>() },
                    new { info = new { id = "msg-assistant-2", role = "assistant", parentID = "msg-2", time = new { created = 1, completed = 2 } }, parts = Array.Empty<object>() }
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));
        var client = new OpenCodeServerClient(httpClient);

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

    private static ProjectProfile ExternalProject(string root)
    {
        return new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = root,
            OpenCodeOverrides = new OpenCodeSettings { ServerUrl = "http://127.0.0.1:50125" }
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

    private static HttpResponseMessage SetPostedNoContent(ref bool posted)
    {
        posted = true;
        return new HttpResponseMessage(HttpStatusCode.NoContent);
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
}
