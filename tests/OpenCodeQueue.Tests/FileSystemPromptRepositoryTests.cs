using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Infrastructure.Prompts;

namespace OpenCodeQueue.Tests;

public sealed class FileSystemPromptRepositoryTests
{
    [Fact]
    public async Task GetTaskPromptsAsync_ReturnsOnlyNumberedMarkdownFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var prompts = Path.Combine(root, "prompts");
        Directory.CreateDirectory(prompts);
        await File.WriteAllTextAsync(Path.Combine(prompts, "01-first.md"), "first");
        await File.WriteAllTextAsync(Path.Combine(prompts, "0.1. auth.md"), "auth");
        await File.WriteAllTextAsync(Path.Combine(prompts, "notes.md"), "ignore");

        var repository = new FileSystemPromptRepository();
        var project = new ProjectProfile { Id = "test", ProjectDir = root };

        var result = await repository.GetTaskPromptsAsync(project, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, prompt => prompt.FileName == "01-first.md");
        Assert.Contains(result, prompt => prompt.FileName == "0.1. auth.md");
    }
}
