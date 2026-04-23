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

  /// <summary>
  /// Copies all files from <paramref name="localDirectory"/> to <paramref name="remoteDirectory"/>
  /// on the remote host over SFTP. The remote directory is cleaned before upload to prevent stale artifacts.
  /// </summary>
  Task DeployDirectoryAsync(string localDirectory, string remoteDirectory, ILogger logger, CancellationToken cancellationToken);

  /// <summary>
  /// Copies the aspire-sidecar artifacts from <paramref name="localSidecarDir"/> into the
  /// remote tools directory alongside vsdbg. Does not remove existing files.
  /// </summary>
  Task DeploySidecarAsync(string localSidecarDir, ILogger logger, CancellationToken cancellationToken);

  /// <summary>
  /// Starts the aspire-sidecar daemon on the remote host as a background process.
  /// If the daemon is already running the call is a no-op and returns <see langword="true"/>.
  /// Returns <see langword="false"/> if the daemon could not be started.
  /// </summary>
  Task<bool> StartSidecarDaemonAsync(ILogger logger, CancellationToken cancellationToken);

  /// <summary>
  /// Returns a health check result indicating whether the aspire-sidecar daemon is running
  /// on the remote host. Uses a lightweight SFTP socket-file existence check.
  /// </summary>
  Task<ResourceHealthCheckResult> CheckSidecarHealthAsync(ILogger logger, CancellationToken cancellationToken);
}
