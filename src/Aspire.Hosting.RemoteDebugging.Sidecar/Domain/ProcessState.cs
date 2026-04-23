namespace Aspire.Hosting.RemoteDebugging.Sidecar.Domain;

/// <summary>Lifecycle states for a <see cref="ManagedProcess"/>.</summary>
internal enum ProcessState
{
  Starting,
  Running,
  Stopping,
  Stopped,
  Failed
}
