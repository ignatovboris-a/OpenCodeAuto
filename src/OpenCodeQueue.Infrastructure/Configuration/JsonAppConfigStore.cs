using System.Text.Json;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Infrastructure.Configuration;

public sealed class JsonAppConfigStore : IAppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<AppConfig?> LoadAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(configPath);
        return await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions, cancellationToken);
    }

    public async Task SaveAsync(string configPath, AppConfig config, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(configPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = fullPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, fullPath, overwrite: true);
    }
}
