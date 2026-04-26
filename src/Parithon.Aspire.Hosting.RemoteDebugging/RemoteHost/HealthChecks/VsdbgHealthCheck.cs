using Aspire.Hosting.ApplicationModel;
using Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Parithon.Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;

internal sealed class VsdbgHealthCheck(RemoteHostResource resource, ILogger<VsdbgHealthCheck> logger) : IHealthCheck
{
  public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
  {
    if (!resource.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var transportAnnotation) || transportAnnotation is null)
    {
      return HealthCheckResult.Unhealthy("Not connected to remote host.");
    }

    var result = await transportAnnotation.Transport.CheckVsdbgHealthAsync(logger, cancellationToken).ConfigureAwait(false);

    return result.Status switch
    {
      ResourceHealthStatus.Healthy   => HealthCheckResult.Healthy(result.Description),
      ResourceHealthStatus.Unhealthy => HealthCheckResult.Unhealthy(result.Description),
      _                              => HealthCheckResult.Degraded(result.Description)
    };
  }
}
