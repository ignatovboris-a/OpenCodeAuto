using OpenCodeQueue.Cli.Commands;
using OpenCodeQueue.Cli.ConsoleUi;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Discovery;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Infrastructure.Configuration;
using OpenCodeQueue.Infrastructure.Discovery;
using OpenCodeQueue.Infrastructure.Prompts;
using OpenCodeQueue.Infrastructure.State;
using OpenCodeQueue.Infrastructure;

namespace OpenCodeQueue.Tests;

public sealed class ProjectCommandTests
{
    [Fact]
    public async Task Run_ReturnsErrorWhenActiveProjectIsMissing()
    {
        var configPath = Path.Combine(CreateTempRoot(), "opencode-queue.json");
        var reporter = new TestReporter();
        var dispatcher = CreateDispatcher(reporter);

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["run", "--config", configPath]), CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains(reporter.Messages, message => message.Contains("Проект не выбран", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectOverride_DoesNotChangeActiveProjectId()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectADir = Path.Combine(root, "a");
        var projectBDir = Path.Combine(root, "b");
        Directory.CreateDirectory(projectADir);
        Directory.CreateDirectory(projectBDir);
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile { Id = "project-a", ProjectDir = projectADir }, CancellationToken.None);
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile { Id = "project-b", ProjectDir = projectBDir }, CancellationToken.None);
        await registry.SelectAsync(configPath, "project-a", CancellationToken.None);
        var dispatcher = CreateDispatcher(new TestReporter());

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["status", "--config", configPath, "--project", "project-b"]), CancellationToken.None);
        var config = await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("project-a", config!.ActiveProjectId!.Value.Value);
    }

    [Fact]
    public async Task List_PrintsRussianPromptDiscoveryForSelectedProject()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(projectDir, "prompts"));
        Directory.CreateDirectory(Path.Combine(projectDir, "quality"));
        await File.WriteAllTextAsync(Path.Combine(projectDir, "prompts", "1.10-cache.md"), "cache");
        await File.WriteAllTextAsync(Path.Combine(projectDir, "prompts", "1.2-api.md"), "api");
        await File.WriteAllTextAsync(Path.Combine(projectDir, "quality", "review.md"), "review");
        await File.WriteAllTextAsync(Path.Combine(projectDir, "quality", "01-self-check.md"), "check");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile { Id = "project-a", ProjectDir = projectDir }, CancellationToken.None);
        await registry.SelectAsync(configPath, "project-a", CancellationToken.None);
        var reporter = new TestReporter();
        var dispatcher = CreateDispatcher(reporter);

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["list", "--config", configPath]), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(reporter.Messages, message => message.Contains("Проект: project-a", StringComparison.Ordinal));
        Assert.Contains(reporter.Messages, message => message.Contains("Pending task prompts", StringComparison.Ordinal));
        Assert.True(reporter.Messages.IndexOf("  1.2  1.2-api.md") < reporter.Messages.IndexOf("  1.10  1.10-cache.md"));
        Assert.Contains(reporter.Messages, message => message.Contains("Quality prompts", StringComparison.Ordinal));
        Assert.Contains(reporter.Messages, message => message.Contains("review.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_PrintsEmptyQueueMessageInRussian()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(projectDir, "prompts"));
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile { Id = "project-a", ProjectDir = projectDir }, CancellationToken.None);
        await registry.SelectAsync(configPath, "project-a", CancellationToken.None);
        var reporter = new TestReporter();
        var dispatcher = CreateDispatcher(reporter);

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["list", "--config", configPath]), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(reporter.Messages, message => message.Contains("Очередь задач пуста", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_ReturnsErrorWhenProjectHasActiveRun()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(projectDir, "prompts"));
        await File.WriteAllTextAsync(Path.Combine(projectDir, "prompts", "01.md"), "task");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        var project = new ProjectProfile { Id = "project-a", ProjectDir = projectDir };
        await registry.AddOrUpdateAsync(configPath, project, CancellationToken.None);
        await registry.SelectAsync(configPath, "project-a", CancellationToken.None);
        await new JsonStateStore().SaveQueueStateAsync(project, new QueueState
        {
            ProjectId = "project-a",
            ProjectDirSnapshot = projectDir,
            ActiveRunId = "run-active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var reporter = new TestReporter();
        var dispatcher = CreateDispatcher(reporter);

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["run", "--config", configPath]), CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains(reporter.Messages, message => message.Contains("Новая задача не будет выбрана", StringComparison.Ordinal));
    }

    private static CommandDispatcher CreateDispatcher(TestReporter reporter)
    {
        var configStore = new JsonAppConfigStore();
        var registry = new JsonProjectRegistry(configStore);
        var prompts = new FileSystemPromptRepository();
        var state = new JsonStateStore();
        var runLock = new FileRunLock();
        var discovery = new CompositeProjectDiscoveryService(configStore);
        var projectPrompt = new ProjectProfilePrompt(reporter);
        var presenter = new ProjectConsolePresenter(reporter);
        var menu = new InteractiveMenu(reporter, registry, prompts, discovery, configStore, projectPrompt, presenter);
        return new CommandDispatcher(reporter, configStore, registry, prompts, state, runLock, new SystemClock(), discovery, projectPrompt, presenter, menu);
    }

    private static string CreateTempRoot() => Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));

    private sealed class TestReporter : IConsoleReporter
    {
        public List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add(message);

        public void Warning(string message) => Messages.Add(message);

        public void Error(string message) => Messages.Add(message);

        public string? ReadLine(string prompt) => null;
    }
}
