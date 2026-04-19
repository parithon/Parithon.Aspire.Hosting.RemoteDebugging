using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging;

internal interface IRemoteHostTransport : IDisposable
{
  Task ConnectAsync(RemoteHostResource resource, ILogger logger, CancellationToken cancellationToken);
  Task DisconnectAsync(ILogger logger, CancellationToken cancellationToken);
  Task<RemoteDebuggerInstallationResult> InstallRemoteDebugger(ILogger logger, CancellationToken cancellationToken);
  Task<bool> StartRemoteDebugger(ILogger logger, CancellationToken cancellationToken);
}
