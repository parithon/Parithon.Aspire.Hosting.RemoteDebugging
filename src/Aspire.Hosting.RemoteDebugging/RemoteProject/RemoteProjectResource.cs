using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost;
using Aspire.Hosting.RemoteDebugging.RemoteProject.HealthChecks;

namespace Aspire.Hosting.RemoteDebugging.RemoteProject;

public sealed class RemoteProjectResource<TProject>(string name, RemoteHostResource host) : Resource(name), 
  IResourceWithParent<RemoteHostResource> where TProject : IProjectMetadata
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
}
