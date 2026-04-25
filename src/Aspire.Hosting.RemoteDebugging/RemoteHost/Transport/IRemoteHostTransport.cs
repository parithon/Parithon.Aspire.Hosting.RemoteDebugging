using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;

/// <summary>Describes whether the sidecar binary on the remote host needs to be (re)deployed.</summary>
internal enum SidecarDeploymentStatus
{
  /// <summary>File exists and its timestamp matches the local build — no action required.</summary>
  UpToDate,

  /// <summary>File exists but its timestamp differs from the local build — redeploy needed.</summary>
  Outdated,

  /// <summary>File is absent on the remote host — first-time deploy needed.</summary>
  NotDeployed,
}

internal interface IRemoteHostTransport : IDisposable
{
  /// <summary>Raised when the SSH connection is lost unexpectedly (not during intentional disconnect).</summary>
  event EventHandler? ConnectionDropped;

  /// <summary>Raised when the vsdbg process exits unexpectedly (not during intentional disconnect).</summary>
  event EventHandler? RemoteDebuggerExited;

  /// <summary>
  /// The gRPC channel connected to the sidecar agent on the remote host via the SSH tunnel.
  /// <see langword="null"/> until <see cref="StartSidecarAsync"/> completes successfully.
  /// </summary>
  GrpcChannel? SidecarChannel { get; }

  /// <summary>
  /// The OTLP endpoint that remote processes should use to export telemetry.
  /// Points to the reverse SSH tunnel that forwards data back to the AppHost's OTLP collector.
  /// <see langword="null"/> if no OTLP endpoint is configured on the AppHost, or until
  /// <see cref="StartOtelTunnelAsync"/> completes successfully.
  /// </summary>
  Uri? OtelTunnelEndpoint { get; }

  /// <summary>
  /// The value of the <c>OTEL_EXPORTER_OTLP_HEADERS</c> environment variable read from the
  /// AppHost process (e.g. <c>x-otlp-api-key=&lt;uuid&gt;</c>). Forwarded verbatim to remote
  /// processes so they can authenticate with the Aspire dashboard's OTLP endpoint.
  /// <see langword="null"/> if the AppHost has no OTLP headers configured.
  /// </summary>
  string? OtelTunnelHeaders { get; }

  Task ConnectAsync(RemoteHostResource resource, ILogger logger, CancellationToken cancellationToken);

  /// <summary>
  /// Closes the transport connection to the remote host.
  /// When <paramref name="sendShutdown"/> is <see langword="true"/> (explicit user-initiated
  /// disconnect), sends a Shutdown RPC to the sidecar so it stops all managed processes and exits
  /// cleanly. When <see langword="false"/> (AppHost graceful exit or restart), only the SSH tunnel
  /// is closed; the sidecar remains alive so a restarted AppHost can reconnect and re-attach to
  /// any running remote processes via <c>TryReconnectAsync</c>.
  /// </summary>
  Task DisconnectAsync(ILogger logger, CancellationToken cancellationToken, bool sendShutdown = false);
  Task<RemoteDebuggerInstallationResult> InstallRemoteDebugger(ILogger logger, CancellationToken cancellationToken);
  Task<bool> StartRemoteDebugger(ILogger logger, CancellationToken cancellationToken);

  /// <summary>
  /// Checks whether the sidecar binary on the remote host is present and matches the local build
  /// timestamp (within a 2-second tolerance).
  /// </summary>
  Task<SidecarDeploymentStatus> SidecarDeployedAsync(RemoteHostResource resource, CancellationToken cancellationToken);

  /// <summary>
  /// If an outdated sidecar is still running on the remote host, connects to it via a temporary
  /// gRPC tunnel and calls <c>Shutdown</c>. Falls back to an SSH kill command if the gRPC call
  /// fails or the sidecar is unresponsive. Must be called before redeploying an outdated sidecar
  /// to avoid a locked-DLL error on the next deploy.
  /// </summary>
  Task ShutdownRunningSidecarAsync(RemoteHostResource resource, ILogger logger, CancellationToken cancellationToken);

  /// <summary>
  /// Sets up an SSH local port forward to the sidecar's gRPC port and establishes the gRPC channel.
  /// <para>
  /// When <paramref name="isReconnect"/> is <see langword="false"/> (fresh AppHost session): if the
  /// sidecar is already running, calls <c>Reset</c> to stop leftover processes from a previous session.
  /// </para>
  /// <para>
  /// When <paramref name="isReconnect"/> is <see langword="true"/> (SSH dropped and re-established):
  /// reconnects to the already-running sidecar without resetting state. Callers are responsible for
  /// re-subscribing to <c>StreamLogs(replay_cached: true)</c> to retrieve logs produced while offline.
  /// </para>
  /// </summary>
  Task StartSidecarAsync(RemoteHostResource resource, ILogger logger, bool isReconnect, CancellationToken cancellationToken);

  /// <summary>
  /// Sets up a reverse SSH port forward that binds a port on the remote machine and tunnels
  /// OTLP traffic back to the AppHost's OTLP collector endpoint.
  /// <para>
  /// <paramref name="otlpEndpointUrl"/> is the URL of the AppHost's OTLP gRPC endpoint (e.g.
  /// <c>http://localhost:18889</c>). When <see langword="null"/> or empty the tunnel is skipped.
  /// </para>
  /// <para>
  /// <paramref name="otlpApiKey"/> is the raw value for the <c>OTEL_EXPORTER_OTLP_HEADERS</c>
  /// environment variable (e.g. <c>x-otlp-api-key=&lt;uuid&gt;</c>). May be <see langword="null"/>
  /// if the dashboard is not using API-key authentication.
  /// </para>
  /// <para>
  /// After this call <see cref="OtelTunnelEndpoint"/> contains the URI that remote processes
  /// should set as <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>.
  /// </para>
  /// <para>
  /// When the OTLP endpoint uses HTTPS, the AppHost's ASP.NET Core development certificate is
  /// exported (public key only), uploaded to the remote host via SFTP, and installed into the
  /// machine certificate store so that the remote .NET process can validate TLS through the tunnel.
  /// This requires the SSH user to have administrator (Windows) or root/sudo (Linux) privileges.
  /// </para>
  /// </summary>
  Task StartOtelTunnelAsync(string? otlpEndpointUrl, string? otlpApiKey, ILogger logger, CancellationToken cancellationToken);

  /// <summary>
  /// Compares the <c>LastWriteTimeUtc</c> of a local DLL against the corresponding
  /// remote file over SFTP using a ±2-second tolerance.
  /// Returns <see langword="false"/> when the remote file is absent or the timestamps differ.
  /// </summary>
  Task<bool> IsProjectCurrentAsync(string localDllPath, string remoteDllPath, CancellationToken cancellationToken);

  Task<ResourceHealthCheckResult> CheckSidecarHealthAsync(ILogger logger, CancellationToken cancellationToken);

  /// <summary>Checks the health of the vsdbg remote debugger process.</summary>
  Task<ResourceHealthCheckResult> CheckVsdbgHealthAsync(ILogger logger, CancellationToken cancellationToken);

  /// <summary>
  /// Copies all files from <paramref name="localDirectory"/> to <paramref name="remoteDirectory"/>
  /// on the remote host over SFTP. The remote directory is cleaned before upload to prevent stale artifacts.
  /// </summary>
  Task DeployDirectoryAsync(string localDirectory, string remoteDirectory, ILogger logger, CancellationToken cancellationToken);

  /// <summary>
  /// Runs an SSH command on the remote host and returns its exit code, stdout, and stderr.
  /// The command is executed as-is (no shell wrapping) — callers are responsible for any
  /// shell-specific quoting.
  /// </summary>
  Task<(int ExitCode, string Output, string Error)> ExecuteSshCommandAsync(string command, CancellationToken cancellationToken);

  /// <summary>
  /// Uploads a text file to the remote host via SFTP, creating any parent directories as needed.
  /// </summary>
  Task UploadTextAsync(string content, string remotePath, CancellationToken cancellationToken);

  /// <summary>
  /// Sets up a reverse SSH port forward: sshd on the remote host binds a dynamically-allocated
  /// port and tunnels every incoming connection back to <paramref name="localHost"/>:<paramref name="localPort"/>
  /// on the dev machine.
  /// <para>
  /// This is used to make dev-host resource endpoints (e.g. a local web API) reachable from
  /// processes running on the remote host when a remote project references them via
  /// <c>WithReference</c>.
  /// </para>
  /// </summary>
  /// <returns>
  /// The port bound on the remote host (the value the remote process should connect to on
  /// <c>127.0.0.1</c>), or <c>0</c> if the tunnel could not be established.
  /// </returns>
  Task<uint> StartEndpointTunnelAsync(string localHost, uint localPort, ILogger logger, CancellationToken cancellationToken);
}
