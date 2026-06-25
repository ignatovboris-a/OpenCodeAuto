using OpenCodeQueue.Core.Configuration;

namespace OpenCodeQueue.Core.Ports;

public interface IRunLock
{
    Task<RunLockAcquireResult> TryAcquireAsync(ProjectProfile project, CancellationToken cancellationToken);

    Task<RunLockInfo?> ReadAsync(ProjectProfile project, CancellationToken cancellationToken);

    Task ForceUnlockAsync(ProjectProfile project, CancellationToken cancellationToken);
}

public sealed record RunLockInfo
{
    public int? Pid { get; init; }

    public string? MachineName { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public ProjectId ProjectId { get; init; }

    public bool IsCurrentProcess { get; init; }

    public bool IsStale { get; init; }
}

public sealed record RunLockAcquireResult(IAsyncDisposable? Releaser, RunLockInfo? ExistingLock, string? Message)
{
    public bool Acquired => Releaser is not null;
}
