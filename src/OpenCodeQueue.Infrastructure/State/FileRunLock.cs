using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Infrastructure.State;

public sealed class FileRunLock : IRunLock
{
    public Task<IAsyncDisposable?> TryAcquireAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stateDir = ProjectPaths.StateDir(project);
        Directory.CreateDirectory(stateDir);
        var lockPath = ProjectPaths.RunLockFile(project);

        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.WriteLine(Environment.ProcessId);
            writer.Flush();
            stream.Position = 0;
            IAsyncDisposable releaser = new FileLockReleaser(stream, lockPath);
            return Task.FromResult<IAsyncDisposable?>(releaser);
        }
        catch (IOException)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }

    private sealed class FileLockReleaser(FileStream stream, string lockPath) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            TryDeleteLockFile(lockPath);
        }

        private static void TryDeleteLockFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
