using OpenCodeQueue.Cli.Commands;

namespace OpenCodeQueue.Tests;

public sealed class CliCommandTests
{
    [Fact]
    public void Parse_SkipsOptionValuesWhenReadingPositionals()
    {
        var command = CliCommand.Parse(["project", "select", "project-a", "--config", "queue.json"]);

        Assert.Equal("project", command.Name);
        Assert.Equal("select", command.SubCommand);
        Assert.Equal("project-a", command.Argument);
        Assert.Equal("queue.json", command.ConfigPath);
    }

    [Fact]
    public void Parse_DoesNotUseNextOptionAsMissingOptionValue()
    {
        var command = CliCommand.Parse(["run", "--config", "--once"]);

        Assert.Equal("opencode-queue.json", command.ConfigPath);
        Assert.True(command.Once);
    }
}
