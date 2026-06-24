using OpenCodeQueue.Cli.Commands;

namespace OpenCodeQueue.Cli;

public sealed class QueueCliApplication(CommandDispatcher dispatcher)
{
    public Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var command = CliCommand.Parse(args);
        return dispatcher.DispatchAsync(command, cancellationToken);
    }
}
