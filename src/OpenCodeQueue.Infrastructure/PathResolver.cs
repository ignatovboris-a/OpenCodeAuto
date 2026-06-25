namespace OpenCodeQueue.Infrastructure;

internal static class PathResolver
{
    public static string Resolve(string path, string baseDir)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));
    }

    public static string ResolveProjectPath(string? path, string projectDir, string defaultPath = ".")
    {
        return Resolve(string.IsNullOrWhiteSpace(path) ? defaultPath : path, projectDir);
    }

    public static bool AreSamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalize(left), Normalize(right), comparison);
    }

    private static string Normalize(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
