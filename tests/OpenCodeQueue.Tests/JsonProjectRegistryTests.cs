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

    [Fact]
    public async Task AddOrUpdateAsync_DoesNotSaveInvalidProject()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"), "opencode-queue.json");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());

        var result = await registry.AddOrUpdateAsync(configPath, new ProjectProfile
        {
            Id = "bad id",
            ProjectDir = Path.Combine(Path.GetTempPath(), "bad")
        }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(configPath));
        Assert.Contains("id должен быть стабильным slug", result.Message);
    }

    [Fact]
    public async Task SelectAsync_RejectsProjectWhenProjectDirDoesNotExist()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"), "opencode-queue.json");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile
        {
            Id = "project-a",
            ProjectDir = Path.Combine(Path.GetTempPath(), "missing", Guid.NewGuid().ToString("N"))
        }, CancellationToken.None);

        var result = await registry.SelectAsync(configPath, "project-a", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("projectDir не существует", result.Message);
    }

    [Fact]
    public async Task SelectAsync_SavesActiveProjectIdWhenProjectDirExists()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"), "opencode-queue.json");
        var projectDir = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"), "project-a");
        Directory.CreateDirectory(projectDir);
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile { Id = "project-a", ProjectDir = projectDir }, CancellationToken.None);

        var result = await registry.SelectAsync(configPath, "project-a", CancellationToken.None);
        var config = await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("project-a", config!.ActiveProjectId!.Value.Value);
    }

    [Fact]
    public async Task RemoveAsync_RejectsActiveProjectWithoutConfirmation()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"), "opencode-queue.json");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile { Id = "project-a", ProjectDir = Path.GetTempPath() }, CancellationToken.None);

        var result = await registry.RemoveAsync(configPath, "project-a", confirmedActiveRemoval: false, CancellationToken.None);
        var active = await registry.GetActiveAsync(configPath, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(active);
    }

    [Fact]
    public async Task RemoveAsync_RemovesNonActiveProjectWithoutChangingActiveProjectId()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(root, "opencode-queue.json");
        var projectADir = Path.Combine(root, "a");
        var projectBDir = Path.Combine(root, "b");
        Directory.CreateDirectory(projectADir);
        Directory.CreateDirectory(projectBDir);
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile { Id = "project-a", ProjectDir = projectADir }, CancellationToken.None);
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile { Id = "project-b", ProjectDir = projectBDir }, CancellationToken.None);
        await registry.SelectAsync(configPath, "project-a", CancellationToken.None);

        var result = await registry.RemoveAsync(configPath, "project-b", confirmedActiveRemoval: false, CancellationToken.None);
        var config = await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("project-a", config!.ActiveProjectId!.Value.Value);
        Assert.Equal("project-a", Assert.Single(config.Projects).Id.Value);
    }

    [Fact]
    public async Task RemoveAsync_ClearsActiveProjectIdWhenConfirmed()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "OpenCodeQueueTests", Guid.NewGuid().ToString("N"), "opencode-queue.json");
        var registry = new JsonProjectRegistry(new JsonAppConfigStore());
        await registry.AddOrUpdateAsync(configPath, new ProjectProfile { Id = "project-a", ProjectDir = Path.GetTempPath() }, CancellationToken.None);

        var result = await registry.RemoveAsync(configPath, "project-a", confirmedActiveRemoval: true, CancellationToken.None);
        var config = await new JsonAppConfigStore().LoadAsync(configPath, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(config!.ActiveProjectId);
        Assert.Empty(config.Projects);
    }
}
