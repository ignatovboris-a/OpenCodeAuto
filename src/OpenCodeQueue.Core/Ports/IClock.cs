namespace OpenCodeQueue.Core.Ports;

public interface IClock
{
    DateTimeOffset Now { get; }
}
