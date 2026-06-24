using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Cli.ConsoleUi;

public sealed class RussianConsoleReporter : IConsoleReporter
{
    public void Info(string message) => Console.WriteLine(message);

    public void Warning(string message) => Console.WriteLine("Предупреждение: " + message);

    public void Error(string message) => Console.Error.WriteLine("Ошибка: " + message);

    public string? ReadLine(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine();
    }
}
