using System.Text.RegularExpressions;
using OpenCodeQueue.Core.Configuration;

namespace OpenCodeQueue.Core.OpenCode;

public interface IOpenCodeRunClassifier
{
    StepClassification Classify(OpenCodeMessageResult result, ResilienceSettings resilience);
}

public sealed class OpenCodeStepResultClassifier : IOpenCodeRunClassifier
{
    private static readonly string[] RecoverableMarkers =
    [
        "terminated",
        "Tool execution aborted",
        "tool execution aborted",
        "aborted",
        "cancelled",
        "canceled",
        "connection reset",
        "timeout",
        "network error",
        "ECONNRESET",
        "ETIMEDOUT",
        "socket hang up"
    ];

    private static readonly string[] FatalCliMarkers =
    [
        "unknown argument",
        "unknown option",
        "unrecognized option",
        "not recognized as",
        "No such file or directory",
        "executable not found",
        "command not found"
    ];

    public StepClassification Classify(OpenCodeMessageResult result, ResilienceSettings resilience)
    {
        var text = string.Join('\n', result.Stdout, result.Stderr, result.LastAssistantText, result.ErrorMessage);
        if (ContainsNeedsManualIntervention(text))
        {
            return new StepClassification(OpenCodeStepOutcomeKind.NeedsManualIntervention, "needs-manual", ExtractNeedsManualReason(text));
        }

        if (result.IsSuccess)
        {
            return new StepClassification(OpenCodeStepOutcomeKind.Completed, null, "OpenCode завершил шаг успешно.");
        }

        if (!resilience.Enabled)
        {
            return new StepClassification(OpenCodeStepOutcomeKind.FatalFailure, NormalizeSignature(text, result.ExitCode), result.ErrorMessage);
        }

        if (result.IsTransportError || result.IsTimeout)
        {
            var signature = result.IsTimeout ? "timeout" : NormalizeSignature(text, result.ExitCode);
            return new StepClassification(OpenCodeStepOutcomeKind.RecoverableInterruption, signature, result.ErrorMessage ?? "Transport/timeout interruption.");
        }

        if (ContainsFatalCliMarker(text))
        {
            return new StepClassification(OpenCodeStepOutcomeKind.FatalFailure, NormalizeSignature(text, result.ExitCode), result.ErrorMessage);
        }

        if (ContainsRecoverableMarker(text, resilience))
        {
            return new StepClassification(OpenCodeStepOutcomeKind.RecoverableInterruption, NormalizeSignature(text, result.ExitCode), result.ErrorMessage ?? "OpenCode execution interrupted.");
        }

        return new StepClassification(OpenCodeStepOutcomeKind.FatalFailure, NormalizeSignature(text, result.ExitCode), result.ErrorMessage);
    }

    private static bool ContainsRecoverableMarker(string text, ResilienceSettings resilience)
    {
        foreach (var marker in RecoverableMarkers)
        {
            if (!resilience.DetectTerminatedText && marker.Equals("terminated", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!resilience.RecoverOnToolExecutionAborted && marker.Contains("Tool execution aborted", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsFatalCliMarker(string text)
    {
        return FatalCliMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsNeedsManualIntervention(string text)
    {
        return text.Contains("NEEDS_MANUAL_INTERVENTION:", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractNeedsManualReason(string text)
    {
        var match = Regex.Match(text, "NEEDS_MANUAL_INTERVENTION:\\s*(.+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "OpenCode запросил ручное вмешательство.";
    }

    private static string NormalizeSignature(string text, int? exitCode)
    {
        var marker = RecoverableMarkers.FirstOrDefault(item => text.Contains(item, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(marker))
        {
            return marker.ToLowerInvariant();
        }

        var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return exitCode.HasValue ? $"exit-code-{exitCode.Value}" : "unknown-failure";
        }

        return firstLine.Length <= 120 ? firstLine : firstLine[..120];
    }
}
