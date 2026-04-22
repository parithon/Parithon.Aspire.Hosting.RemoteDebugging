using System.Diagnostics;
using Aspire.Hosting.RemoteDebugging.Sidecar.Logging;

namespace Aspire.Hosting.RemoteDebugging.Sidecar.Process;

/// <summary>
/// Wraps a running project process, pipes its stdout/stderr into a
/// <see cref="LogBuffer"/>, and tracks lifecycle state.
/// </summary>
internal sealed class ManagedProcess : IAsyncDisposable
{
  private readonly System.Diagnostics.Process _process;
  private Task _pipeTask;
  private volatile ProcessState _state = ProcessState.Starting;

  public string ProjectName { get; }
  public int Pid => _process.Id;
  public ProcessState State => _state;
  public LogBuffer LogBuffer { get; } = new();

  private ManagedProcess(string projectName, System.Diagnostics.Process process, Task pipeTask)
  {
    ProjectName = projectName;
    _process = process;
    _pipeTask = pipeTask;
  }

  /// <summary>
  /// Spawns <paramref name="executablePath"/> in <paramref name="workingDirectory"/>,
  /// begins piping its output into a new <see cref="LogBuffer"/>, and returns the
  /// <see cref="ManagedProcess"/> instance.
  /// </summary>
  public static ManagedProcess Start(string projectName, string executablePath, string workingDirectory)
  {
    var psi = new ProcessStartInfo(executablePath)
    {
      WorkingDirectory = workingDirectory,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
    };

    var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
    process.Start();

    // Pipe stdout and stderr concurrently into the log buffer.
    var managed = new ManagedProcess(projectName, process, Task.CompletedTask);
    managed._state = ProcessState.Running;
    managed._pipeTask = Task.WhenAll(
      managed.DrainStreamAsync(process.StandardOutput, isError: false),
      managed.DrainStreamAsync(process.StandardError, isError: true)
    ).ContinueWith(_ =>
    {
      managed.LogBuffer.Complete();
      managed._state = ProcessState.Exited;
    }, TaskScheduler.Default);

    return managed;
  }

  public async Task StopAsync(CancellationToken cancellationToken = default)
  {
    if (_state is ProcessState.Exited or ProcessState.Stopping)
      return;

    _state = ProcessState.Stopping;

    try
    {
      _process.Kill(entireProcessTree: true);
      await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
    catch (InvalidOperationException) { /* already exited */ }
  }

  private async Task DrainStreamAsync(StreamReader reader, bool isError)
  {
    string? line;
    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
    {
      var formatted = isError ? $"[stderr] {line}" : line;
      LogBuffer.Write(formatted);
    }
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync().ConfigureAwait(false);
    await _pipeTask.ConfigureAwait(false);
    _process.Dispose();
  }
}
