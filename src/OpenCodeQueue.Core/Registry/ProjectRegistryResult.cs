using OpenCodeQueue.Core.Configuration;

namespace OpenCodeQueue.Core.Registry;

public sealed record ProjectRegistryResult(bool IsSuccess, string? Message = null, ProjectProfile? Project = null)
{
    public static ProjectRegistryResult Success(string? message = null, ProjectProfile? project = null) => new(true, message, project);

    public static ProjectRegistryResult Failure(string message) => new(false, message);
}
