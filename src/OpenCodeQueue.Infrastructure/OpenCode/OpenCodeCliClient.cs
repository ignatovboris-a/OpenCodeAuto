using System.Text.Json;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Infrastructure.Files;
using OpenCodeQueue.Infrastructure.Json;

namespace OpenCodeQueue.Infrastructure.OpenCode;

public sealed class OpenCodeCliClient(IProcessRunner processRunner) : IOpenCodeClient
{
    public Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(project.ProjectDir))
        {
            throw new OpenCodeClientException($"ProjectDir не найден для OpenCode CLI: {project.ProjectDir}");
        }

        return Task.CompletedTask;
    }

    public async Task<OpenCodeSession> StartSessionAsync(ProjectProfile project, string title, CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(project, cancellationToken);
        var uniqueTitle = title.Contains("run", StringComparison.OrdinalIgnoreCase) ? title : $"{title} run-{Guid.NewGuid():N}";
        var arguments = new List<string> { "session", "create", "--dir", project.ProjectDir };
        arguments.Add("--title");
        arguments.Add(uniqueTitle);
        arguments.Add("--format");
        arguments.Add("json");

        var result = await RunCliAsync(project, arguments, null, "session-create", cancellationToken);
        EnsureExitCode(result, "создать session через OpenCode CLI");

        var sessionId = TryReadSessionId(result.StandardOutput);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = await FindSessionByTitleAsync(project, uniqueTitle, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new OpenCodeClientException("OpenCode CLI не вернул session id, а session list не позволил однозначно найти session по title. Workflow остановлен безопасно; следующая задача не будет запущена.");
        }

        return new OpenCodeSession(sessionId, project.ProjectDir, uniqueTitle);
    }

    public Task<OpenCodeSessionDetails> GetSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
    {
        var session = new OpenCodeSession(sessionId, project.ProjectDir);
        var status = new OpenCodeSessionStatus(OpenCodeSessionState.Unknown, "CLI fallback не предоставляет достоверную детализацию session через этот adapter.");
        return Task.FromResult(new OpenCodeSessionDetails(session, status, []));
    }

    public async Task<OpenCodeMessageResult> SendPromptAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new OpenCodeClientException("CLI fallback не продолжает workflow без известного sessionId. Требуется ручное вмешательство.");
        }

        await EnsureReadyAsync(project, cancellationToken);
        var arguments = BaseRunArguments(project.ProjectDir);
        arguments.Add("--session");
        arguments.Add(sessionId);
        arguments.Add("--format");
        arguments.Add("json");
        AddPromptArguments(arguments, payload);

        var result = await RunCliAsync(project, arguments, payload.RunId, payload.StepId ?? payload.MessageId, cancellationToken);
        if (result.ExitCode != 0)
        {
            return new OpenCodeMessageResult(false, payload.MessageId, false, $"OpenCode CLI завершился с кодом {result.ExitCode}.");
        }

        var messageId = TryReadMessageId(result.StandardOutput) ?? payload.MessageId;
        return new OpenCodeMessageResult(true, messageId, true);
    }

    public Task<OpenCodeSessionStatus> GetSessionStatusAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new OpenCodeSessionStatus(OpenCodeSessionState.Unknown, "CLI fallback не может доказуемо определить status session."));
    }

    public async Task<StepRecoveryResult> TryRecoverStepAsync(ProjectProfile project, RunManifest manifest, WorkflowStep step, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifest.SessionId))
        {
            return new StepRecoveryResult(StepRecoveryOutcome.Failed, "CLI fallback не продолжает recovery без сохранённого sessionId. Run требует ручного вмешательства.");
        }

        var recoveryId = string.IsNullOrWhiteSpace(step.SessionMessageId) ? $"recover-{Guid.NewGuid():N}" : $"{step.SessionMessageId}-recover";
        var recoveryPayload = OpenCodePrompt.RecoveryPayload(recoveryId, step.SourcePath, manifest.RunId, step.Id.Value);
        var result = await SendPromptAsync(project, manifest.SessionId, recoveryPayload, cancellationToken);
        return result.IsSuccess
            ? new StepRecoveryResult(StepRecoveryOutcome.ConservativeContinueSent, "CLI fallback отправил ConservativeContinue recovery prompt в конкретную session.", result.MessageId ?? recoveryId)
            : new StepRecoveryResult(StepRecoveryOutcome.Failed, result.ErrorMessage ?? "CLI fallback recovery prompt завершился ошибкой.");
    }

    public Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task<string?> FindSessionByTitleAsync(ProjectProfile project, string title, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "session", "list", "--dir", project.ProjectDir, "--format", "json" };
        var result = await RunCliAsync(project, arguments, null, null, cancellationToken);
        EnsureExitCode(result, "получить список session OpenCode CLI");
        var matches = ReadSessions(result.StandardOutput).Where(item => string.Equals(item.Title, title, StringComparison.Ordinal)).ToList();
        return matches.Count == 1 ? matches[0].Id : null;
    }

    private async Task<ProcessRunResult> RunCliAsync(ProjectProfile project, IReadOnlyList<string> arguments, string? runId, string? stepId, CancellationToken cancellationToken)
    {
        var stdoutPath = TryGetLogPath(project, runId, stepId, "stdout");
        var stderrPath = TryGetLogPath(project, runId, stepId, "stderr");
        await EnsureLogFileAsync(stdoutPath, cancellationToken);
        await EnsureLogFileAsync(stderrPath, cancellationToken);

        return await processRunner.RunAsync(new ProcessRunRequest
        {
            Executable = project.OpenCodeOverrides.OpenCodeExecutable,
            WorkingDirectory = project.ProjectDir,
            Arguments = arguments,
            Timeout = TimeSpan.FromHours(6),
            OnStdoutLine = stdoutPath is null ? null : (line, token) => AppendLogLineAsync(stdoutPath, line, token),
            OnStderrLine = stderrPath is null ? null : (line, token) => AppendLogLineAsync(stderrPath, line, token)
        }, cancellationToken);
    }

    private static List<string> BaseRunArguments(string projectDir) => ["run", "--dir", projectDir];

    private static void AddPromptArguments(List<string> arguments, PromptPayload payload)
    {
        if (payload.Transport != PromptTransport.Inline)
        {
            if (!File.Exists(payload.SourcePath))
            {
                throw new OpenCodeClientException($"Prompt-файл для attachment не найден: {payload.SourcePath}");
            }

            arguments.Add("--file");
            arguments.Add(payload.SourcePath);
            arguments.Add(OpenCodePrompt.CliAttachmentInstruction);
            return;
        }

        arguments.Add(payload.Content);
    }

    private static void EnsureExitCode(ProcessRunResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new OpenCodeClientException($"Не удалось {operation}: OpenCode CLI завершился с кодом {result.ExitCode}.");
        }
    }

    private static string? TryReadSessionId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractJson(text));
            var explicitId = JsonElementReader.FindString(document.RootElement, "sessionId", "sessionID", "session_id", "id");
            if (!string.IsNullOrWhiteSpace(explicitId))
            {
                return explicitId;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("session", out var sessionElement))
            {
                return sessionElement.ValueKind == JsonValueKind.String
                    ? sessionElement.GetString()
                    : JsonElementReader.ReadString(sessionElement, "id");
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? TryReadMessageId(string json) => TryReadStringFromJson(json, "messageId", "messageID", "message_id", "id");

    private static string? TryReadStringFromJson(string text, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractJson(text));
            return JsonElementReader.FindString(document.RootElement, names);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<(string Id, string? Title)> ReadSessions(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJson(json));
            var array = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement
                : document.RootElement.TryGetProperty("sessions", out var sessions) ? sessions : default;
            if (array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<(string Id, string? Title)>();
            foreach (var item in array.EnumerateArray())
            {
                var id = JsonElementReader.FindString(item, "id", "sessionId", "sessionID", "session_id");
                var title = JsonElementReader.FindString(item, "title");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result.Add((id, title));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task EnsureLogFileAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, string.Empty, cancellationToken);
        }
    }

    private static Task AppendLogLineAsync(string path, string line, CancellationToken cancellationToken)
    {
        return File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
    }

    private static string? TryGetLogPath(ProjectProfile project, string? runId, string? stepId, string stream)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            return null;
        }

        return Path.Combine(ProjectPaths.RunDir(project, runId), "logs", $"{FileNameSanitizer.Sanitize(stepId)}.{stream}.log");
    }

    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return trimmed;
        }

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var candidate = line.Trim();
            if (candidate.StartsWith('{') || candidate.StartsWith('['))
            {
                return candidate;
            }
        }

        return trimmed;
    }
}
