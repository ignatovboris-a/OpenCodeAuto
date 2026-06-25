using System.Diagnostics;

namespace OpenCodeQueue.Infrastructure.OpenCode;

public sealed record ProcessRunRequest
{
    public required string Executable { get; init; }

    public required string WorkingDirectory { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    public TimeSpan? Timeout { get; init; }

    public Func<string, CancellationToken, Task>? OnStdoutLine { get; init; }

    public Func<string, CancellationToken, Task>? OnStderrLine { get; init; }
}

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken);
}

public sealed class DefaultProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var item in request.EnvironmentVariables)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        using var timeoutCts = request.Timeout is null ? null : new CancellationTokenSource(request.Timeout.Value);
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Не удалось запустить процесс: {request.Executable}");

        var stdout = ReadLinesAsync(process.StandardOutput, request.OnStdoutLine, linkedCts.Token);
        var stderr = ReadLinesAsync(process.StandardError, request.OnStderrLine, linkedCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts?.IsCancellationRequested == true)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new TimeoutException($"Процесс OpenCode CLI превысил timeout: {request.Timeout}.");
        }

        return new ProcessRunResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, Func<string, CancellationToken, Task>? onLine, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            builder.AppendLine(line);
            if (onLine is not null)
            {
                await onLine(line, cancellationToken);
            }
        }

        return builder.ToString();
    }
}
