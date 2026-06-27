namespace OpenCodeQueue.Core.Configuration;

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

public enum PermissionPolicy
{
    Manual,
    AutoApprove
}

public sealed record OpenCodeSettings
{
    public string ServerUrl { get; init; } = "http://localhost:4096";

    public string? ServerPassword { get; init; }

    public string ServerUsername { get; init; } = "opencode";

    public string Provider { get; init; } = "openai";

    public string Model { get; init; } = "gpt-5.5";

    public string ReasoningEffort { get; init; } = "high";

    public string? Agent { get; init; }

    public PromptTransport PromptTransport { get; init; } = PromptTransport.Auto;

    public int MaxInlinePromptChars { get; init; } = 24_000;

    public ConsoleVerbosity ConsoleVerbosity { get; init; } = ConsoleVerbosity.Normal;

    public ResilienceSettings Resilience { get; init; } = new();

    public OpenCodeSettings Redacted() => this with { ServerPassword = ServerPassword is null ? null : "***" };

    public override string ToString()
    {
        var password = ServerPassword is null ? "null" : "***";
        return $"OpenCodeSettings {{ ServerUrl = {ServerUrl}, ServerPassword = {password}, ServerUsername = {ServerUsername}, Provider = {Provider}, Model = {Model}, ReasoningEffort = {ReasoningEffort}, Agent = {Agent}, PromptTransport = {PromptTransport}, MaxInlinePromptChars = {MaxInlinePromptChars}, ConsoleVerbosity = {ConsoleVerbosity}, Resilience = {Resilience} }}";
    }
}

public sealed record ResilienceSettings
{
    public bool Enabled { get; init; } = true;

    public int StepTimeoutMinutes { get; init; } = 90;

    public int IdleTimeoutMinutes { get; init; } = 20;

    public int MaxContinuationAttemptsPerStep { get; init; } = 5;

    public int MaxTransportRetriesPerAttempt { get; init; } = 3;

    public int RetryDelaySeconds { get; init; } = 15;

    public double RetryBackoffMultiplier { get; init; } = 2.0;

    public int StopAfterSameSignatureRepeats { get; init; } = 3;

    public bool DetectTerminatedText { get; init; } = true;

    public bool RecoverOnToolExecutionAborted { get; init; } = true;

    public bool AutoRespondToRecoverableQuestions { get; init; }

    public PermissionPolicy PermissionPolicy { get; init; } = PermissionPolicy.Manual;

    public string? ContinuationPrompt { get; init; }
}
