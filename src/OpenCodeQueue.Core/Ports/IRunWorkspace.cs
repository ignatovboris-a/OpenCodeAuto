using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Core.Ports;

public interface IRunWorkspace
{
    Task<string> SnapshotPromptAsync(ProjectProfile project, string runId, PromptDescriptor prompt, string snapshotFileName, CancellationToken cancellationToken);

    Task<string> WriteAttemptMessageAsync(ProjectProfile project, string runId, string messageId, string content, CancellationToken cancellationToken);
}
