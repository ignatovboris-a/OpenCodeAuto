using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Infrastructure.Files;
using OpenCodeQueue.Infrastructure.Json;

namespace OpenCodeQueue.Infrastructure.OpenCode;

public sealed class OpenCodeServerClient : IOpenCodeClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly IOpenCodeServerProcessFactory processFactory;
    private IOpenCodeServerProcess? managedProcess;
    private string? managedProjectDir;
    private string? managedServerUrl;
    private string? readyServerUrl;

    public OpenCodeServerClient(HttpClient httpClient, IOpenCodeServerProcessFactory processFactory)
    {
        this.httpClient = httpClient;
        this.processFactory = processFactory;
    }

    public async Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var settings = project.OpenCodeOverrides;
        var serverUrl = NormalizeServerUrl(settings.ServerUrl);
        if (settings.ManageOpenCodeServer)
        {
            var shouldRestart = managedProcess is null
                || managedProcess.HasExited
                || !string.Equals(managedServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase)
                || managedProjectDir is null
                || !PathResolver.AreSamePath(managedProjectDir, project.ProjectDir);
            if (shouldRestart)
            {
                if (managedProcess is not null)
                {
                    await managedProcess.DisposeAsync();
                }

                managedProcess = processFactory.Start(project, GetPort(serverUrl));
                managedProjectDir = project.ProjectDir;
                managedServerUrl = serverUrl;
            }
        }
        else if (managedProcess is not null)
        {
            await managedProcess.DisposeAsync();
            managedProcess = null;
            managedProjectDir = null;
            managedServerUrl = null;
        }

        await WaitForHealthAsync(project, serverUrl, cancellationToken);
        await EnsureProjectMatchesAsync(project, serverUrl, cancellationToken);
        readyServerUrl = serverUrl;
        await SaveConnectionStateAsync(project, serverUrl, settings.ManageOpenCodeServer, cancellationToken);
    }

    public async Task<OpenCodeSession> StartSessionAsync(ProjectProfile project, string title, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(project, cancellationToken);
        using var document = await SendJsonAsync(project, HttpMethod.Post, "/session", new { title }, cancellationToken);
        var id = ReadRequiredString(document.RootElement, "id", "session id");
        var directory = ReadString(document.RootElement, "directory") ?? project.ProjectDir;
        return new OpenCodeSession(id, directory, ReadString(document.RootElement, "title") ?? title);
    }

    public async Task<OpenCodeSessionDetails> GetSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(project, cancellationToken);
        using var sessionDocument = await SendJsonAsync(project, HttpMethod.Get, $"/session/{Uri.EscapeDataString(sessionId)}", null, cancellationToken);
        var session = ReadSession(sessionDocument.RootElement, project.ProjectDir);
        var status = await GetSessionStatusAsync(project, sessionId, cancellationToken);
        var messages = await GetMessagesAsync(project, sessionId, cancellationToken);
        return new OpenCodeSessionDetails(session, status, messages);
    }

    public async Task<OpenCodeMessageResult> SendPromptAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(project, cancellationToken);
        var body = BuildMessageBody(project, payload);
        using var document = await SendJsonAsync(project, HttpMethod.Post, $"/session/{Uri.EscapeDataString(sessionId)}/message", body, cancellationToken);
        var info = document.RootElement.TryGetProperty("info", out var infoElement) ? infoElement : document.RootElement;
        var messageId = ReadString(info, "id") ?? payload.MessageId;
        var failed = TryReadError(info, out var error);
        return new OpenCodeMessageResult(!failed, messageId, HasAssistantResponse(document.RootElement), error);
    }

    public async Task<OpenCodeSessionStatus> GetSessionStatusAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(project, cancellationToken);
        using var document = await SendJsonAsync(project, HttpMethod.Get, "/session/status", null, cancellationToken);
        if (!document.RootElement.TryGetProperty(sessionId, out var statusElement))
        {
            return new OpenCodeSessionStatus(OpenCodeSessionState.Unknown, "Статус session отсутствует в ответе OpenCode.");
        }

        return ReadStatus(statusElement);
    }

    public async Task<StepRecoveryResult> TryRecoverStepAsync(ProjectProfile project, RunManifest manifest, WorkflowStep step, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifest.SessionId))
        {
            return new StepRecoveryResult(StepRecoveryOutcome.NotFound, "В manifest нет sessionId.");
        }

        var details = await GetSessionAsync(project, manifest.SessionId, cancellationToken);
        var messageId = step.SessionMessageId;
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            var message = details.Messages.FirstOrDefault(item => string.Equals(item.Id, messageId, StringComparison.Ordinal));
            if (message is not null && message.IsFailed)
            {
                return new StepRecoveryResult(StepRecoveryOutcome.Failed, message.ErrorMessage ?? "OpenCode сообщил об ошибке message.");
            }

            if (message is not null && details.Messages.Any(item => item.Role == "assistant" && item.IsCompleted && string.Equals(item.ParentId, messageId, StringComparison.Ordinal)))
            {
                return new StepRecoveryResult(StepRecoveryOutcome.Completed, "Шаг найден в session и имеет завершённый ответ assistant.");
            }
        }

        var recoveryId = string.IsNullOrWhiteSpace(messageId) ? $"recover-{Guid.NewGuid():N}" : $"{messageId}-recover";
        var recoveryPayload = new PromptPayload
        {
            MessageId = recoveryId,
            SourcePath = step.SourcePath,
            Transport = PromptTransport.Inline,
            RunId = manifest.RunId,
            StepId = step.Id.Value,
            Content = "Консервативное восстановление OpenCodeQueue: проверь предыдущий незавершённый шаг в этой session. Если исходный prompt уже выполнен, кратко сообщи результат и не повторяй опасные действия. Если выполнение не начиналось или прервалось, продолжи его с учётом уже видимого контекста session."
        };
        var result = await SendPromptAsync(project, manifest.SessionId, recoveryPayload, cancellationToken);
        return result.IsSuccess
            ? new StepRecoveryResult(StepRecoveryOutcome.ConservativeContinueSent, "Отправлен ConservativeContinue recovery prompt; исходный prompt повторно не отправлялся.")
            : new StepRecoveryResult(StepRecoveryOutcome.Failed, result.ErrorMessage ?? "Не удалось отправить recovery prompt.");
    }

    public async Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(project, cancellationToken);
        using var _ = await SendJsonAsync(project, HttpMethod.Post, $"/session/{Uri.EscapeDataString(sessionId)}/abort", new { }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (managedProcess is not null)
        {
            await managedProcess.DisposeAsync();
        }

        httpClient.Dispose();
    }

    private async Task WaitForHealthAsync(ProjectProfile project, string serverUrl, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(project.OpenCodeOverrides.ManageOpenCodeServer ? 30 : 5);
        Exception? lastException = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = CreateRequest(project, HttpMethod.Get, serverUrl, "/global/health", null);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                lastException = exception;
            }

            await Task.Delay(300, cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline && managedProcess?.HasExited != true);

        throw new OpenCodeClientException($"OpenCode server недоступен по адресу {serverUrl}/global/health.", lastException ?? new TimeoutException());
    }

    private async Task EnsureProjectMatchesAsync(ProjectProfile project, string serverUrl, CancellationToken cancellationToken)
    {
        string? serverPath = null;
        try
        {
            using var pathDocument = await SendJsonAsync(project, HttpMethod.Get, "/path", null, cancellationToken, serverUrl);
            serverPath = ReadString(pathDocument.RootElement, "worktree") ?? ReadString(pathDocument.RootElement, "directory");
        }
        catch (OpenCodeClientException)
        {
            using var projectDocument = await SendJsonAsync(project, HttpMethod.Get, "/project/current", null, cancellationToken, serverUrl);
            serverPath = ReadString(projectDocument.RootElement, "worktree");
        }

        if (string.IsNullOrWhiteSpace(serverPath) || !PathResolver.AreSamePath(project.ProjectDir, serverPath))
        {
            throw new OpenCodeProjectMismatchException(project.ProjectDir, serverPath ?? "неизвестно");
        }
    }

    private async Task<JsonDocument> SendJsonAsync(ProjectProfile project, HttpMethod method, string path, object? body, CancellationToken cancellationToken, string? serverUrlOverride = null)
    {
        var serverUrl = serverUrlOverride ?? readyServerUrl ?? NormalizeServerUrl(project.OpenCodeOverrides.ServerUrl);
        using var request = CreateRequest(project, method, serverUrl, path, body);
        using var response = await SendAsync(request, method, path, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new OpenCodeClientException($"OpenCode server вернул HTTP {(int)response.StatusCode} для {method} {path}: {TrimForError(text)}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return JsonDocument.Parse("{}");
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new OpenCodeClientException($"OpenCode server вернул некорректный JSON для {method} {path}.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new OpenCodeClientException($"Не удалось выполнить запрос к OpenCode server: {method} {path}.", exception);
        }
    }

    private HttpRequestMessage CreateRequest(ProjectProfile project, HttpMethod method, string serverUrl, string path, object? body)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(serverUrl), path));
        var settings = project.OpenCodeOverrides;
        if (!string.IsNullOrWhiteSpace(settings.ServerPassword))
        {
            var raw = $"{settings.ServerUsername}:{settings.ServerPassword}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        return request;
    }

    private object BuildMessageBody(ProjectProfile project, PromptPayload payload)
    {
        var transport = payload.Transport == PromptTransport.Auto
            ? payload.Content.Length <= payload.MaxInlinePromptChars ? PromptTransport.Inline : PromptTransport.FileAttachment
            : payload.Transport;
        var parts = new List<object>();
        if (transport == PromptTransport.Inline)
        {
            parts.Add(new { type = "text", text = payload.Content });
        }
        else
        {
            if (!File.Exists(payload.SourcePath))
            {
                throw new OpenCodeClientException($"Prompt-файл для attachment не найден: {payload.SourcePath}");
            }

            parts.Add(new { type = "text", text = "Прикреплённый Markdown-файл является основным prompt. Выполни его содержимое без перевода, переписывания или нормализации." });
            parts.Add(new { type = "file", mime = "text/markdown", filename = Path.GetFileName(payload.SourcePath), url = new Uri(Path.GetFullPath(payload.SourcePath)).AbsoluteUri });
        }

        var body = new Dictionary<string, object>
        {
            ["messageID"] = payload.MessageId,
            ["parts"] = parts
        };
        if (!string.IsNullOrWhiteSpace(project.OpenCodeOverrides.Model))
        {
            body["model"] = project.OpenCodeOverrides.Model;
        }

        if (!string.IsNullOrWhiteSpace(project.OpenCodeOverrides.Agent))
        {
            body["agent"] = project.OpenCodeOverrides.Agent;
        }

        return body;
    }

    private async Task<IReadOnlyList<OpenCodeMessage>> GetMessagesAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
    {
        using var document = await SendJsonAsync(project, HttpMethod.Get, $"/session/{Uri.EscapeDataString(sessionId)}/message", null, cancellationToken);
        var messages = new List<OpenCodeMessage>();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var info = item.TryGetProperty("info", out var infoElement) ? infoElement : item;
            var id = ReadString(info, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var role = ReadString(info, "role") ?? "unknown";
            var failed = TryReadError(info, out var error);
            var parentId = ReadString(info, "parentID");
            var completed = role != "assistant" || (info.TryGetProperty("time", out var time) && time.TryGetProperty("completed", out _));
            messages.Add(new OpenCodeMessage(id, role, completed, failed, parentId, error));
        }

        return messages;
    }

    private static OpenCodeSession ReadSession(JsonElement element, string fallbackProjectDir)
    {
        return new OpenCodeSession(
            ReadRequiredString(element, "id", "session id"),
            ReadString(element, "directory") ?? fallbackProjectDir,
            ReadString(element, "title"));
    }

    private static OpenCodeSessionStatus ReadStatus(JsonElement element)
    {
        var type = ReadString(element, "type");
        return type switch
        {
            "idle" => new OpenCodeSessionStatus(OpenCodeSessionState.Idle),
            "busy" => new OpenCodeSessionStatus(OpenCodeSessionState.Busy),
            "retry" => new OpenCodeSessionStatus(OpenCodeSessionState.Retry, ReadString(element, "message")),
            _ => new OpenCodeSessionStatus(OpenCodeSessionState.Unknown, type)
        };
    }

    private static bool TryReadError(JsonElement info, out string? error)
    {
        error = null;
        if (!info.TryGetProperty("error", out var errorElement) || errorElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        error = errorElement.TryGetProperty("data", out var data)
            ? ReadString(data, "message")
            : ReadString(errorElement, "message") ?? errorElement.ToString();
        return true;
    }

    private static bool HasAssistantResponse(JsonElement root)
    {
        if (root.TryGetProperty("info", out var info))
        {
            return string.Equals(ReadString(info, "role"), "assistant", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string description)
    {
        return ReadString(element, propertyName) ?? throw new OpenCodeClientException($"OpenCode server вернул ответ без поля {description}.");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return JsonElementReader.ReadString(element, propertyName);
    }

    private static string NormalizeServerUrl(string serverUrl)
    {
        return serverUrl.TrimEnd('/') + "/";
    }

    private static int GetPort(string serverUrl)
    {
        var uri = new Uri(serverUrl);
        return uri.IsDefaultPort ? 4096 : uri.Port;
    }

    private static string TrimForError(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "пустой ответ";
        }

        return text.Length <= 500 ? text : text[..500];
    }

    private static async Task SaveConnectionStateAsync(ProjectProfile project, string serverUrl, bool managed, CancellationToken cancellationToken)
    {
        var path = Path.Combine(ProjectPaths.StateDir(project), "opencode-server.json");
        await AtomicFileWriter.WriteAsync(path, async (stream, ct) =>
        {
            await JsonSerializer.SerializeAsync(stream, new { serverUrl, managed, updatedAt = DateTimeOffset.UtcNow }, JsonOptions, ct);
        }, cancellationToken);
    }
}
