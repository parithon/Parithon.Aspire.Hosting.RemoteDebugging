using Aspire.Hosting.ApplicationModel;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Transport;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;

internal sealed class RemoteHostHealthCheck(RemoteHostResource resource, ILogger<RemoteHostHealthCheck> logger) : IHealthCheck
{
  public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
  {
    // Always read the annotation fresh so reconnect/disconnect is reflected correctly.
    if (!resource.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var transportAnnotation) || transportAnnotation is null)
    {
      return HealthCheckResult.Unhealthy("Not connected to remote host.");
    }

    var result = await transportAnnotation.Transport.CheckSidecarHealthAsync(logger, cancellationToken).ConfigureAwait(false);

    return result.Status switch
    {
      ResourceHealthStatus.Healthy   => HealthCheckResult.Healthy(result.Description),
      ResourceHealthStatus.Unhealthy => HealthCheckResult.Unhealthy(result.Description),
      _                              => HealthCheckResult.Degraded(result.Description)
    };
  }
}
