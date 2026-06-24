using System.Text.RegularExpressions;
using OpenCodeQueue.Core.Configuration;
using OpenCodeQueue.Core.Ports;
using OpenCodeQueue.Core.Prompts;

namespace OpenCodeQueue.Infrastructure.Prompts;

public sealed partial class FileSystemPromptRepository : IPromptRepository
{
    public Task<IReadOnlyList<PromptFile>> GetTaskPromptsAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        return GetPromptsAsync(project, project.PromptsDir, PromptKind.Task, cancellationToken);
    }

    public Task<IReadOnlyList<PromptFile>> GetQualityPromptsAsync(ProjectProfile project, CancellationToken cancellationToken)
    {
        return GetPromptsAsync(project, project.QualityDir, PromptKind.Quality, cancellationToken);
    }

    public Task<string> ReadPromptTextAsync(PromptFile prompt, CancellationToken cancellationToken)
    {
        return File.ReadAllTextAsync(prompt.Path, cancellationToken);
    }

    private static Task<IReadOnlyList<PromptFile>> GetPromptsAsync(ProjectProfile project, string directory, PromptKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullDirectory = Path.GetFullPath(Path.Combine(project.ProjectDir, directory));
        if (!Directory.Exists(fullDirectory))
        {
            IReadOnlyList<PromptFile> empty = [];
            return Task.FromResult(empty);
        }

        var prompts = Directory.EnumerateFiles(fullDirectory, "*.md", SearchOption.TopDirectoryOnly)
            .Select(path => new { Path = path, Match = NumberPrefixRegex().Match(Path.GetFileName(path)) })
            .Where(item => item.Match.Success)
            .Select(item => new PromptFile(item.Path, Path.GetFileName(item.Path), item.Match.Groups[1].Value, kind))
            .OrderBy(prompt => prompt.NumberPrefix, NumberPrefixComparer.Instance)
            .ThenBy(prompt => prompt.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<PromptFile>>(prompts);
    }

    [GeneratedRegex("^([0-9]+(?:\\.[0-9]+)*)")]
    private static partial Regex NumberPrefixRegex();
}
