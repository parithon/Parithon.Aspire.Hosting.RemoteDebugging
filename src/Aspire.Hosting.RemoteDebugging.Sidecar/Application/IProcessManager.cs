using Aspire.Hosting.RemoteDebugging.Sidecar.Domain;

namespace Aspire.Hosting.RemoteDebugging.Sidecar.Application;

/// <summary>
/// Manages the lifecycle of all <see cref="ManagedProcess"/> instances on the remote host.
/// </summary>
internal interface IProcessManager
{
  /// <summary>
  /// Starts a new managed process with the given logical <paramref name="name"/>.
  /// If a process with that name is already running, returns its PID and
  /// <c>alreadyRunning = true</c> without spawning a second instance.
  /// </summary>
  Task<(long Pid, bool AlreadyRunning)> StartProcessAsync(
    string name,
    string workingDirectory,
    string entryPoint,
    IReadOnlyDictionary<string, string> environment,
    CancellationToken cancellationToken);

  /// <summary>
  /// Stops the process identified by <paramref name="name"/>.
  /// Returns <see langword="false"/> if no such process is registered.
  /// </summary>
  Task<bool> StopProcessAsync(string name, CancellationToken cancellationToken);

  /// <summary>Returns a snapshot list of all registered processes and their states.</summary>
  IReadOnlyList<(string Name, long Pid, ProcessState State)> ListProcesses();

  /// <summary>
  /// Returns the <see cref="ILogBuffer"/> for the named process, or <see langword="null"/>
  /// if no process with that name exists.
  /// </summary>
  ILogBuffer? GetLogBuffer(string name);

  /// <summary>
  /// Returns all registered <see cref="ManagedProcess"/> instances.
  /// Used by <see cref="Infrastructure.ConnectionMonitor"/> and
  /// <see cref="Infrastructure.LogCachePersistence"/> during shutdown.
  /// </summary>
  IReadOnlyList<ManagedProcess> GetAllProcesses();
}
