using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Infrastructure.Files;

public sealed class RunWorkspace : IRunWorkspace
{
    public async Task<string> SnapshotPromptAsync(ProjectProfile project, string runId, PromptDescriptor prompt, string snapshotFileName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshotsDir = Path.Combine(ProjectPaths.RunDir(project, runId), "snapshots");
        Directory.CreateDirectory(snapshotsDir);
        var destination = Path.Combine(snapshotsDir, FileNameSanitizer.Sanitize(snapshotFileName));
        await using var source = new FileStream(prompt.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
        return destination;
    }

    public async Task<string> WriteAttemptMessageAsync(ProjectProfile project, string runId, string messageId, string content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attemptsDir = Path.Combine(ProjectPaths.RunDir(project, runId), "attempts");
        Directory.CreateDirectory(attemptsDir);
        var destination = Path.Combine(attemptsDir, FileNameSanitizer.Sanitize(messageId) + ".message.md");
        await File.WriteAllTextAsync(destination, content, cancellationToken);
        return destination;
    }
}
