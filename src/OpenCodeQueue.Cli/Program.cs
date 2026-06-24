using Microsoft.Extensions.DependencyInjection;
using OpenCodeQueue.Cli;
using OpenCodeQueue.Cli.Commands;
using OpenCodeQueue.Cli.ConsoleUi;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Infrastructure;

var services = new ServiceCollection()
    .AddOpenCodeQueueInfrastructure()
    .AddSingleton<IConsoleReporter, RussianConsoleReporter>()
    .AddSingleton<CommandDispatcher>()
    .AddSingleton<InteractiveMenu>()
    .AddSingleton<QueueCliApplication>()
    .BuildServiceProvider();

var app = services.GetRequiredService<QueueCliApplication>();
return await app.RunAsync(args, CancellationToken.None);
