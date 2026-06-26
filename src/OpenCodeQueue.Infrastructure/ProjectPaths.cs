using OpenCodeQueue.Core.Configuration;

namespace OpenCodeQueue.Infrastructure;

public static class ProjectPaths
{
    public static string PromptsDir(ProjectProfile project) => PathResolver.ResolveProjectPath(project.PromptsDir, project.ProjectDir, "prompts");

    public static string QualityDir(ProjectProfile project) => PathResolver.ResolveProjectPath(project.QualityDir ?? project.ReviewsDir, project.ProjectDir, "quality");

    public static string StateDir(ProjectProfile project) => PathResolver.ResolveProjectPath(project.StateDir, project.ProjectDir, ".queue");

    public static string StateFile(ProjectProfile project) => Path.Combine(StateDir(project), "state.json");

    public static string EventsFile(ProjectProfile project) => Path.Combine(StateDir(project), "events.jsonl");

    public static string RunLockFile(ProjectProfile project) => Path.Combine(StateDir(project), "lock");

    public static string RunsDir(ProjectProfile project) => Path.Combine(StateDir(project), "runs");

    public static string RunDir(ProjectProfile project, string runId) => Path.Combine(RunsDir(project), runId);

    public static string RunManifestFile(ProjectProfile project, string runId) => Path.Combine(RunDir(project, runId), "manifest.json");

    public static string CompletedDir(ProjectProfile project) => string.IsNullOrWhiteSpace(project.CompletedDir)
        ? Path.Combine(StateDir(project), "completed")
        : PathResolver.ResolveProjectPath(project.CompletedDir, project.ProjectDir);

    public static bool AreSamePath(string left, string right) => PathResolver.AreSamePath(left, right);
}
