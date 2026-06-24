namespace OpenCodeQueue.Cli.Commands;

public sealed record CliCommand(string Name, string? SubCommand, string? Argument, string ConfigPath, string? ProjectId, bool Once, bool HelpRequested)
{
    public static CliCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new CliCommand("menu", null, null, "opencode-queue.json", null, false, false);
        }

        var helpRequested = args.Any(arg => arg is "--help" or "-h");
        var configPath = ReadOption(args, "--config") ?? "opencode-queue.json";
        var projectId = ReadOption(args, "--project");
        var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);

        var positional = ReadPositionals(args);
        var name = helpRequested && positional.Length == 0 ? "help" : positional.ElementAtOrDefault(0) ?? "menu";
        var subCommand = positional.ElementAtOrDefault(1);
        var argument = positional.ElementAtOrDefault(2);

        if (string.Equals(name, "project", StringComparison.OrdinalIgnoreCase) && string.Equals(subCommand, "select", StringComparison.OrdinalIgnoreCase) && argument is null)
        {
            argument = projectId;
        }

        return new CliCommand(name, subCommand, argument, configPath, projectId, once, helpRequested);
    }

    private static string? ReadOption(IReadOnlyList<string> args, string optionName)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
            {
                var value = args[index + 1];
                return value.StartsWith("-", StringComparison.Ordinal) ? null : value;
            }
        }

        return null;
    }

    private static string[] ReadPositionals(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var current = args[index];
            if (string.Equals(current, "--config", StringComparison.OrdinalIgnoreCase) || string.Equals(current, "--project", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (current.StartsWith("--", StringComparison.Ordinal) || current is "-h")
            {
                continue;
            }

            result.Add(current);
        }

        return result.ToArray();
    }
}
