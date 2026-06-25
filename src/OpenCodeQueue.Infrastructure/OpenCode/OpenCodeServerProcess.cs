using System.Diagnostics;
using OpenCodeQueue.Core.Configuration;

namespace OpenCodeQueue.Infrastructure.OpenCode;

public interface IOpenCodeServerProcess : IAsyncDisposable
{
    bool HasExited { get; }
}

public interface IOpenCodeServerProcessFactory
{
    IOpenCodeServerProcess Start(ProjectProfile project, int port);
}

public sealed class DefaultOpenCodeServerProcessFactory : IOpenCodeServerProcessFactory
{
    public IOpenCodeServerProcess Start(ProjectProfile project, int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = project.OpenCodeOverrides.OpenCodeExecutable,
            WorkingDirectory = project.ProjectDir,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--hostname");
        startInfo.ArgumentList.Add("127.0.0.1");

        if (!string.IsNullOrWhiteSpace(project.OpenCodeOverrides.ServerPassword))
        {
            startInfo.Environment["OPENCODE_SERVER_PASSWORD"] = project.OpenCodeOverrides.ServerPassword;
            startInfo.Environment["OPENCODE_SERVER_USERNAME"] = project.OpenCodeOverrides.ServerUsername;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить процесс opencode serve.");
        return new DefaultOpenCodeServerProcess(process);
    }
}

internal sealed class DefaultOpenCodeServerProcess(Process process) : IOpenCodeServerProcess
{
    public bool HasExited => process.HasExited;

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        process.Dispose();
    }
}
