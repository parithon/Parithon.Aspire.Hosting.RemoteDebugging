using System.Diagnostics;
using Aspire.Hosting.RemoteDebugging.Sidecar.Application;

namespace Aspire.Hosting.RemoteDebugging.Sidecar.Domain;

/// <summary>
/// Aggregate root that owns a single child process and its <see cref="ILogBuffer"/>.
/// Responsible for start/stop lifecycle and continuous stdout/stderr capture.
/// </summary>
internal sealed class ManagedProcess : IAsyncDisposable
{
  private readonly ILogger _logger;
  private Process? _process;
  private CancellationTokenSource? _readerCts;

  public string Name { get; }
  public long Pid { get; private set; }
  public ProcessState State { get; private set; } = ProcessState.Stopped;
  public ILogBuffer LogBuffer { get; }

  public ManagedProcess(string name, ILogBuffer logBuffer, ILogger logger)
  {
    Name      = name;
    LogBuffer = logBuffer;
    _logger   = logger;
  }

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Starts <c>dotnet <paramref name="entryPoint"/></c> in the given working directory with
  /// the supplied environment variables.  Kicks off background reader tasks for stdout/stderr
  /// and a process-exit monitor.
  /// </summary>
  /// <exception cref="InvalidOperationException">
  /// Thrown if the process is already in a <see cref="ProcessState.Starting"/> or
  /// <see cref="ProcessState.Running"/> state.
  /// </exception>
  public Task StartAsync(
    string workingDirectory,
    string entryPoint,
    IReadOnlyDictionary<string, string> environment,
    CancellationToken cancellationToken)
  {
    if (State is ProcessState.Starting or ProcessState.Running)
      throw new InvalidOperationException(
        $"Process '{Name}' is already in state '{State}' and cannot be started again.");

    State = ProcessState.Starting;

    var psi = new ProcessStartInfo("dotnet", entryPoint)
    {
      WorkingDirectory       = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError  = true,
      UseShellExecute        = false,
      CreateNoWindow         = true
    };

    foreach (var (key, value) in environment)
      psi.Environment[key] = value;

    _process = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start process '{Name}' — Process.Start returned null.");

    Pid   = _process.Id;
    State = ProcessState.Running;

    _logger.LogInformation("Started '{Name}' with PID {Pid}.", Name, Pid);

    _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    // Fire-and-forget readers — errors are swallowed (logged at Debug) so they never
    // surface as unobserved task exceptions.
    _ = ReadOutputAsync(_process.StandardOutput, isError: false, _readerCts.Token);
    _ = ReadOutputAsync(_process.StandardError,  isError: true,  _readerCts.Token);
    _ = MonitorExitAsync(_process, _readerCts.Token);

    return Task.CompletedTask;
  }

  /// <summary>
  /// Terminates the child process tree and waits for it to exit.
  /// No-op if the process is not currently running.
  /// </summary>
  public async Task StopAsync(CancellationToken cancellationToken)
  {
    if (State is not ProcessState.Running)
      return;

    State = ProcessState.Stopping;
    _logger.LogInformation("Stopping '{Name}' (PID {Pid}).", Name, Pid);

    try
    {
      if (_process is { HasExited: false })
      {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
      }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      _logger.LogWarning(ex, "Error while stopping process '{Name}'.", Name);
    }
    finally
    {
      State = ProcessState.Stopped;
      _readerCts?.Cancel();
    }
  }

  public async ValueTask DisposeAsync()
  {
    _readerCts?.Cancel();
    _readerCts?.Dispose();

    if (_process is not null)
    {
      try { _process.Kill(entireProcessTree: true); }
      catch { /* best-effort */ }

      _process.Dispose();
    }

    if (LogBuffer is IDisposable disposable)
      disposable.Dispose();

    await ValueTask.CompletedTask;
  }

  // ── Private helpers ───────────────────────────────────────────────────────

  private async Task ReadOutputAsync(StreamReader reader, bool isError, CancellationToken cancellationToken)
  {
    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null) break; // EOF — process has closed the stream
        LogBuffer.Append(line, isError);
      }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      _logger.LogDebug(ex, "Output reader for '{Name}' terminated.", Name);
    }
  }

  private async Task MonitorExitAsync(Process process, CancellationToken cancellationToken)
  {
    try
    {
      await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

      State = ProcessState.Stopped;
      _logger.LogInformation(
        "Process '{Name}' (PID {Pid}) exited with code {ExitCode}.",
        Name, Pid, process.ExitCode);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      State = ProcessState.Failed;
      _logger.LogError(ex, "Process '{Name}' exited unexpectedly.", Name);
    }
  }
}
