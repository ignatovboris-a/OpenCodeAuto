using OpenCodeQueue.Core.Configuration;

namespace OpenCodeQueue.Infrastructure;

internal static class ProjectPaths
{
    public static string StateDir(ProjectProfile project) => Path.GetFullPath(Path.Combine(project.ProjectDir, project.StateDir));

    public static string StateFile(ProjectProfile project) => Path.Combine(StateDir(project), "state.json");

    public static string EventsFile(ProjectProfile project) => Path.Combine(StateDir(project), "events.jsonl");

    public static string RunLockFile(ProjectProfile project) => Path.Combine(StateDir(project), "run.lock");

    public static string CompletedDir(ProjectProfile project) => Path.Combine(StateDir(project), "completed");
}
