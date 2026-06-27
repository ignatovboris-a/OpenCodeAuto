using OpenCodeQueue.Cli.Commands;
using OpenCodeQueue.Cli.ConsoleUi;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Discovery;
using OpenCodeQueue.Core.OpenCode;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.State;
using OpenCodeQueue.Core.Workflow;
using OpenCodeQueue.Infrastructure.Configuration;
using OpenCodeQueue.Infrastructure.Discovery;
using OpenCodeQueue.Infrastructure.Files;
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
    public async Task ProjectSelect_CreatesQueueFoldersForExistingRegistryProject()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDir);
        var project = new ProjectProfile { Id = "project-a", ProjectDir = projectDir };
        await new JsonProjectRegistry(new JsonAppConfigStore()).AddOrUpdateAsync(configPath, project, CancellationToken.None);
        var dispatcher = CreateDispatcher(new TestReporter());

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["project", "select", "project-a", "--config", configPath]), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(Directory.Exists(ProjectPaths.PromptsDir(project)));
        Assert.True(Directory.Exists(ProjectPaths.QualityDir(project)));
        Assert.True(Directory.Exists(ProjectPaths.RunsDir(project)));
        Assert.NotEmpty(Directory.EnumerateFiles(ProjectPaths.QualityDir(project), "*.md"));
    }

    [Fact]
    public async Task List_PrintsRussianPromptDiscoveryForSelectedProject()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        var project = new ProjectProfile { Id = "project-a", ProjectDir = projectDir };
        CreateQueueDirs(project);
        await File.WriteAllTextAsync(Path.Combine(ProjectPaths.PromptsDir(project), "1.10-cache.md"), "cache");
        await File.WriteAllTextAsync(Path.Combine(ProjectPaths.PromptsDir(project), "1.2-api.md"), "api");
        await File.WriteAllTextAsync(Path.Combine(ProjectPaths.QualityDir(project), "review.md"), "review");
        await File.WriteAllTextAsync(Path.Combine(ProjectPaths.QualityDir(project), "01-self-check.md"), "check");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, project, CancellationToken.None);
        await registry.SelectAsync(configPath, "project-a", CancellationToken.None);
        var reporter = new TestReporter();
        var dispatcher = CreateDispatcher(reporter);

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["list", "--config", configPath]), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(reporter.Messages, message => message.Contains("Проект: project-a", StringComparison.Ordinal));
        Assert.Contains(reporter.Messages, message => message.Contains("Ожидающие task prompts", StringComparison.Ordinal));
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
        var project = new ProjectProfile { Id = "project-a", ProjectDir = projectDir };
        CreateQueueDirs(project);
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, project, CancellationToken.None);
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
        var project = new ProjectProfile { Id = "project-a", ProjectDir = projectDir };
        CreateQueueDirs(project);
        await File.WriteAllTextAsync(Path.Combine(ProjectPaths.PromptsDir(project), "01.md"), "task");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
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

        Assert.Equal(3, exitCode);
        Assert.Contains(reporter.Messages, message => message.Contains("Новая задача не будет выбрана", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_ReturnsWorkflowStepFailedWhenOpenCodeStepFails()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        var project = new ProjectProfile { Id = "project-a", ProjectDir = projectDir };
        CreateQueueDirs(project);
        await File.WriteAllTextAsync(Path.Combine(ProjectPaths.PromptsDir(project), "01.md"), "task");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, project, CancellationToken.None);
        await registry.SelectAsync(configPath, "project-a", CancellationToken.None);
        var dispatcher = CreateDispatcher(new TestReporter(), new FakeOpenCodeClient { FailPrompt = true });

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["run", "--config", configPath, "--once"]), CancellationToken.None);

        Assert.Equal(5, exitCode);
    }

    [Fact]
    public async Task Doctor_ReturnsOpenCodeUnavailableWhenRuntimeCheckFails()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        var project = new ProjectProfile { Id = "project-a", ProjectDir = projectDir };
        CreateQueueDirs(project);
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, project, CancellationToken.None);
        await registry.SelectAsync(configPath, "project-a", CancellationToken.None);
        var dispatcher = CreateDispatcher(new TestReporter(), new FakeOpenCodeClient { EnsureReadyException = new OpenCodeClientException("нет opencode") });

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["doctor", "--config", configPath]), CancellationToken.None);

        Assert.Equal(4, exitCode);
    }

    [Fact]
    public async Task Validate_ReturnsValidationErrorWhenNoProjectIsSelected()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        await new JsonAppConfigStore().SaveAsync(configPath, new AppConfig
        {
            Projects = [new ProjectProfile { Id = "project-a", ProjectDir = Path.Combine(root, "project") }]
        }, CancellationToken.None);
        var reporter = new TestReporter();
        var dispatcher = CreateDispatcher(reporter);

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["validate", "--config", configPath]), CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains(reporter.Messages, message => message.Contains("Активный проект не выбран", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_AcceptsFixedQueueQualityDirectory()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        var project = new ProjectProfile { Id = "project-a", ProjectDir = projectDir };
        CreateQueueDirs(project);
        await File.WriteAllTextAsync(Path.Combine(ProjectPaths.PromptsDir(project), "01.md"), "task");
        await File.WriteAllTextAsync(Path.Combine(ProjectPaths.QualityDir(project), "01.md"), "review");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, project, CancellationToken.None);
        await registry.SelectAsync(configPath, "project-a", CancellationToken.None);
        var reporter = new TestReporter();
        var dispatcher = CreateDispatcher(reporter);

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["validate", "--config", configPath]), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(reporter.Messages, message => message.Contains("queue quality", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Doctor_SkipsRuntimeCheckWhenValidateFails()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDir);
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile { Id = "project-a", ProjectDir = projectDir }, CancellationToken.None);
        await registry.SelectAsync(configPath, "project-a", CancellationToken.None);
        var openCode = new FakeOpenCodeClient();
        var dispatcher = CreateDispatcher(new TestReporter(), openCode);

        var exitCode = await dispatcher.DispatchAsync(CliCommand.Parse(["doctor", "--config", configPath]), CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.False(openCode.EnsureReadyCalled);
    }

    private static CommandDispatcher CreateDispatcher(TestReporter reporter, FakeOpenCodeClient? openCodeClient = null)
    {
        var configStore = new JsonAppConfigStore();
        var registry = new JsonProjectRegistry(configStore);
        var prompts = new FileSystemPromptRepository();
        var state = new JsonStateStore();
        var runLock = new FileRunLock();
        openCodeClient ??= new FakeOpenCodeClient();
        var queueUseCases = new QueueUseCases(registry, prompts, state, runLock, openCodeClient, new RunWorkspace(), new FileSystemArchiver(), new SystemClock(), new OpenCodeStepResultClassifier());
        var discovery = new CompositeProjectDiscoveryService(configStore);
        var projectPrompt = new ProjectProfilePrompt(reporter);
        var presenter = new ProjectConsolePresenter(reporter);
        var operationResultPrinter = new OperationResultPrinter(reporter, presenter);
        var diagnosticsValidator = new ProjectDiagnosticsValidator();
        var menu = new InteractiveMenu(reporter, registry, prompts, discovery, configStore, state, queueUseCases, projectPrompt, presenter, operationResultPrinter);
        return new CommandDispatcher(reporter, configStore, registry, prompts, state, runLock, openCodeClient, queueUseCases, discovery, projectPrompt, presenter, operationResultPrinter, diagnosticsValidator, menu);
    }

    private static string CreateTempRoot() => Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));

    private static void CreateQueueDirs(ProjectProfile project)
    {
        Directory.CreateDirectory(ProjectPaths.PromptsDir(project));
        Directory.CreateDirectory(ProjectPaths.QualityDir(project));
        Directory.CreateDirectory(ProjectPaths.RunsDir(project));
    }

    private sealed class TestReporter : IConsoleReporter
    {
        public List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add(message);

        public void Warning(string message) => Messages.Add(message);

        public void Error(string message) => Messages.Add(message);

        public string? ReadLine(string prompt) => null;
    }

    private sealed class FakeOpenCodeClient : IOpenCodeClient
    {
        public bool FailPrompt { get; init; }

        public Exception? EnsureReadyException { get; init; }

        public bool EnsureReadyCalled { get; private set; }

        public Task EnsureReadyAsync(ProjectProfile project, CancellationToken cancellationToken)
        {
            EnsureReadyCalled = true;
            if (EnsureReadyException is not null)
            {
                throw EnsureReadyException;
            }

            return Task.CompletedTask;
        }

        public Task<OpenCodeSession> StartSessionAsync(ProjectProfile project, string title, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeSession("fake-session", project.ProjectDir, title));
        }

        public Task<OpenCodeSessionDetails> GetSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeSessionDetails(new OpenCodeSession(sessionId, project.ProjectDir), new OpenCodeSessionStatus(OpenCodeSessionState.Idle), []));
        }

        public Task<OpenCodeMessageResult> SendPromptAsync(ProjectProfile project, string sessionId, PromptPayload payload, CancellationToken cancellationToken)
        {
            if (FailPrompt)
            {
                return Task.FromResult(new OpenCodeMessageResult(false, payload.MessageId, false, "step failed"));
            }

            return Task.FromResult(new OpenCodeMessageResult(true, payload.MessageId));
        }

        public Task<OpenCodeMessageResult> WaitForPromptAsync(ProjectProfile project, string sessionId, string messageId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeMessageResult(true, messageId));
        }

        public Task<OpenCodeSessionStatus> GetSessionStatusAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenCodeSessionStatus(OpenCodeSessionState.Idle));
        }

        public Task AbortSessionAsync(ProjectProfile project, string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
