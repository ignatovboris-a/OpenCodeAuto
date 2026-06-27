using System.Reflection;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Infrastructure;

namespace OpenCodeQueue.Cli.ConsoleUi;

internal static class ProjectFileInitializer
{
    private const string QualityResourcePrefix = "OpenCodeQueue.Cli.Resources.Quality.";

    public static async Task EnsureProjectFilesAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ProjectPaths.QueueDir(project));
        Directory.CreateDirectory(ProjectPaths.PromptsDir(project));
        Directory.CreateDirectory(ProjectPaths.QualityDir(project));
        Directory.CreateDirectory(ProjectPaths.RunsDir(project));
        await CopyQualityResourcesAsync(project, cancellationToken);
    }

    private static async Task CopyQualityResourcesAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resourceName in assembly.GetManifestResourceNames().Where(name => name.StartsWith(QualityResourcePrefix, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = resourceName[QualityResourcePrefix.Length..];
            var destination = Path.Combine(ProjectPaths.QualityDir(project), fileName);
            if (File.Exists(destination))
            {
                continue;
            }

            await using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Embedded quality resource not found: " + resourceName);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
        }
    }
}
