using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;

internal sealed class RemoteSidecarHealthCheck(RemoteHostResource resource, ILogger<RemoteSidecarHealthCheck> logger) : IHealthCheck
{
  public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
  {
    if (!resource.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var transportAnnotation) || transportAnnotation is null)
      return HealthCheckResult.Unhealthy("Not connected to remote host.");

    var result = await transportAnnotation.Transport
      .CheckSidecarHealthAsync(logger, cancellationToken)
      .ConfigureAwait(false);

    return result.Status switch
    {
      ResourceHealthStatus.Healthy   => HealthCheckResult.Healthy(result.Description),
      ResourceHealthStatus.Unhealthy => HealthCheckResult.Unhealthy(result.Description),
      _                              => HealthCheckResult.Degraded(result.Description)
    };
  }
}
