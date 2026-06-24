using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Infrastructure.Configuration;

namespace OpenCodeQueue.Tests;

public sealed class JsonProjectRegistryTests
{
    [Fact]
    public async Task AddOrUpdateAsync_SavesProjectAndMakesFirstProjectActive()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"), "opencode-queue.json");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());

        var result = await registry.AddOrUpdateAsync(configPath, new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = Path.Combine(Path.GetTempPath(), "project-a")
        }, CancellationToken.None);

        var active = await registry.GetActiveAsync(configPath, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(active);
        Assert.Equal("project-a", active.Id);
    }
}
