using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

internal interface IRemoteHostTransport : IDisposable
{
  /// <summary>Raised when the SSH connection is lost unexpectedly (not during intentional disconnect).</summary>
  event EventHandler? ConnectionDropped;

  /// <summary>Raised when the vsdbg process exits unexpectedly (not during intentional disconnect).</summary>
  event EventHandler? RemoteDebuggerExited;

  Task ConnectAsync(RemoteHostResource resource, ILogger logger, CancellationToken cancellationToken);
  Task DisconnectAsync(ILogger logger, CancellationToken cancellationToken);
  Task<RemoteDebuggerInstallationResult> InstallRemoteDebugger(ILogger logger, CancellationToken cancellationToken);
  Task<bool> StartRemoteDebugger(ILogger logger, CancellationToken cancellationToken);
  Task<ResourceHealthCheckResult> CheckHealthAsync(ILogger logger, CancellationToken cancellationToken);
}
