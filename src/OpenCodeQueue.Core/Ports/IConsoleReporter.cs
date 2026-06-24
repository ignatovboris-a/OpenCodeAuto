namespace OpenCodeQueue.Core.Ports;

public interface IConsoleReporter
{
    void Info(string message);

    void Warning(string message);

    void Error(string message);

    string? ReadLine(string prompt);
}
