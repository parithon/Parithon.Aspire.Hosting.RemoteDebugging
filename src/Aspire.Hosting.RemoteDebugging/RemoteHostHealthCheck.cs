using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging;

internal sealed class RemoteHostHealthCheck(RemoteHostResource resource, ILogger<RemoteHostHealthCheck> logger) : IHealthCheck
{
  public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
  {
    // Always read the annotation fresh so reconnect/disconnect is reflected correctly.
    if (!resource.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var transportAnnotation) || transportAnnotation is null)
    {
      return HealthCheckResult.Unhealthy("Not connected to remote host.");
    }

    var result = await transportAnnotation.Transport.CheckHealthAsync(logger, cancellationToken).ConfigureAwait(false);

    return result.Status switch
    {
      ResourceHealthStatus.Healthy   => HealthCheckResult.Healthy(result.Description),
      ResourceHealthStatus.Unhealthy => HealthCheckResult.Unhealthy(result.Description),
      _                              => HealthCheckResult.Degraded(result.Description)
    };
  }
}
