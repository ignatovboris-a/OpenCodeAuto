namespace OpenCodeQueue.Core.Configuration;

public enum OpenCodeMode
{
    Server,
    Cli
}

public enum PromptTransport
{
    Auto,
    Inline,
    FileAttachment
}

public enum ConsoleVerbosity
{
    Quiet,
    Normal,
    Detailed
}

public sealed record OpenCodeSettings
{
    public OpenCodeMode OpenCodeMode { get; init; } = OpenCodeMode.Server;

    public string OpenCodeExecutable { get; init; } = "opencode";

    public string ServerUrl { get; init; } = "http://localhost:4096";

    public bool ManageOpenCodeServer { get; init; } = true;

    public string? ServerPassword { get; init; }

    public string ServerUsername { get; init; } = "opencode";

    public string? Model { get; init; }

    public string? Agent { get; init; }

    public PromptTransport PromptTransport { get; init; } = PromptTransport.Auto;

    public int MaxInlinePromptChars { get; init; } = 24_000;

    public ConsoleVerbosity ConsoleVerbosity { get; init; } = ConsoleVerbosity.Normal;

    public OpenCodeSettings Redacted() => this with { ServerPassword = ServerPassword is null ? null : "***" };

    public override string ToString()
    {
        var password = ServerPassword is null ? "null" : "***";
        return $"OpenCodeSettings {{ OpenCodeMode = {OpenCodeMode}, OpenCodeExecutable = {OpenCodeExecutable}, ServerUrl = {ServerUrl}, ManageOpenCodeServer = {ManageOpenCodeServer}, ServerPassword = {password}, ServerUsername = {ServerUsername}, Model = {Model}, Agent = {Agent}, PromptTransport = {PromptTransport}, MaxInlinePromptChars = {MaxInlinePromptChars}, ConsoleVerbosity = {ConsoleVerbosity} }}";
    }
}
