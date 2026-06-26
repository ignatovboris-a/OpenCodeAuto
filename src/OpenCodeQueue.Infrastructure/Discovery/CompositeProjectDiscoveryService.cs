using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Discovery;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Infrastructure.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenCodeQueue.Infrastructure.Discovery;

public sealed class CompositeProjectDiscoveryService(IAppConfigStore configStore) : IProjectDiscoveryService
{
    public Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DiscoveredProject>>([]);
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

        foreach (var settings in CandidateServerSettings(config).DistinctBy(settings => settings.ServerUrl, StringComparer.OrdinalIgnoreCase))
        {
            await AddServerCandidatesAsync(result, settings, cancellationToken);
        }

        return result;
    }

    private static IEnumerable<OpenCodeSettings> CandidateServerSettings(AppConfig config)
    {
        yield return config.Defaults;
        foreach (var settings in config.Projects.Select(project => project.OpenCodeOverrides))
        {
            yield return settings;
        }
    }

    private static async Task AddServerCandidatesAsync(List<DiscoveredProject> result, OpenCodeSettings settings, CancellationToken cancellationToken)
    {
        var serverUrl = settings.ServerUrl;
        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseUri))
        {
            return;
        }

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            foreach (var endpoint in new[] { "project", "projects" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, endpoint));
                AddAuthorization(request, settings);
                using var response = await httpClient.SendAsync(request, cancellationToken);
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
        rawProjectDir ??= ReadString(element, "worktree");
        if (string.IsNullOrWhiteSpace(rawProjectDir) && AddWrappedProjectCandidates(result, element, serverUrl))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rawProjectDir) || rawProjectDir == "/" || !Path.IsPathFullyQualified(rawProjectDir))
        {
            return;
        }

        var projectDir = Path.GetFullPath(rawProjectDir);
        var displayName = ReadString(element, "name")
            ?? ReadString(element, "displayName")
            ?? (projectDir is null ? null : Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            ?? projectDir
            ?? serverUrl;
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
        return JsonElementReader.ReadString(element, propertyName);
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void AddAuthorization(HttpRequestMessage request, OpenCodeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ServerPassword))
        {
            return;
        }

        var raw = $"{settings.ServerUsername}:{settings.ServerPassword}";
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
    }
}
