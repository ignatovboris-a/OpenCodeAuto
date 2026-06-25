using System.Text.Json;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Infrastructure.Files;
using OpenCodeQueue.Infrastructure.Json;

namespace OpenCodeQueue.Infrastructure.State;

public sealed class JsonStateStore : IStateStore
{
    public async Task<QueueState?> LoadQueueStateAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var path = ProjectPaths.StateFile(project);
        if (!File.Exists(path))
        {
            return null;
        }

        var state = await ReadJsonAsync<QueueState>(path, cancellationToken);
        ValidateProjectSnapshot(project, state.ProjectId, state.ProjectDirSnapshot, "state.json");
        return state;
    }

    public async Task SaveQueueStateAsync(ProjectProfile project, QueueState state, CancellationToken cancellationToken)
    {
        ValidateProjectSnapshot(project, state.ProjectId, state.ProjectDirSnapshot, "state.json");
        await WriteJsonAsync(ProjectPaths.StateFile(project), state, cancellationToken);
    }

    public async Task<RunManifest?> LoadRunManifestAsync(ProjectProfile project, string runId, CancellationToken cancellationToken)
    {
        ValidateRunId(runId);
        var path = ProjectPaths.RunManifestFile(project, runId);
        if (!File.Exists(path))
        {
            return null;
        }

        var manifest = await ReadJsonAsync<RunManifest>(path, cancellationToken);
        ValidateProjectSnapshot(project, manifest.ProjectId, manifest.ProjectDirSnapshot, "manifest.json");
        return manifest;
    }

    public async Task SaveRunManifestAsync(ProjectProfile project, RunManifest manifest, CancellationToken cancellationToken)
    {
        ValidateRunId(manifest.RunId);
        ValidateProjectSnapshot(project, manifest.ProjectId, manifest.ProjectDirSnapshot, "manifest.json");
        await WriteJsonAsync(ProjectPaths.RunManifestFile(project, manifest.RunId), manifest, cancellationToken);
    }

    public async Task AppendEventAsync(ProjectProfile project, QueueEvent queueEvent, CancellationToken cancellationToken)
    {
        var stateDir = ProjectPaths.StateDir(project);
        Directory.CreateDirectory(stateDir);
        var eventJson = JsonSerializer.Serialize(queueEvent, QueueJson.Options);
        await using var stream = new FileStream(ProjectPaths.EventsFile(project), FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync(eventJson.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, QueueJson.Options, cancellationToken)
                ?? throw new InvalidOperationException($"Файл состояния пуст или повреждён: {path}");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Файл состояния повреждён: {path}. Нужна ручная проверка перед продолжением.", exception);
        }
    }

    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        return AtomicFileWriter.WriteAsync(
            path,
            (stream, token) => JsonSerializer.SerializeAsync(stream, value, QueueJson.Options, token),
            cancellationToken);
    }

    private static void ValidateProjectSnapshot(ProjectProfile project, ProjectId projectId, string? projectDirSnapshot, string fileName)
    {
        if (!string.Equals(project.Id.Value, projectId.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Предупреждение: {fileName} относится к проекту '{projectId}', а выбран проект '{project.Id}'. Автоматическое продолжение остановлено.");
        }

        if (string.IsNullOrWhiteSpace(projectDirSnapshot))
        {
            throw new InvalidOperationException($"Предупреждение: {fileName} не содержит projectDir snapshot. Автоматическое продолжение остановлено.");
        }

        if (!PathResolver.AreSamePath(project.ProjectDir, projectDirSnapshot))
        {
            throw new InvalidOperationException($"Предупреждение: {fileName} содержит projectDir '{projectDirSnapshot}', а выбран '{project.ProjectDir}'. Автоматическое продолжение остановлено.");
        }
    }

    private static void ValidateRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)
            || runId is "." or ".."
            || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || runId.Contains(Path.DirectorySeparatorChar)
            || runId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("runId должен быть безопасным именем папки без разделителей пути.");
        }
    }
}
