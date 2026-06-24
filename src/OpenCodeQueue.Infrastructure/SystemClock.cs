using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}
