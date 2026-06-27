using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Infrastructure;
using OpenCodeQueue.Infrastructure.Prompts;

namespace OpenCodeQueue.Tests;

public sealed class FileSystemPromptRepositoryTests
{
    [Fact]
    public async Task DiscoverAsync_ReturnsOnlyNumberedMarkdownTaskFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var prompts = Prompts(root);
        Directory.CreateDirectory(prompts);
        await File.WriteAllTextAsync(Path.Combine(prompts, "01-first.md"), "first");
        await File.WriteAllTextAsync(Path.Combine(prompts, "0.1. auth.md"), "auth");
        await File.WriteAllTextAsync(Path.Combine(prompts, "notes.md"), "ignore");

        var repository = new FileSystemPromptRepository();
        var project = new ProjectProfile { Id = "test", ProjectDir = root };

        var result = (await repository.DiscoverAsync(project, CancellationToken.None)).TaskPrompts;

        Assert.Equal(2, result.Count);
        Assert.Contains(result, prompt => prompt.FileName == "01-first.md");
        Assert.Contains(result, prompt => prompt.FileName == "0.1. auth.md");
        Assert.All(result, prompt => Assert.Equal(64, prompt.ContentHash.Length));
        Assert.All(result, prompt => Assert.True(prompt.SizeBytes > 0));
        Assert.All(result, prompt => Assert.True(prompt.LastWriteTimeUtc <= DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task DiscoverAsync_OrdersTaskFilesByNumericPrefixSegments()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var prompts = Prompts(root);
        Directory.CreateDirectory(prompts);
        await File.WriteAllTextAsync(Path.Combine(prompts, "10-late.md"), "late");
        await File.WriteAllTextAsync(Path.Combine(prompts, "2-middle.md"), "middle");
        await File.WriteAllTextAsync(Path.Combine(prompts, "1.2-nested.md"), "nested");
        await File.WriteAllTextAsync(Path.Combine(prompts, "1-first.md"), "first");

        var repository = new FileSystemPromptRepository();
        var project = new ProjectProfile { Id = "test", ProjectDir = root };

        var result = (await repository.DiscoverAsync(project, CancellationToken.None)).TaskPrompts;

        Assert.Equal(["1-first.md", "1.2-nested.md", "2-middle.md", "10-late.md"], result.Select(prompt => prompt.FileName));
    }

    [Theory]
    [InlineData("01.md", "1")]
    [InlineData("1.md", "1")]
    [InlineData("01-task.md", "1")]
    [InlineData("01 task.md", "1")]
    [InlineData("01.task.md", "1")]
    [InlineData("0.1.md", "0.1")]
    [InlineData("0.1. task.md", "0.1")]
    [InlineData("0.0.2-refactor.md", "0.0.2")]
    [InlineData("1.10-add-cache.md", "1.10")]
    public async Task DiscoverAsync_ParsesSupportedNumericPrefixFormats(string fileName, string expectedPrefix)
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var prompts = Prompts(root);
        Directory.CreateDirectory(prompts);
        await File.WriteAllTextAsync(Path.Combine(prompts, fileName), "prompt");

        var repository = new FileSystemPromptRepository();
        var project = new ProjectProfile { Id = "test", ProjectDir = root };

        var result = (await repository.DiscoverAsync(project, CancellationToken.None)).TaskPrompts;

        var prompt = Assert.Single(result);
        Assert.Equal(fileName, prompt.FileName);
        Assert.Equal(expectedPrefix, string.Join('.', prompt.Prefix.Segments));
    }

    [Fact]
    public async Task DiscoverAsync_OrdersNumericSegmentsAsNumbers()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var prompts = Prompts(root);
        Directory.CreateDirectory(prompts);
        await File.WriteAllTextAsync(Path.Combine(prompts, "1.10-cache.md"), "cache");
        await File.WriteAllTextAsync(Path.Combine(prompts, "1.2-api.md"), "api");
        await File.WriteAllTextAsync(Path.Combine(prompts, "0.10-later.md"), "later");
        await File.WriteAllTextAsync(Path.Combine(prompts, "0.2-earlier.md"), "earlier");
        await File.WriteAllTextAsync(Path.Combine(prompts, "0.1-middle.md"), "middle");
        await File.WriteAllTextAsync(Path.Combine(prompts, "0.0.2-first.md"), "first");

        var repository = new FileSystemPromptRepository();
        var project = new ProjectProfile { Id = "test", ProjectDir = root };

        var result = (await repository.DiscoverAsync(project, CancellationToken.None)).TaskPrompts;

        Assert.Equal([
            "0.0.2-first.md",
            "0.1-middle.md",
            "0.2-earlier.md",
            "0.10-later.md",
            "1.2-api.md",
            "1.10-cache.md"], result.Select(prompt => prompt.FileName));
    }

    [Fact]
    public async Task DiscoverAsync_OrdersRequiredNumberedFormatsAndWarnsForUnprefixedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var prompts = Prompts(root);
        Directory.CreateDirectory(prompts);
        foreach (var fileName in new[]
        {
            "01.md",
            "1.md",
            "01-task.md",
            "01 task.md",
            "01.task.md",
            "0.1.md",
            "0.1. task.md",
            "0.0.2-refactor.md",
            "1.10.md",
            "1.2.md",
            "notes.md"
        })
        {
            await File.WriteAllTextAsync(Path.Combine(prompts, fileName), fileName);
        }

        var result = await new FileSystemPromptRepository().DiscoverAsync(new ProjectProfile { Id = "test", ProjectDir = root }, CancellationToken.None);

        Assert.Equal([
            "0.0.2-refactor.md",
            "0.1. task.md",
            "0.1.md",
            "01 task.md",
            "01-task.md",
            "01.md",
            "01.task.md",
            "1.md",
            "1.2.md",
            "1.10.md"], result.TaskPrompts.Select(prompt => prompt.FileName));
        Assert.Contains(result.Warnings, warning => warning.Contains("notes.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_OrdersEqualNumericKeysByFileNameThenPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var prompts = Prompts(root);
        Directory.CreateDirectory(prompts);
        await File.WriteAllTextAsync(Path.Combine(prompts, "01-b.md"), "b");
        await File.WriteAllTextAsync(Path.Combine(prompts, "1-a.md"), "a");

        var repository = new FileSystemPromptRepository();
        var project = new ProjectProfile { Id = "test", ProjectDir = root };

        var result = (await repository.DiscoverAsync(project, CancellationToken.None)).TaskPrompts;

        Assert.Equal(["01-b.md", "1-a.md"], result.Select(prompt => prompt.FileName));
        Assert.True(result[0].Prefix.CompareTo(result[1].Prefix) == 0);
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsSeparateResultsForDifferentProjectProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var projectA = Path.Combine(root, "a");
        var projectB = Path.Combine(root, "b");
        Directory.CreateDirectory(Prompts(projectA));
        Directory.CreateDirectory(Quality(projectA));
        Directory.CreateDirectory(Prompts(projectB));
        Directory.CreateDirectory(Quality(projectB));
        await File.WriteAllTextAsync(Path.Combine(Prompts(projectA), "01-a.md"), "a");
        await File.WriteAllTextAsync(Path.Combine(Quality(projectA), "01-qa.md"), "qa");
        await File.WriteAllTextAsync(Path.Combine(Prompts(projectB), "01-b.md"), "b");
        await File.WriteAllTextAsync(Path.Combine(Quality(projectB), "01-qb.md"), "qb");

        var repository = new FileSystemPromptRepository();

        var resultA = await repository.DiscoverAsync(new ProjectProfile { Id = "a", ProjectDir = projectA }, CancellationToken.None);
        var resultB = await repository.DiscoverAsync(new ProjectProfile { Id = "b", ProjectDir = projectB }, CancellationToken.None);

        Assert.Equal("01-a.md", Assert.Single(resultA.TaskPrompts).FileName);
        Assert.Equal("01-qa.md", Assert.Single(resultA.QualityPrompts).FileName);
        Assert.Equal("01-b.md", Assert.Single(resultB.TaskPrompts).FileName);
        Assert.Equal("01-qb.md", Assert.Single(resultB.QualityPrompts).FileName);
    }

    [Fact]
    public async Task DiscoverAsync_WarnsAboutQualityFilesWithoutNumericPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var quality = Quality(root);
        Directory.CreateDirectory(Prompts(root));
        Directory.CreateDirectory(quality);
        await File.WriteAllTextAsync(Path.Combine(quality, "review.md"), "review");

        var repository = new FileSystemPromptRepository();
        var project = new ProjectProfile { Id = "test", ProjectDir = root };

        var result = await repository.DiscoverAsync(project, CancellationToken.None);

        Assert.Empty(result.QualityPrompts);
        Assert.Contains(result.Warnings, warning => warning.Contains("review.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_WarnsInsteadOfThrowingForTooLargeNumericPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var prompts = Prompts(root);
        Directory.CreateDirectory(prompts);
        await File.WriteAllTextAsync(Path.Combine(prompts, "999999999999999999999999999999-task.md"), "prompt");

        var repository = new FileSystemPromptRepository();
        var project = new ProjectProfile { Id = "test", ProjectDir = root };

        var result = await repository.DiscoverAsync(project, CancellationToken.None);

        Assert.Empty(result.TaskPrompts);
        Assert.Contains(result.Warnings, warning => warning.Contains("999999999999999999999999999999-task.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_IgnoresUnderscoreDraftsOnlyForTasks()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Prompts(root));
        Directory.CreateDirectory(Quality(root));
        await File.WriteAllTextAsync(Path.Combine(Prompts(root), "_01-draft.md"), "draft");
        await File.WriteAllTextAsync(Path.Combine(Quality(root), "_01-review.md"), "review");

        var repository = new FileSystemPromptRepository();
        var project = new ProjectProfile { Id = "test", ProjectDir = root };

        var result = await repository.DiscoverAsync(project, CancellationToken.None);

        Assert.Empty(result.TaskPrompts);
        Assert.Empty(result.QualityPrompts);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("_01-draft.md", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("_01-review.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoverAsync_IgnoresConfiguredDirectoriesAndUsesFixedQueueLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var completed = Path.Combine(root, "completed");
        Directory.CreateDirectory(completed);
        await File.WriteAllTextAsync(Path.Combine(completed, "01-task.md"), "task");
        Directory.CreateDirectory(Prompts(root));
        Directory.CreateDirectory(Quality(root));

        var result = await new FileSystemPromptRepository().DiscoverAsync(new ProjectProfile
        {
            Id = "test",
            ProjectDir = root,
            PromptsDir = completed,
            QualityDir = completed
        }, CancellationToken.None);

        Assert.Empty(result.TaskPrompts);
        Assert.Empty(result.QualityPrompts);
    }

    private static string Prompts(string projectDir) => ProjectPaths.PromptsDir(new ProjectProfile { Id = "test", ProjectDir = projectDir });

    private static string Quality(string projectDir) => ProjectPaths.QualityDir(new ProjectProfile { Id = "test", ProjectDir = projectDir });
}
