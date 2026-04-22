namespace Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;

/// <summary>
/// Represents the result of a health check for a remote host resource.
/// </summary>
public sealed class ResourceHealthCheckResult
{
  private ResourceHealthCheckResult(ResourceHealthStatus status, string description)
  {
    Status = status;
    Description = description;
  }

  /// <summary>
  /// The health status.
  /// </summary>
  public ResourceHealthStatus Status { get; }

  /// <summary>
  /// A description of the health check result.
  /// </summary>
  public string Description { get; }

  /// <summary>
  /// Creates a healthy result.
  /// </summary>
  public static ResourceHealthCheckResult Healthy(string description = "Healthy") =>
    new(ResourceHealthStatus.Healthy, description);

  /// <summary>
  /// Creates an unhealthy result.
  /// </summary>
  public static ResourceHealthCheckResult Unhealthy(string description = "Unhealthy") =>
    new(ResourceHealthStatus.Unhealthy, description);

  /// <summary>
  /// Creates an unknown result.
  /// </summary>
  public static ResourceHealthCheckResult Unknown(string description = "Unknown") =>
    new(ResourceHealthStatus.Unknown, description);
}

/// <summary>
/// Represents the health status of a remote host resource.
/// </summary>
public enum ResourceHealthStatus
{
  /// <summary>
  /// The resource is healthy and responding.
  /// </summary>
  Healthy,

  /// <summary>
  /// The resource is unhealthy or not responding.
  /// </summary>
  Unhealthy,

  /// <summary>
  /// The health status is unknown.
  /// </summary>
  Unknown
}
