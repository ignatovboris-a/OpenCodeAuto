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
        Assert.Equal("project-b", config.GetActiveProjectOrThrow().Id);
    }

    [Fact]
    public async Task LoadAsync_NormalizesRelativePathsPredictably()
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
        Assert.Equal(Path.Combine(expectedProjectDir, "my prompts"), project.PromptsDir);
        Assert.Equal(Path.Combine(expectedProjectDir, "checks"), project.QualityDir);
        Assert.Equal(Path.Combine(expectedProjectDir, ".queue-state"), project.StateDir);
        Assert.Equal(Path.Combine(expectedProjectDir, ".queue-state", "completed"), project.CompletedDir);
    }

    [Fact]
    public void ProjectPaths_CompletedDirDefaultFollowsResolvedStateDir()
    {
        var root = CreateTempRoot();
        var project = new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = root,
            StateDir = "queue state"
        };

        Assert.Equal(Path.Combine(root, "queue state", "completed"), ProjectPaths.CompletedDir(project));
    }

    [Fact]
    public async Task LoadAsync_UsesReviewsDirAsQualityAlias()
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

        Assert.Equal(Path.Combine(project.ProjectDir, "reviews"), project.QualityDir);
        Assert.Null(project.ReviewsDir);
    }

    [Fact]
    public async Task LoadAsync_QualityDirWinsOverReviewsAliasWhenBothAreSet()
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

        Assert.Equal(Path.Combine(project.ProjectDir, "quality-checks"), project.QualityDir);
        Assert.Null(project.ReviewsDir);
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
    public void GetActiveProjectOrThrow_WhenNotSelected_ReturnsRussianError()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new AppConfig().GetActiveProjectOrThrow());

        Assert.Equal("Активный проект не выбран.", exception.Message);
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
