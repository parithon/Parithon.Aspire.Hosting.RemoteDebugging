using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteProject.HealthChecks;

namespace Aspire.Hosting.RemoteDebugging.RemoteProject;

public sealed class RemoteProjectResource<TProject>(string name, RemoteHostResource host) : Resource(name), 
  IResourceWithParent<RemoteHostResource>, IResourceWithEnvironment where TProject : IProjectMetadata
{
  private readonly RemoteHostResource _host = host;
  public RemoteHostResource Parent => _host;

  /// <summary>Serializes concurrent run/stop operations for this resource.</summary>
  internal SemaphoreSlim RunGate { get; } = new SemaphoreSlim(1, 1);

  private CancellationTokenSource? _runCts;
  private readonly object _runCtsLock = new();

  /// <summary>Cancels any in-flight run and returns a new token for the next run.</summary>
  internal CancellationToken CreateRunToken(CancellationToken appCancellationToken)
  {
    lock (_runCtsLock)
    {
      _runCts?.Cancel();
      // Do not dispose immediately — the old run may still be unwinding with the token.
      _runCts = CancellationTokenSource.CreateLinkedTokenSource(appCancellationToken);
      return _runCts.Token;
    }
  }

  /// <summary>Cancels any in-flight run without starting a new one.</summary>
  internal void CancelRun()
  {
    lock (_runCtsLock) { _runCts?.Cancel(); }
  }

  /// <summary>
  /// The local directory containing the build artifacts for the current run.
  /// Set by <c>BuildAsync</c> and consumed by <c>DeployAsync</c>.
  /// </summary>
  internal string? BuildOutputPath { get; set; }

  /// <summary>
  /// The absolute SFTP path on the remote host where build artifacts were deployed.
  /// Set by <c>DeployAsync</c> and consumed by <c>StartAsync</c>.
  /// </summary>
  internal string? RemoteDeploymentPath { get; set; }

  /// <summary>
  /// The assembly name (without extension) used as the process entry-point DLL.
  /// Parsed from the <c>&lt;AssemblyName&gt;</c> element in the <c>.csproj</c>;
  /// falls back to the project file name. Set by <c>BuildAsync</c>.
  /// </summary>
  internal string? AssemblyName { get; set; }

  /// <summary>
  /// The PID of the managed process running on the remote host.
  /// Set by <c>StartAsync</c> after the sidecar launches the process.
  /// </summary>
  internal long? RemoteProcessId { get; set; }

  /// <summary>
  /// Additional environment variables injected into the remote process.
  /// Populated via <c>WithEnvironment</c>; merged with system defaults at start time.
  /// </summary>
  internal Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.Ordinal);
}
