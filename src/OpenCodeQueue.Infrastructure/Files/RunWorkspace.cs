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
        var destination = Path.Combine(snapshotsDir, SanitizeFileName(snapshotFileName));
        await using var source = new FileStream(prompt.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
        return destination;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
    }
}
