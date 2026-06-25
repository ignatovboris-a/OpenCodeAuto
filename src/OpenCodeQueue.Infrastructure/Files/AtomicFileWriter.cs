namespace OpenCodeQueue.Infrastructure.Files;

internal static class AtomicFileWriter
{
    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> writeAsync, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await writeAsync(stream, cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, null);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            FileCleanup.TryDelete(tempPath);
        }
    }
}
