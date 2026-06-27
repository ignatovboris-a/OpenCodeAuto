using OpenCodeQueue.Core.Configuration;

namespace OpenCodeQueue.Infrastructure;

public static class ProjectPaths
{
    public const string QueueDirectoryName = ".opencodequeue";

    public static string QueueDir(ProjectProfile project) => Path.Combine(Path.GetFullPath(project.ProjectDir), QueueDirectoryName);

    public static string PromptsDir(ProjectProfile project) => Path.Combine(QueueDir(project), "prompts");

    public static string QualityDir(ProjectProfile project) => Path.Combine(QueueDir(project), "quality");

    public static string StateDir(ProjectProfile project) => QueueDir(project);

    public static string StateFile(ProjectProfile project) => Path.Combine(StateDir(project), "state.json");

    public static string EventsFile(ProjectProfile project) => Path.Combine(StateDir(project), "events.jsonl");

    public static string RunLockFile(ProjectProfile project) => Path.Combine(StateDir(project), "lock");

    public static string RunsDir(ProjectProfile project) => Path.Combine(StateDir(project), "runs");

    public static string RunDir(ProjectProfile project, string runId) => Path.Combine(RunsDir(project), runId);

    public static string RunManifestFile(ProjectProfile project, string runId) => Path.Combine(RunDir(project, runId), "manifest.json");

    public static string CompletedDir(ProjectProfile project) => Path.Combine(RunsDir(project), "completed");

    public static bool AreSamePath(string left, string right) => PathResolver.AreSamePath(left, right);
}
