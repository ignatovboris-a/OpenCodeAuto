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
    private const int RepeatedAssistantLoopThreshold = 6;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim readinessGate = new(1, 1);

    public OpenCodeServerClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        await readinessGate.WaitAsync(cancellationToken);
        try
        {
            var serverUrl = NormalizeServerUrl(project.OpenCodeOverrides.ServerUrl);
            await WaitForHealthAsync(project, serverUrl, cancellationToken);
            await EnsureProjectMatchesAsync(project, serverUrl, cancellationToken);
            await SaveConnectionStateAsync(project, serverUrl, cancellationToken);
        }
        finally
        {
            readinessGate.Release();
        }
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
        var messageId = payload.MessageId;
        await SendNoContentAsync(project, HttpMethod.Post, $"/session/{Uri.EscapeDataString(sessionId)}/prompt_async", body, cancellationToken);
        return await WaitForAssistantResponseAsync(project, sessionId, messageId, cancellationToken);
    }

    public async Task<OpenCodeMessageResult> WaitForPromptAsync(ProjectProfile project, string sessionId, string messageId, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(project, cancellationToken);
        return await WaitForAssistantResponseAsync(project, sessionId, messageId, cancellationToken);
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

    public async Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(project, cancellationToken);
        using var _ = await SendJsonAsync(project, HttpMethod.Post, $"/session/{Uri.EscapeDataString(sessionId)}/abort", new { }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        readinessGate.Dispose();
        httpClient.Dispose();
        await ValueTask.CompletedTask;
    }

    private async Task WaitForHealthAsync(ProjectProfile project, string serverUrl, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
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
        while (DateTimeOffset.UtcNow < deadline);

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
        var serverUrl = serverUrlOverride ?? NormalizeServerUrl(project.OpenCodeOverrides.ServerUrl);
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

    private async Task SendNoContentAsync(ProjectProfile project, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var serverUrl = NormalizeServerUrl(project.OpenCodeOverrides.ServerUrl);
        using var request = CreateRequest(project, method, serverUrl, path, body);
        using var response = await SendAsync(request, method, path, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new OpenCodeClientException($"OpenCode server вернул HTTP {(int)response.StatusCode} для {method} {path}: {TrimForError(text)}");
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
        var transport = OpenCodePrompt.ResolveTransport(payload);
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

            parts.Add(new { type = "text", text = OpenCodePrompt.ServerAttachmentInstruction });
            parts.Add(new { type = "file", mime = "text/markdown", filename = Path.GetFileName(payload.SourcePath), url = new Uri(Path.GetFullPath(payload.SourcePath)).AbsoluteUri });
        }

        var body = new Dictionary<string, object>
        {
            ["messageID"] = payload.MessageId,
            ["parts"] = parts
        };
        AddModelSettings(project, body);

        if (!string.IsNullOrWhiteSpace(project.OpenCodeOverrides.Agent))
        {
            body["agent"] = project.OpenCodeOverrides.Agent;
        }

        return body;
    }

    private static void AddModelSettings(ProjectProfile project, Dictionary<string, object> body)
    {
        if (!string.IsNullOrWhiteSpace(project.OpenCodeOverrides.Model))
        {
            body["model"] = string.IsNullOrWhiteSpace(project.OpenCodeOverrides.Provider)
                ? new { modelID = project.OpenCodeOverrides.Model }
                : new { providerID = project.OpenCodeOverrides.Provider, modelID = project.OpenCodeOverrides.Model };
        }
    }

    private async Task<OpenCodeMessageResult> WaitForAssistantResponseAsync(ProjectProfile project, string sessionId, string messageId, CancellationToken cancellationToken)
    {
        var resilience = project.OpenCodeOverrides.Resilience;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, resilience.StepTimeoutMinutes));
        var idleDeadline = TimeSpan.FromMinutes(Math.Max(1, resilience.IdleTimeoutMinutes));
        var lastProgressAt = DateTimeOffset.UtcNow;
        var lastProgressSignature = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCodeSessionStatus status;
            IReadOnlyList<OpenCodeMessage> messages;
            try
            {
                status = await GetSessionStatusAsync(project, sessionId, cancellationToken);
                messages = await GetMessagesAsync(project, sessionId, cancellationToken);
            }
            catch (OpenCodeClientException exception) when (IsTransientPollingError(exception))
            {
                await Task.Delay(1000, cancellationToken);
                continue;
            }

            var progressSignature = BuildProgressSignature(status, messages);
            if (!string.Equals(progressSignature, lastProgressSignature, StringComparison.Ordinal))
            {
                lastProgressSignature = progressSignature;
                lastProgressAt = DateTimeOffset.UtcNow;
            }

            var userMessage = messages.FirstOrDefault(item => string.Equals(item.Id, messageId, StringComparison.Ordinal));
            if (userMessage?.IsFailed == true)
            {
                return new OpenCodeMessageResult(false, messageId, false, userMessage.ErrorMessage ?? "OpenCode сообщил об ошибке message.");
            }

            var assistant = FindAssistantResponse(messages, messageId);
            if (assistant?.IsFailed == true)
            {
                return new OpenCodeMessageResult(false, messageId, false, assistant.ErrorMessage ?? "Assistant response завершился ошибкой.", LastAssistantText: assistant.Text);
            }

            if (status.State != OpenCodeSessionState.Busy && assistant?.IsCompleted == true)
            {
                return new OpenCodeMessageResult(true, messageId, true, LastAssistantText: assistant.Text);
            }

            if (status.State == OpenCodeSessionState.Busy && HasRepeatedAssistantLoop(messages, messageId, out var repeatedText))
            {
                await AbortSessionAsync(project, sessionId, cancellationToken);
                return new OpenCodeMessageResult(
                    false,
                    messageId,
                    true,
                    "NEEDS_MANUAL_INTERVENTION: OpenCode stuck in a repeated assistant response loop; session was aborted to stop token usage.",
                    LastAssistantText: repeatedText,
                    FinishedAt: DateTimeOffset.UtcNow);
            }

            if (status.State != OpenCodeSessionState.Busy && assistant?.PendingToolName == "question")
            {
                var error = "question: OpenCode requested user input through the question tool. Answer it manually in OpenCode UI, then resume the run.";
                return new OpenCodeMessageResult(false, messageId, false, error, LastAssistantText: assistant.Text, FinishedAt: DateTimeOffset.UtcNow);
            }

            if (status.State != OpenCodeSessionState.Busy && !string.IsNullOrWhiteSpace(assistant?.PendingToolName))
            {
                var error = $"permission request: OpenCode tool '{assistant.PendingToolName}' is pending approval. Approve or deny it in OpenCode UI, then resume the run.";
                return new OpenCodeMessageResult(false, messageId, false, error, LastAssistantText: assistant.Text, FinishedAt: DateTimeOffset.UtcNow);
            }

            if (status.State is OpenCodeSessionState.Failed or OpenCodeSessionState.Aborted)
            {
                return new OpenCodeMessageResult(false, messageId, false, status.Message ?? $"OpenCode session перешла в статус {status.State}.");
            }

            if (DateTimeOffset.UtcNow - lastProgressAt >= idleDeadline)
            {
                if (userMessage is not null && assistant is null)
                {
                    return new OpenCodeMessageResult(false, messageId, false, "NEEDS_MANUAL_INTERVENTION: OpenCode accepted the prompt but did not start an assistant response; not retrying to avoid duplicate prompts.", FinishedAt: DateTimeOffset.UtcNow);
                }

                return new OpenCodeMessageResult(false, messageId, false, $"Idle timeout: OpenCode не присылал новых событий или сообщений {resilience.IdleTimeoutMinutes} минут.", IsTimeout: true, FinishedAt: DateTimeOffset.UtcNow);
            }

            await Task.Delay(1000, cancellationToken);
        }

        return new OpenCodeMessageResult(false, messageId, false, "Timeout ожидания завершённого assistant response от OpenCode server.", IsTimeout: true, FinishedAt: DateTimeOffset.UtcNow);
    }

    private static bool IsTransientPollingError(OpenCodeClientException exception)
    {
        var text = string.Join('\n', exception.Message, exception.InnerException?.Message);
        return exception.InnerException is HttpRequestException or IOException or TimeoutException or TaskCanceledException
            || text.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || text.Contains("network error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ETIMEDOUT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("socket hang up", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static OpenCodeMessage? FindAssistantResponse(IReadOnlyList<OpenCodeMessage> messages, string messageId)
    {
        var userIndex = -1;
        for (var index = 0; index < messages.Count; index++)
        {
            if (string.Equals(messages[index].Id, messageId, StringComparison.Ordinal))
            {
                userIndex = index;
                break;
            }
        }

        if (userIndex >= 0)
        {
            return messages.Skip(userIndex + 1).LastOrDefault(item => item.Role == "assistant");
        }

        return messages.LastOrDefault(item => item.Role == "assistant" && string.Equals(item.ParentId, messageId, StringComparison.Ordinal));
    }

    private static bool HasRepeatedAssistantLoop(IReadOnlyList<OpenCodeMessage> messages, string messageId, out string? repeatedText)
    {
        repeatedText = null;
        var userIndex = -1;
        for (var index = 0; index < messages.Count; index++)
        {
            if (string.Equals(messages[index].Id, messageId, StringComparison.Ordinal))
            {
                userIndex = index;
                break;
            }
        }

        if (userIndex < 0)
        {
            return false;
        }

        var consecutive = 0;
        string? previous = null;
        for (var index = messages.Count - 1; index > userIndex; index--)
        {
            var message = messages[index];
            if (message.Role != "assistant" || !message.IsCompleted)
            {
                continue;
            }

            var normalized = NormalizeAssistantLoopText(message.Text);
            if (normalized is null)
            {
                continue;
            }

            if (previous is null)
            {
                previous = normalized;
                repeatedText = message.Text;
                consecutive = 1;
                continue;
            }

            if (!string.Equals(previous, normalized, StringComparison.Ordinal))
            {
                break;
            }

            consecutive++;
            if (consecutive >= RepeatedAssistantLoopThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeAssistantLoopText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 80)
        {
            return null;
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 240 ? collapsed : collapsed[..240];
    }

    private static string BuildProgressSignature(OpenCodeSessionStatus status, IReadOnlyList<OpenCodeMessage> messages)
    {
        var lastMessage = messages.LastOrDefault();
        return string.Join('|', status.State, status.Message, messages.Count, lastMessage?.Id, lastMessage?.IsCompleted, lastMessage?.IsFailed, lastMessage?.ErrorMessage, lastMessage?.PendingToolName);
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
            messages.Add(new OpenCodeMessage(id, role, completed, failed, parentId, error, ReadMessageText(item), ReadPendingToolName(item)));
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

    private static string? ReadMessageText(JsonElement messageElement)
    {
        if (!messageElement.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            var type = ReadString(part, "type");
            if (!string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = ReadString(part, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.AppendLine(text);
            }
        }

        return builder.Length == 0 ? null : builder.ToString().TrimEnd();
    }

    private static string? ReadPendingToolName(JsonElement messageElement)
    {
        if (!messageElement.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var part in parts.EnumerateArray())
        {
            if (!string.Equals(ReadString(part, "type"), "tool", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!part.TryGetProperty("state", out var state))
            {
                continue;
            }

            var tool = ReadString(part, "tool") ?? "unknown";
            var status = ReadString(state, "status");
            if (string.Equals(tool, "question", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)))
            {
                return "question";
            }

            if (!string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return tool;
        }

        return null;
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

    private static string TrimForError(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "пустой ответ";
        }

        return text.Length <= 500 ? text : text[..500];
    }

    private static async Task SaveConnectionStateAsync(ProjectProfile project, string serverUrl, CancellationToken cancellationToken)
    {
        var path = Path.Combine(ProjectPaths.StateDir(project), "opencode-server.json");
        await AtomicFileWriter.WriteAsync(path, async (stream, ct) =>
        {
            await JsonSerializer.SerializeAsync(stream, new { serverUrl, updatedAt = DateTimeOffset.UtcNow }, JsonOptions, ct);
        }, cancellationToken);
    }
}
