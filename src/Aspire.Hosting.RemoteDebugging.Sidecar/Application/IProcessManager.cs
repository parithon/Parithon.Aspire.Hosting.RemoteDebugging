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
    CancellationToken cancellationToken,
    string? executable = null);

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
  /// Stops all managed processes and removes them from the registry.
  /// Returns the number of processes that were stopped.
  /// Used by <see cref="Infrastructure.ConnectionMonitor"/> during shutdown and
  /// by the <c>Reset</c> RPC when a new AppHost session reconnects to an existing sidecar.
  /// </summary>
  Task<int> StopAllAsync(CancellationToken cancellationToken);

  /// <summary>
  /// Returns all registered <see cref="ManagedProcess"/> instances.
  /// Used by <see cref="Infrastructure.ConnectionMonitor"/> and
  /// <see cref="Infrastructure.LogCachePersistence"/> during shutdown.
  /// </summary>
  IReadOnlyList<ManagedProcess> GetAllProcesses();
}
