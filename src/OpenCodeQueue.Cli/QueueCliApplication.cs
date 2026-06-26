using OpenCodeQueue.Cli.Commands;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Workflow;

namespace OpenCodeQueue.Cli;

public sealed class QueueCliApplication(CommandDispatcher dispatcher, IConsoleReporter reporter)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            var command = CliCommand.Parse(args);
            return await dispatcher.DispatchAsync(command, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            reporter.Warning("Операция отменена.");
            return QueueExitCodes.Cancelled;
        }
        catch (Exception exception)
        {
            reporter.Error("Непредвиденная ошибка: " + exception.Message);
            return QueueExitCodes.UnexpectedError;
        }
    }
}
