using OpenCodeQueue.Cli.ConsoleUi;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;

namespace OpenCodeQueue.Tests;

public sealed class ProjectProfilePromptTests
{
    [Fact]
    public void ReadNewProject_ReturnsNullWhenProjectDirCreationIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var reporter = new TestReporter(["project-a", "Project A", root, "n"]);

        var project = new ProjectProfilePrompt(reporter).ReadNewProject(askOpenCodeOverrides: true);

        Assert.Null(project);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ReadNewProject_AppliesOpenCodeOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reporter = new TestReporter([
            "project-a",
            "Project A",
            root,
            "",
            "n",
            "",
            "n",
            "",
            "n",
            "http://127.0.0.1:4097",
            "custom-opencode"
        ]);

        var project = new ProjectProfilePrompt(reporter).ReadNewProject(askOpenCodeOverrides: true);

        Assert.NotNull(project);
        Assert.Equal("http://127.0.0.1:4097", project.OpenCodeOverrides.ServerUrl);
        Assert.Equal("custom-opencode", project.OpenCodeOverrides.OpenCodeExecutable);
    }

    [Fact]
    public void ReadUpdatedProject_ResolvesRelativeDirectoriesFromUpdatedProjectDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var updatedProjectDir = Path.Combine(root, "updated project");
        var current = new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = Path.Combine(root, "old"),
            PromptsDir = Path.Combine(root, "old", "prompts"),
            QualityDir = Path.Combine(root, "old", "quality"),
            StateDir = Path.Combine(root, "old", ".queue")
        };
        var reporter = new TestReporter([
            "",
            updatedProjectDir,
            "custom prompts",
            "checks",
            "queue-state",
            "",
            ""
        ]);

        var project = new ProjectProfilePrompt(reporter).ReadUpdatedProject(current);

        Assert.Equal(updatedProjectDir, project.ProjectDir);
        Assert.Equal(Path.Combine(updatedProjectDir, "custom prompts"), project.PromptsDir);
        Assert.Equal(Path.Combine(updatedProjectDir, "checks"), project.QualityDir);
        Assert.Equal(Path.Combine(updatedProjectDir, "queue-state"), project.StateDir);
    }

    private sealed class TestReporter(IReadOnlyList<string?> answers) : IConsoleReporter
    {
        private int index;

        public void Info(string message)
        {
        }

        public void Warning(string message)
        {
        }

        public void Error(string message)
        {
        }

        public string? ReadLine(string prompt)
        {
            return index >= answers.Count ? null : answers[index++];
        }
    }
}
