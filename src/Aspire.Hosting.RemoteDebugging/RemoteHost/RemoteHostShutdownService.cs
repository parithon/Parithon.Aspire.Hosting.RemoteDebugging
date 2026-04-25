using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Aspire.Hosting.RemoteDebugging.RemoteProject;
using Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost;

/// <summary>
/// An <see cref="IHostedService"/> that gracefully disconnects all
/// <see cref="RemoteHostResource"/> instances when the AppHost shuts down.
/// </summary>
/// <remarks>
/// <see cref="IDistributedApplicationLifecycleHook"/> has no stop hook, and
/// <c>ResourceStoppedEvent</c> is only fired for DCP-managed resources — it is
/// never raised for custom resources during <c>aspire stop</c>.  Using
/// <see cref="IHostedService.StopAsync"/> is the correct pattern; the host calls
/// it for every registered service before the process exits.
/// </remarks>
internal sealed class RemoteHostShutdownService(
  DistributedApplicationModel model,
  ResourceNotificationService notifications,
  ResourceLoggerService loggers) : IHostedService
{
  public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  public async Task StopAsync(CancellationToken cancellationToken)
  {
    var connected = model.Resources
      .OfType<RemoteHostResource>()
      .Where(r => r.HasAnnotationOfType<RemoteHostTransportAnnotation>())
      .ToList();

    if (connected.Count == 0)
      return;

    // Phase 1: Stop any Windows Services running as children of each connected host.
    // This must happen BEFORE the SSH transport is closed, as sc.exe runs via SSH.
    // We do this sequentially per host (parallel would complicate error handling).
    foreach (var host in connected)
    {
      if (!host.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var ta)
        || ta?.Transport is not IRemoteHostTransport transport)
        continue;

      var hostLogger = loggers.GetLogger(host);

      var windowsServiceProjects = model.Resources
        .OfType<IResourceWithParent<RemoteHostResource>>()
        .Where(r => r.Parent == host && ((IResource)r).TryGetLastAnnotation<WindowsServiceAnnotation>(out _))
        .ToList();

      foreach (var project in windowsServiceProjects)
      {
        if (!((IResource)project).TryGetLastAnnotation<WindowsServiceAnnotation>(out var svcAnnotation))
          continue;

        try
        {
          using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
          await WindowsServiceRunner.StopAndUninstallAsync(svcAnnotation, transport, hostLogger, stopCts.Token)
            .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
          hostLogger.LogWarning(ex, "Failed to stop Windows Service '{Name}' during shutdown.", project.Name);
        }
      }
    }

    // Phase 2: Disconnect all remote hosts in parallel and send the Shutdown RPC so the
    // sidecar stops its managed processes and exits cleanly.  Use CancellationToken.None
    // so the host's shutdown-deadline token does not abort the graceful Shutdown RPC —
    // DisconnectAsync uses its own internal 10-second timeout for that call.
    await Task.WhenAll(connected.Select(r =>
      RemoteHostConnector.DisconnectAsync(r, notifications, loggers, CancellationToken.None, sendShutdown: true)))
      .ConfigureAwait(false);
  }
}
