using System.Collections.Concurrent;
using Parithon.Aspire.Hosting.RemoteDebugging.Sidecar.Domain;
using Microsoft.Extensions.Options;

namespace Parithon.Aspire.Hosting.RemoteDebugging.Sidecar.Application;

/// <summary>
/// Singleton service that creates, tracks, and stops all <see cref="ManagedProcess"/> instances.
/// </summary>
internal sealed class ProcessManagerService(
  ILogger<ProcessManagerService> logger,
  ILoggerFactory loggerFactory,
  IOptions<SidecarOptions> options) : IProcessManager, IAsyncDisposable
{
  private readonly ConcurrentDictionary<string, ManagedProcess> _processes =
    new(StringComparer.Ordinal);

  /// <inheritdoc/>
  public async Task<(long Pid, bool AlreadyRunning)> StartProcessAsync(
    string name,
    string workingDirectory,
    string entryPoint,
    IReadOnlyDictionary<string, string> environment,
    CancellationToken cancellationToken,
    string? executable = null)
  {
    // Return early if a running process with this name already exists.
    if (_processes.TryGetValue(name, out var existing) && existing.State is ProcessState.Running)
    {
      logger.LogInformation("Process '{Name}' is already running (PID {Pid}).", name, existing.Pid);
      return (existing.Pid, AlreadyRunning: true);
    }

    // Dispose any stale (stopped/failed) entry before creating a new one.
    if (_processes.TryRemove(name, out var stale))
      await stale.DisposeAsync().ConfigureAwait(false);

    var processLogger = loggerFactory.CreateLogger($"Sidecar.Process.{name}");
    var buffer        = new LogBuffer(options.Value.LogCacheRetention);
    var process       = new ManagedProcess(name, buffer, processLogger);

    _processes[name] = process;

    await process.StartAsync(workingDirectory, entryPoint, environment, cancellationToken, executable)
      .ConfigureAwait(false);

    return (process.Pid, AlreadyRunning: false);
  }

  /// <inheritdoc/>
  public async Task<int> StopAllAsync(CancellationToken cancellationToken)
  {
    var count = 0;
    foreach (var key in _processes.Keys.ToList())
    {
      if (!_processes.TryRemove(key, out var process))
        continue;

      try
      {
        await process.StopAsync(cancellationToken).ConfigureAwait(false);
        count++;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Error stopping '{Name}' during StopAll.", process.Name);
      }
      finally
      {
        await process.DisposeAsync().ConfigureAwait(false);
      }
    }
    return count;
  }

  /// <inheritdoc/>
  public async Task<bool> StopProcessAsync(string name, CancellationToken cancellationToken)
  {
    if (!_processes.TryGetValue(name, out var process))
      return false;

    await process.StopAsync(cancellationToken).ConfigureAwait(false);
    return true;
  }

  /// <inheritdoc/>
  public IReadOnlyList<(string Name, long Pid, ProcessState State)> ListProcesses()
    => [.. _processes.Values.Select(p => (p.Name, p.Pid, p.State))];

  /// <inheritdoc/>
  public ILogBuffer? GetLogBuffer(string name)
    => _processes.TryGetValue(name, out var p) ? p.LogBuffer : null;

  /// <inheritdoc/>
  public IReadOnlyList<ManagedProcess> GetAllProcesses()
    => [.. _processes.Values];

  public async ValueTask DisposeAsync()
  {
    foreach (var process in _processes.Values)
      await process.DisposeAsync().ConfigureAwait(false);

    _processes.Clear();
  }
}
