using System.Text.Json;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;

namespace OpenCodeQueue.Infrastructure.State;

public sealed class JsonStateStore : IStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<RunManifest?> LoadActiveRunAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var path = ProjectPaths.StateFile(project);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RunManifest>(stream, JsonOptions, cancellationToken);
    }

    public async Task SaveActiveRunAsync(ProjectProfile project, RunManifest manifest, CancellationToken cancellationToken)
    {
        var path = ProjectPaths.StateFile(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public async Task AppendEventAsync(ProjectProfile project, string eventJson, CancellationToken cancellationToken)
    {
        var stateDir = ProjectPaths.StateDir(project);
        Directory.CreateDirectory(stateDir);
        await File.AppendAllTextAsync(ProjectPaths.EventsFile(project), eventJson + Environment.NewLine, cancellationToken);
    }
}
