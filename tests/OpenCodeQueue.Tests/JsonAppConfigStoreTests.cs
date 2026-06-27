using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Infrastructure;
using OpenCodeQueue.Infrastructure.Configuration;

namespace OpenCodeQueue.Tests;

public sealed class JsonAppConfigStoreTests
{
    [Fact]
    public async Task LoadAsync_LoadsMultipleProjectProfiles()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(configPath, """
            {
              "schemaVersion": 1,
              "activeProjectId": "project-b",
              "projects": [
                { "id": "project-a", "projectDir": "a" },
                { "id": "project-b", "projectDir": "b" }
              ]
            }
            """);

        var config = await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None);

        Assert.NotNull(config);
        Assert.Equal(2, config.Projects.Count);
        Assert.Equal("project-b", config.ActiveProjectId!.Value.Value);
    }

    [Fact]
    public async Task LoadAsync_NormalizesProjectDirAndIgnoresLegacyQueuePathSettings()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "config", "opencode-queue.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, """
            {
              "schemaVersion": 1,
              "projects": [
                {
                  "id": "project-a",
                  "projectDir": "../target project",
                  "promptsDir": "my prompts",
                  "qualityDir": "checks",
                  "stateDir": ".queue-state"
                }
              ]
            }
            """);

        var project = (await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None))!.Projects.Single();
        var expectedProjectDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, "../target project"));

        Assert.Equal(expectedProjectDir, project.ProjectDir);
        Assert.Equal(string.Empty, project.PromptsDir);
        Assert.Null(project.QualityDir);
        Assert.Equal(string.Empty, project.StateDir);
        Assert.Equal(string.Empty, project.CompletedDir);
        Assert.Equal(Path.Combine(expectedProjectDir, ".opencodequeue", "prompts"), ProjectPaths.PromptsDir(project));
        Assert.Equal(Path.Combine(expectedProjectDir, ".opencodequeue", "quality"), ProjectPaths.QualityDir(project));
    }

    [Fact]
    public void ProjectPaths_CompletedDirIsUnderFixedRunsDir()
    {
        var root = CreateTempRoot();
        var project = new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = root,
            StateDir = "queue state"
        };

        Assert.Equal(Path.Combine(root, ".opencodequeue", "runs", "completed"), ProjectPaths.CompletedDir(project));
    }

    [Fact]
    public async Task LoadAsync_IgnoresReviewsDirAlias()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(configPath, """
            {
              "schemaVersion": 1,
              "projects": [
                { "id": "project-a", "projectDir": "target", "reviewsDir": "reviews" }
              ]
            }
            """);

        var project = (await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None))!.Projects.Single();

        Assert.Null(project.QualityDir);
        Assert.Null(project.ReviewsDir);
    }

    [Fact]
    public async Task LoadAsync_IgnoresQualityDirAndReviewsAliasWhenBothAreSet()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(configPath, """
            {
              "schemaVersion": 1,
              "projects": [
                { "id": "project-a", "projectDir": "target", "qualityDir": "quality-checks", "reviewsDir": "reviews" }
              ]
            }
            """);

        var project = (await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None))!.Projects.Single();

        Assert.Null(project.QualityDir);
        Assert.Null(project.ReviewsDir);
    }

    [Fact]
    public async Task LoadAsync_AppliesDefaultsToProjectsAndAllowsProjectOverrides()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(configPath, """
            {
              "schemaVersion": 1,
              "defaults": {
                "serverUrl": "http://127.0.0.1:5000",
                "promptTransport": "FileAttachment",
                "maxInlinePromptChars": 12000
              },
              "projects": [
                { "id": "project-a", "projectDir": "target-a" },
                {
                  "id": "project-b",
                  "projectDir": "target-b",
                  "openCodeOverrides": {
                    "serverUrl": "http://127.0.0.1:5001"
                  }
                }
              ]
            }
            """);

        var projects = (await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None))!.Projects;
        var projectA = projects.Single(project => project.Id.Value == "project-a");
        var projectB = projects.Single(project => project.Id.Value == "project-b");

        Assert.Equal("http://127.0.0.1:5000", projectA.OpenCodeOverrides.ServerUrl);
        Assert.Equal(PromptTransport.FileAttachment, projectA.OpenCodeOverrides.PromptTransport);
        Assert.Equal(12000, projectA.OpenCodeOverrides.MaxInlinePromptChars);

        Assert.Equal("http://127.0.0.1:5001", projectB.OpenCodeOverrides.ServerUrl);
    }

    [Fact]
    public async Task LoadOrCreateDefaultAsync_ReturnsEmptyRegistryWhenConfigDoesNotExist()
    {
        var configPath = Path.Combine(CreateTempRoot(), "missing", "opencode-queue.json");

        var config = await new JsonAppConfigStore().LoadOrCreateDefaultAsync(configPath, CancellationToken.None);

        Assert.Equal(1, config.SchemaVersion);
        Assert.Null(config.ActiveProjectId);
        Assert.Empty(config.Projects);
    }

    [Fact]
    public async Task SaveAsync_CreatesParentDirectory()
    {
        var configPath = Path.Combine(CreateTempRoot(), "nested", "opencode-queue.json");

        await new JsonAppConfigStore().SaveAsync(configPath, new AppConfig(), CancellationToken.None);

        Assert.True(File.Exists(configPath));
    }

    [Fact]
    public async Task SaveAsync_DropsLegacyQueuePathSettings()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(configPath, """
            {
              "schemaVersion": 1,
              "activeProjectId": "project-a",
              "projects": [
                { "id": "project-a", "projectDir": "projects/a", "promptsDir": "prompts", "qualityDir": "quality", "stateDir": ".queue" },
                { "id": "project-b", "projectDir": "projects/b", "promptsDir": "prompts", "qualityDir": "quality", "stateDir": ".queue" }
              ]
            }
            """);
        var store = new JsonAppConfigStore();
        var config = await store.LoadAsync(configPath, CancellationToken.None);

        await store.SaveAsync(configPath, config! with { ActiveProjectId = "project-b" }, CancellationToken.None);

        var saved = await File.ReadAllTextAsync(configPath);
        var reloaded = await store.LoadAsync(configPath, CancellationToken.None);

        Assert.Equal("project-b", reloaded!.ActiveProjectId!.Value.Value);
        Assert.Equal(Path.Combine(root, "projects", "a"), reloaded.Projects.Single(project => project.Id.Value == "project-a").ProjectDir);
        Assert.Equal(Path.Combine(root, "projects", "b"), reloaded.Projects.Single(project => project.Id.Value == "project-b").ProjectDir);
        Assert.DoesNotContain("promptsDir", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("qualityDir", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("stateDir", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenCodeSettings_ToString_RedactsServerPassword()
    {
        var settings = new OpenCodeSettings { ServerPassword = "super-secret" };

        var text = settings.ToString();

        Assert.DoesNotContain("super-secret", text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void Validate_ReturnsAllErrorsInRussian()
    {
        var config = new AppConfig
        {
            ActiveProjectId = "missing",
            Projects =
            [
                new ProjectProfile { Id = "bad id", ProjectDir = "" }
            ]
        };

        var errors = AppConfigValidator.Validate(config);

        Assert.True(errors.Count >= 2);
        Assert.Contains(errors, error => error.Contains("id должен быть стабильным slug", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Активный проект 'missing' не найден", StringComparison.Ordinal));
    }

    private static string CreateTempRoot() => Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
}
