using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RemoteDebugging.RemoteHost.HealthChecks;

namespace Aspire.Hosting.RemoteDebugging.RemoteHost.Annotations;

/// <summary>
/// Annotation to store the health check provider for a remote host resource.
/// </summary>
/// <remarks>
/// This annotation is no longer used internally. Health checks are now registered
/// via Aspire's built-in <see cref="HealthCheckAnnotation"/> and displayed in the
/// Aspire dashboard "Health checks" section.
/// </remarks>
[Obsolete("RemoteHostHealthCheckAnnotation is no longer used. Health checks are registered automatically via AddRemoteHost using Aspire's built-in HealthCheckAnnotation.")]
public sealed class RemoteHostHealthCheckAnnotation(Func<Task<ResourceHealthCheckResult>> healthCheckFunc) : IResourceAnnotation
{
  /// <summary>
  /// Gets the health check function.
  /// </summary>
  public Func<Task<ResourceHealthCheckResult>> HealthCheckFunc { get; } = healthCheckFunc;
}

