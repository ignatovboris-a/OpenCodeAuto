using OpenCodeQueue.Cli.ConsoleUi;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Discovery;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Workflow;
using OpenCodeQueue.Infrastructure.Configuration;
using OpenCodeQueue.Infrastructure.Discovery;
using OpenCodeQueue.Infrastructure.Prompts;
using OpenCodeQueue.Infrastructure.State;

namespace OpenCodeQueue.Tests;

public sealed class InteractiveMenuTests
{
    [Fact]
    public async Task RunAsync_WhenConfigMissing_OffersFirstStartSetupAndSavesManualProject()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "manual project");
        Directory.CreateDirectory(projectDir);
        var reporter = new TestReporter([
            "2",
            "project-a", "Project A", projectDir,
            "", "y",
            "", "y",
            "", "y",
            "", "",
            "y",
            "0"
        ]);
        var menu = CreateMenu(reporter);

        var exitCode = await menu.RunAsync(configPath, CancellationToken.None);
        var config = await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(config);
        var project = Assert.Single(config!.Projects);
        Assert.Equal("project-a", project.Id.Value);
        Assert.Equal(projectDir, project.ProjectDir);
        Assert.Equal("project-a", config.ActiveProjectId!.Value.Value);
        Assert.Contains(reporter.Messages, message => message.Contains("Конфигурация OpenCodeQueue не найдена", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(projectDir, "prompts")));
        Assert.True(Directory.Exists(Path.Combine(projectDir, "quality")));
        Assert.True(Directory.Exists(Path.Combine(projectDir, ".queue")));
    }

    [Fact]
    public async Task RunAsync_FirstStartManualProject_UsesDefaultDirectoriesWhenAccepted()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDir);
        var reporter = new TestReporter([
            "2",
            "project-a", "", projectDir,
            "", "y",
            "", "y",
            "", "y",
            "", "",
            "y",
            "0"
        ]);

        await CreateMenu(reporter).RunAsync(configPath, CancellationToken.None);
        var project = (await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None))!.Projects.Single();

        Assert.Equal(Path.Combine(projectDir, "prompts"), project.PromptsDir);
        Assert.Equal(Path.Combine(projectDir, "quality"), project.QualityDir);
        Assert.Equal(Path.Combine(projectDir, ".queue"), project.StateDir);
    }

    [Fact]
    public async Task RunAsync_WhenFolderCreationRejected_SavesChosenPathsWithoutCreatingFolders()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDir);
        var reporter = new TestReporter([
            "2",
            "project-a", "", projectDir,
            "", "n",
            "", "n",
            "", "n",
            "", "",
            "y",
            "0"
        ]);

        await CreateMenu(reporter).RunAsync(configPath, CancellationToken.None);
        var config = await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None);

        Assert.NotNull(config);
        Assert.Equal("project-a", Assert.Single(config!.Projects).Id.Value);
        Assert.False(Directory.Exists(Path.Combine(projectDir, "prompts")));
        Assert.False(Directory.Exists(Path.Combine(projectDir, "quality")));
    }

    [Fact]
    public async Task RunAsync_SelectingActiveProjectChangesFollowingStatusAction()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectA = await AddProjectAsync(configPath, root, "project-a");
        var projectB = await AddProjectAsync(configPath, root, "project-b");
        await new JsonProjectRegistry(new JsonAppConfigStore()).SelectAsync(configPath, projectA.Id.Value, CancellationToken.None);
        var useCases = new FakeQueueUseCases();
        var reporter = new TestReporter(["6", projectB.Id.Value, "4", "0"]);

        await CreateMenu(reporter, useCases).RunAsync(configPath, CancellationToken.None);

        Assert.Equal("project-b", useCases.StatusProjectIds.Single());
    }

    [Fact]
    public async Task RunAsync_WhenActiveRunExists_BlocksNewQueueRunFromMenu()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var project = await AddProjectAsync(configPath, root, "project-a");
        await new JsonProjectRegistry(new JsonAppConfigStore()).SelectAsync(configPath, project.Id.Value, CancellationToken.None);
        await new JsonStateStore().SaveQueueStateAsync(project, new Core.State.QueueState
        {
            ProjectId = project.Id,
            ProjectDirSnapshot = project.ProjectDir,
            ActiveRunId = "run-active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var useCases = new FakeQueueUseCases();
        var reporter = new TestReporter(["1", "0"]);

        await CreateMenu(reporter, useCases).RunAsync(configPath, CancellationToken.None);

        Assert.Equal(0, useCases.RunQueueCalls);
        Assert.Contains(reporter.Messages, message => message.Contains("Новый запуск заблокирован", StringComparison.Ordinal));
    }

    private static InteractiveMenu CreateMenu(TestReporter reporter, FakeQueueUseCases? queueUseCases = null)
    {
        var configStore = new JsonAppConfigStore();
        var registry = new JsonProjectRegistry(configStore);
        var promptRepository = new FileSystemPromptRepository();
        var stateStore = new JsonStateStore();
        queueUseCases ??= new FakeQueueUseCases();
        var presenter = new ProjectConsolePresenter(reporter);
        return new InteractiveMenu(
            reporter,
            registry,
            promptRepository,
            new EmptyDiscoveryService(),
            configStore,
            stateStore,
            queueUseCases,
            new ProjectProfilePrompt(reporter),
            presenter,
            new OperationResultPrinter(reporter, presenter));
    }

    private static async Task<ProjectProfile> AddProjectAsync(string configPath, string root, string id)
    {
        var projectDir = Path.Combine(root, id);
        Directory.CreateDirectory(Path.Combine(projectDir, "prompts"));
        Directory.CreateDirectory(Path.Combine(projectDir, "quality"));
        var project = new ProjectProfile { Id = id, ProjectDir = projectDir };
        await new JsonProjectRegistry(new JsonAppConfigStore()).AddOrUpdateAsync(configPath, project, CancellationToken.None);
        return project;
    }

    private static string CreateTempRoot() => Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));

    private sealed class TestReporter(IReadOnlyList<string?> answers) : IConsoleReporter
    {
        private int index;

        public List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add(message);

        public void Warning(string message) => Messages.Add(message);

        public void Error(string message) => Messages.Add(message);

        public string? ReadLine(string prompt) => index >= answers.Count ? null : answers[index++];
    }

    private sealed class EmptyDiscoveryService : IProjectDiscoveryService
    {
        public Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DiscoveredProject>>([]);

        public Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(string configPath, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DiscoveredProject>>([]);
    }

    private sealed class FakeQueueUseCases : IQueueUseCases
    {
        public int RunQueueCalls { get; private set; }

        public List<string?> StatusProjectIds { get; } = [];

        public Task<QueueOperationResult> RunQueueAsync(string configPath, string? projectId, bool once, CancellationToken cancellationToken)
        {
            RunQueueCalls++;
            return Task.FromResult(QueueOperationResult.Success("run"));
        }

        public Task<QueueOperationResult> ResumeAsync(string configPath, string? projectId, CancellationToken cancellationToken) => Task.FromResult(QueueOperationResult.Success("resume"));

        public Task<QueueOperationResult> GetStatusAsync(string configPath, string? projectId, CancellationToken cancellationToken)
        {
            StatusProjectIds.Add(projectId);
            return Task.FromResult(QueueOperationResult.Success("status"));
        }

        public Task<QueueOperationResult> ListPromptsAsync(string configPath, string? projectId, CancellationToken cancellationToken) => Task.FromResult(QueueOperationResult.Success("list"));

        public Task<QueueOperationResult> AbortRunAsync(string configPath, string? projectId, CancellationToken cancellationToken) => Task.FromResult(QueueOperationResult.Success("abort"));
    }
}
