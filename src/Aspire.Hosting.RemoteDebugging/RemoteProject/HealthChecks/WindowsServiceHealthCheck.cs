using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;
using Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteDebugging.RemoteProject.HealthChecks;

/// <summary>
/// Health check for Windows Service resources.
/// Queries the service status via sc.exe query and reports:
/// - Healthy when service is RUNNING
/// - Degraded when service is STOP_PENDING (transitioning)
/// - Unhealthy for all other states (stopped, not found, etc.)
/// </summary>
internal sealed class WindowsServiceHealthCheck<TProject>(
  RemoteProjectResource<TProject> resource,
  ILogger<WindowsServiceHealthCheck<TProject>> logger) : IHealthCheck
  where TProject : IProjectMetadata
{
  public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
  {
    if (!resource.TryGetLastAnnotation<WindowsServiceAnnotation>(out var serviceAnnotation) || serviceAnnotation is null)
    {
      return HealthCheckResult.Unhealthy("Resource is not configured as a Windows Service.");
    }

    if (!resource.TryGetLastAnnotation<RemoteHostTransportAnnotation>(out var transportAnnotation) || transportAnnotation is null)
    {
      return HealthCheckResult.Unhealthy("Not connected to remote host.");
    }

    var transport = transportAnnotation.Transport;
    var sn = serviceAnnotation.ServiceName;

    try
    {
      var result = await transport.ExecuteSshCommandAsync(
        $"sc.exe query {sn}", cancellationToken).ConfigureAwait(false);
      var (exit, output, error) = result;

      if (exit != 0 || output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) < 0)
      {
        // Service not found or not running. Check if it's transitioning.
        if (output.IndexOf("STOP_PENDING", StringComparison.OrdinalIgnoreCase) >= 0)
        {
          return HealthCheckResult.Degraded($"Windows Service '{sn}' is stopping.");
        }

        if (output.IndexOf("START_PENDING", StringComparison.OrdinalIgnoreCase) >= 0)
        {
          return HealthCheckResult.Degraded($"Windows Service '{sn}' is starting.");
        }

        if (output.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0)
        {
          return HealthCheckResult.Unhealthy($"Windows Service '{sn}' is stopped.");
        }

        // Service not found or in an unexpected state.
        logger.LogWarning(
          "Windows Service '{ServiceName}' query returned unexpected status (exit {Exit}): {Output}",
          sn, exit, output.Trim());

        return HealthCheckResult.Unhealthy(
          $"Windows Service '{sn}' is not found or in an unexpected state.");
      }

      return HealthCheckResult.Healthy($"Windows Service '{sn}' is running.");
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to query Windows Service '{ServiceName}' health.", sn);
      return HealthCheckResult.Unhealthy($"Failed to query Windows Service '{sn}': {ex.Message}");
    }
  }
}
