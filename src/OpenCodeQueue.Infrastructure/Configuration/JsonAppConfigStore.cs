using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Infrastructure.Files;
using OpenCodeQueue.Infrastructure.Json;

namespace OpenCodeQueue.Infrastructure.Configuration;

/// <summary>
/// Relative projectDir values are resolved from the config file directory; queue folders are fixed under .opencodequeue.
/// </summary>
public sealed class JsonAppConfigStore(IClock? clock = null) : IAppConfigStore
{
    private readonly IClock clock = clock ?? new SystemClock();

    public async Task<AppConfig?> LoadAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(configPath);
        var json = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var config = JsonSerializer.Deserialize<AppConfig>(json, QueueJson.Options);
        if (config is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return Normalize(config, fullPath, document.RootElement);
    }

    public async Task<AppConfig> LoadOrCreateDefaultAsync(string configPath, CancellationToken cancellationToken)
    {
        return await LoadAsync(configPath, cancellationToken) ?? new AppConfig();
    }

    public async Task SaveAsync(string configPath, AppConfig config, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(configPath);
        await using var configLock = AcquireConfigLock(fullPath, clock.Now);
        var existingJson = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, cancellationToken) : null;
        var output = string.IsNullOrWhiteSpace(existingJson)
            ? JsonSerializer.Serialize(config, QueueJson.Options)
            : BuildPreservingJson(existingJson, config, fullPath);
        await AtomicFileWriter.WriteAsync(
            fullPath,
            async (stream, token) =>
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync(output.AsMemory(), token);
                await writer.FlushAsync(token);
            },
            cancellationToken);
    }

    private static IAsyncDisposable AcquireConfigLock(string configPath, DateTimeOffset createdAt)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lockPath = configPath + ".lock";
        var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write($"pid={Environment.ProcessId}; machine={Environment.MachineName}; createdAt={createdAt:u}");
        writer.Flush();
        stream.Flush(flushToDisk: true);
        return new ConfigLock(stream, lockPath);
    }

    private static string BuildPreservingJson(string existingJson, AppConfig config, string configPath)
    {
        try
        {
            using var document = JsonDocument.Parse(existingJson);
            var existingConfig = JsonSerializer.Deserialize<AppConfig>(existingJson, QueueJson.Options);
            if (existingConfig is null)
            {
                return JsonSerializer.Serialize(config, QueueJson.Options);
            }

            var root = JsonNode.Parse(existingJson)?.AsObject() ?? [];
            root["schemaVersion"] = config.SchemaVersion;
            root["activeProjectId"] = JsonValue.Create(config.ActiveProjectId.HasValue ? config.ActiveProjectId.Value.Value : null);
            root["defaults"] = JsonSerializer.SerializeToNode(config.Defaults, QueueJson.Options);

            var projects = new JsonArray();
            foreach (var project in config.Projects)
            {
                projects.Add(JsonSerializer.SerializeToNode(project, QueueJson.Options));
            }

            root["projects"] = projects;
            return root.ToJsonString(QueueJson.Options);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(config, QueueJson.Options);
        }
    }

    private sealed class ConfigLock(FileStream stream, string lockPath) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            FileCleanup.TryDelete(lockPath);
        }
    }

    private static AppConfig Normalize(AppConfig config, string configPath, JsonElement? root = null)
    {
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Directory.GetCurrentDirectory();
        var applyDefaults = root is not null;
        var projectElements = applyDefaults ? GetProjectElements(root) : [];
        var projects = config.Projects
            .Select((project, index) => Normalize(project, configDir, config.Defaults, projectElements.ElementAtOrDefault(index), applyDefaults))
            .ToArray();
        return config with { Projects = projects };
    }

    private static ProjectProfile Normalize(ProjectProfile project, string configDir, OpenCodeSettings defaults, JsonElement? projectElement, bool applyDefaults)
    {
        var projectDir = PathResolver.Resolve(project.ProjectDir, configDir);
        var openCodeOverrides = applyDefaults
            ? MergeOpenCodeSettings(defaults, project.OpenCodeOverrides, GetOpenCodeOverridesElement(projectElement))
            : project.OpenCodeOverrides;

        return project with
        {
            Id = new ProjectId(project.Id.Value.Trim()),
            ProjectDir = projectDir,
            PromptsDir = string.Empty,
            QualityDir = null,
            ReviewsDir = null,
            StateDir = string.Empty,
            CompletedDir = string.Empty,
            OpenCodeOverrides = openCodeOverrides
        };
    }

    private static IReadOnlyList<JsonElement> GetProjectElements(JsonElement? root)
    {
        if (root is null || root.Value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (!root.Value.TryGetProperty("projects", out var projectsElement) || projectsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return projectsElement.EnumerateArray().ToArray();
    }

    private static JsonElement? GetOpenCodeOverridesElement(JsonElement? projectElement)
    {
        if (projectElement is null || projectElement.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return projectElement.Value.TryGetProperty("openCodeOverrides", out var overrides) && overrides.ValueKind == JsonValueKind.Object
            ? overrides
            : null;
    }

    private static OpenCodeSettings MergeOpenCodeSettings(OpenCodeSettings defaults, OpenCodeSettings overrides, JsonElement? overridesElement)
    {
        if (overridesElement is null)
        {
            return defaults;
        }

        return new OpenCodeSettings
        {
            ServerUrl = Has(overridesElement, "serverUrl") ? overrides.ServerUrl : defaults.ServerUrl,
            ServerPassword = Has(overridesElement, "serverPassword") ? overrides.ServerPassword : defaults.ServerPassword,
            ServerUsername = Has(overridesElement, "serverUsername") ? overrides.ServerUsername : defaults.ServerUsername,
            Provider = Has(overridesElement, "provider") ? overrides.Provider : defaults.Provider,
            Model = Has(overridesElement, "model") ? overrides.Model : defaults.Model,
            ReasoningEffort = Has(overridesElement, "reasoningEffort") ? overrides.ReasoningEffort : defaults.ReasoningEffort,
            Agent = Has(overridesElement, "agent") ? overrides.Agent : defaults.Agent,
            PromptTransport = Has(overridesElement, "promptTransport") ? overrides.PromptTransport : defaults.PromptTransport,
            MaxInlinePromptChars = Has(overridesElement, "maxInlinePromptChars") ? overrides.MaxInlinePromptChars : defaults.MaxInlinePromptChars,
            ConsoleVerbosity = Has(overridesElement, "consoleVerbosity") ? overrides.ConsoleVerbosity : defaults.ConsoleVerbosity,
            Resilience = Has(overridesElement, "resilience") ? overrides.Resilience : defaults.Resilience
        };
    }

    private static bool Has(JsonElement? element, string propertyName)
    {
        return element is not null && element.Value.TryGetProperty(propertyName, out _);
    }
}
