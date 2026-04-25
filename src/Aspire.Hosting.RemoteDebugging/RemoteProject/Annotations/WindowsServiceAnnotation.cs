using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.RemoteDebugging.RemoteProject.Annotations;

/// <summary>
/// Marks a <see cref="RemoteProjectResource{TProject}"/> for deployment as a Windows Service
/// on the remote host. When present, the resource lifecycle changes to:
/// install → start (ephemeral — service is removed on AppHost stop or crash recovery).
/// </summary>
/// <remarks>
/// Console log capture is not supported for Windows Services — services have no stdout/stderr.
/// </remarks>
public sealed class WindowsServiceAnnotation(string serviceName) : IResourceAnnotation
{
  /// <summary>The SCM service name (used with <c>sc.exe</c> and <c>New-Service</c>).</summary>
  public string ServiceName { get; } = serviceName;

  /// <summary>Optional display name shown in the Windows Services MMC snap-in.</summary>
  public string? DisplayName { get; init; }

  /// <summary>Optional description shown in the Windows Services MMC snap-in.</summary>
  public string? Description { get; init; }
}
