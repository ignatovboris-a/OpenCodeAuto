namespace OpenCodeQueue.Core.OpenCode;

public sealed record OpenCodeSession(string SessionId, string ProjectDir);

public sealed record OpenCodeMessageResult(bool IsSuccess, string? ErrorMessage = null);

public sealed record OpenCodeServerInfo(string ProjectDir, string? Version);
