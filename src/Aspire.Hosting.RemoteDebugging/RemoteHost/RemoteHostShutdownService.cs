using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
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

    // Disconnect all remote hosts in parallel and send the Shutdown RPC so the
    // sidecar stops its managed processes and exits cleanly.  Use CancellationToken.None
    // so the host's shutdown-deadline token does not abort the graceful Shutdown RPC —
    // DisconnectAsync uses its own internal 10-second timeout for that call.
    await Task.WhenAll(connected.Select(r =>
      RemoteHostConnector.DisconnectAsync(r, notifications, loggers, CancellationToken.None, sendShutdown: true)))
      .ConfigureAwait(false);
  }
}
