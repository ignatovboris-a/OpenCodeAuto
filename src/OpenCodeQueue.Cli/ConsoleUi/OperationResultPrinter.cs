using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Workflow;

namespace OpenCodeQueue.Cli.ConsoleUi;

public sealed class OperationResultPrinter(IConsoleReporter reporter, ProjectConsolePresenter projectPresenter)
{
    public int Print(QueueOperationResult result)
    {
        var verbosity = result.Project?.OpenCodeOverrides.ConsoleVerbosity ?? ConsoleVerbosity.Normal;
        foreach (var message in result.Messages)
        {
            if (verbosity == ConsoleVerbosity.Quiet && result.IsSuccess && !IsMajorMessage(message))
            {
                continue;
            }

            if (result.IsSuccess)
            {
                reporter.Info(message);
            }
            else
            {
                reporter.Warning(message);
            }
        }

        if (result.Project is not null && result.Manifest is not null && (verbosity != ConsoleVerbosity.Quiet || !result.IsSuccess))
        {
            projectPresenter.PrintManifest(result.Project, result.Manifest);
        }

        return result.ExitCode;
    }

    private static bool IsMajorMessage(string message)
    {
        return message.Contains("ошиб", StringComparison.OrdinalIgnoreCase)
            || message.Contains("заверш", StringComparison.OrdinalIgnoreCase)
            || message.Contains("пуст", StringComparison.OrdinalIgnoreCase)
            || message.Contains("нет активного", StringComparison.OrdinalIgnoreCase);
    }
}
