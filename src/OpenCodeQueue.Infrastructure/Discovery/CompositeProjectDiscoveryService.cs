using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Discovery;
using OpenCodeQueue.Core.Ports;
using System.Text.Json;

namespace OpenCodeQueue.Infrastructure.Discovery;

public sealed class CompositeProjectDiscoveryService(IAppConfigStore configStore) : IProjectDiscoveryService
{
    public Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<DiscoveredProject>();
        AddStorageCandidates(result);
        return Task.FromResult<IReadOnlyList<DiscoveredProject>>(result);
    }

    public async Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(string configPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<DiscoveredProject>();
        var config = await configStore.LoadAsync(configPath, cancellationToken) ?? new AppConfig();
        foreach (var project in config.Projects)
        {
            result.Add(new DiscoveredProject(
                "OpenCodeQueue registry",
                project.DisplayName ?? project.Id.Value,
                project.ProjectDir,
                project.Id.Value,
                DiscoveryConfidence.High,
                [],
                Directory.Exists(project.ProjectDir)));
        }

        await AddServerCandidatesAsync(result, config.Defaults.ServerUrl, cancellationToken);
        foreach (var serverUrl in config.Projects.Select(project => project.OpenCodeOverrides.ServerUrl).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await AddServerCandidatesAsync(result, serverUrl, cancellationToken);
        }

        AddStorageCandidates(result);
        return result;
    }

    private static async Task AddServerCandidatesAsync(List<DiscoveredProject> result, string? serverUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri))
        {
            return;
        }

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            foreach (var endpoint in new[] { "project", "projects" })
            {
                using var response = await httpClient.GetAsync(new Uri(baseUri, endpoint), cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                AddJsonProjectCandidates(result, document.RootElement, serverUrl);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // OpenCode Server API is optional; unavailable or incompatible servers must not break first start.
        }
    }

    private static void AddJsonProjectCandidates(List<DiscoveredProject> result, JsonElement element, string serverUrl)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AddJsonProjectCandidates(result, item, serverUrl);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var rawProjectDir = ReadString(element, "projectDir") ?? ReadString(element, "path") ?? ReadString(element, "cwd") ?? ReadString(element, "root");
        if (string.IsNullOrWhiteSpace(rawProjectDir) && AddWrappedProjectCandidates(result, element, serverUrl))
        {
            return;
        }

        var projectDir = string.IsNullOrWhiteSpace(rawProjectDir) || !Path.IsPathFullyQualified(rawProjectDir)
            ? null
            : Path.GetFullPath(rawProjectDir);
        var displayName = ReadString(element, "name") ?? ReadString(element, "displayName") ?? projectDir ?? serverUrl;
        var warnings = new List<string>();
        var canSelect = !string.IsNullOrWhiteSpace(projectDir) && Directory.Exists(projectDir);
        if (!canSelect)
        {
            warnings.Add("OpenCode server доступен, но абсолютный существующий projectDir не получен. Укажите путь вручную.");
        }

        result.Add(new DiscoveredProject(
            "OpenCode Server API",
            displayName,
            projectDir,
            Slug(displayName),
            canSelect ? DiscoveryConfidence.High : DiscoveryConfidence.Medium,
            warnings,
            canSelect));
    }

    private static bool AddWrappedProjectCandidates(List<DiscoveredProject> result, JsonElement element, string serverUrl)
    {
        var added = false;
        foreach (var propertyName in new[] { "projects", "project", "current", "data" })
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            var before = result.Count;
            AddJsonProjectCandidates(result, property, serverUrl);
            added |= result.Count > before;
        }

        return added;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void AddStorageCandidates(List<DiscoveredProject> result)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return;
        }

        var candidates = new[]
        {
            Path.Combine(home, ".local", "share", "opencode"),
            Path.Combine(home, ".config", "opencode"),
            Path.Combine(home, "AppData", "Roaming", "opencode")
        };

        foreach (var storage in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(storage))
            {
                continue;
            }

            result.Add(new DiscoveredProject(
                "OpenCode local storage",
                storage,
                null,
                null,
                DiscoveryConfidence.Low,
                ["Найден storage OpenCode, но абсолютный projectDir не восстановлен надёжно. Укажите путь вручную."],
                false));
        }
    }
}
