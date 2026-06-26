using Microsoft.Extensions.DependencyInjection;
using OpenCodeQueue.Cli;
using OpenCodeQueue.Cli.Commands;
using OpenCodeQueue.Cli.ConsoleUi;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Infrastructure;

var services = new ServiceCollection()
    .AddOpenCodeQueueInfrastructure()
    .AddSingleton<IConsoleReporter, RussianConsoleReporter>()
    .AddSingleton<ProjectProfilePrompt>()
    .AddSingleton<ProjectConsolePresenter>()
    .AddSingleton<OperationResultPrinter>()
    .AddSingleton<ProjectDiagnosticsValidator>()
    .AddSingleton<CommandDispatcher>()
    .AddSingleton<InteractiveMenu>()
    .AddSingleton<QueueCliApplication>()
    .BuildServiceProvider();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var app = services.GetRequiredService<QueueCliApplication>();
return await app.RunAsync(args, cancellation.Token);
