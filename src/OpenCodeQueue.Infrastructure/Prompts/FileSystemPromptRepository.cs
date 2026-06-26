using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Prompts;
using OpenCodeQueue.Infrastructure.Files;

namespace OpenCodeQueue.Infrastructure.Prompts;

public sealed partial class FileSystemPromptRepository : IPromptRepository
{
    public async Task<PromptDiscoveryResult> DiscoverAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var tasks = await GetPromptsAsync(project, PromptKind.Task, warnings, cancellationToken);
        var quality = await GetPromptsAsync(project, PromptKind.Quality, warnings, cancellationToken);
        return new PromptDiscoveryResult(tasks, quality, warnings);
    }

    public Task<string> ReadPromptTextAsync(PromptDescriptor prompt, CancellationToken cancellationToken)
    {
        return ReadPromptTextAsync(prompt.Path, cancellationToken);
    }

    public Task<string> ReadPromptTextAsync(string promptPath, CancellationToken cancellationToken)
    {
        return File.ReadAllTextAsync(promptPath, cancellationToken);
    }

    private static async Task<IReadOnlyList<PromptDescriptor>> GetPromptsAsync(ProjectProfile project, PromptKind kind, List<string> warnings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullDirectory;
        try
        {
            fullDirectory = kind == PromptKind.Task ? ProjectPaths.PromptsDir(project) : ProjectPaths.QualityDir(project);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            warnings.Add($"Некорректный путь папки {KindTitle(kind)}: {exception.Message}");
            return [];
        }

        if (!Directory.Exists(fullDirectory))
        {
            warnings.Add($"Папка {KindTitle(kind)} не существует или недоступна: {fullDirectory}");
            return [];
        }

        string[] filePaths;
        try
        {
            filePaths = Directory.EnumerateFiles(fullDirectory, "*.md", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            warnings.Add($"Не удалось прочитать папку {KindTitle(kind)}: {fullDirectory}. {exception.Message}");
            return [];
        }

        var files = new List<NumberedPromptFile>();
        foreach (var path in filePaths)
        {
            var fileName = Path.GetFileName(path);
            if (kind == PromptKind.Task && fileName.StartsWith('_'))
            {
                continue;
            }

            if (NumericPrefix.TryParseFileNamePrefix(fileName, out var prefix))
            {
                files.Add(new NumberedPromptFile(Path.GetFullPath(path), prefix!));
                continue;
            }

            warnings.Add($"Файл без числового префикса пропущен ({KindTitle(kind)}): {fileName}");
        }

        // Equal numeric keys are ordered deterministically by file name and then full path.
        var orderedFiles = files
            .OrderBy(prompt => prompt.Prefix)
            .ThenBy(prompt => Path.GetFileName(prompt.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(prompt => prompt.Path, StringComparer.Ordinal)
            .ToArray();

        var prompts = new List<PromptDescriptor>(orderedFiles.Length);
        foreach (var file in orderedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file.Path);
                prompts.Add(new PromptDescriptor(
                    file.Path,
                    Path.GetFileName(file.Path),
                    file.Prefix,
                    await FileHash.ComputeSha256Async(file.Path, cancellationToken),
                    info.Length,
                    info.LastWriteTimeUtc,
                    kind));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                warnings.Add($"Не удалось прочитать prompt-файл ({KindTitle(kind)}): {file.Path}. {exception.Message}");
            }
        }

        return prompts;
    }

    private static string KindTitle(PromptKind kind) => kind == PromptKind.Task ? "tasks" : "quality";

    private sealed record NumberedPromptFile(string Path, NumericPrefix Prefix);
}
