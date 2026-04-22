using System.Collections.Concurrent;
using System.Net.Sockets;
using Aspire.Hosting.RemoteDebugging.Sidecar.Process;

namespace Aspire.Hosting.RemoteDebugging.Sidecar.Daemon;

/// <summary>
/// Long-running Unix domain socket server that manages one <see cref="ManagedProcess"/>
/// per project and serves log streaming requests from reconnecting AppHost clients.
/// </summary>
internal sealed class SidecarDaemon : IAsyncDisposable
{
  private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new();

  public static string SocketPath =>
    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aspire-sidecar.sock");

  public async Task RunAsync(CancellationToken cancellationToken)
  {
    // Clean up a stale socket file from a previous run.
    if (File.Exists(SocketPath))
      File.Delete(SocketPath);

    var endpoint = new UnixDomainSocketEndPoint(SocketPath);
    using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    listener.Bind(endpoint);
    listener.Listen(backlog: 8);

    Console.WriteLine($"aspire-sidecar daemon listening on {SocketPath}");

    await using var _reg = cancellationToken.Register(() => listener.Close());

    while (!cancellationToken.IsCancellationRequested)
    {
      Socket client;
      try
      {
        client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException) { break; }
      catch (SocketException) { break; }

      _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
    }
  }

  private async Task HandleClientAsync(Socket socket, CancellationToken cancellationToken)
  {
    await using var stream = new NetworkStream(socket, ownsSocket: true);
    using var reader = new StreamReader(stream, leaveOpen: true);
    await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };

    try
    {
      var json = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
      if (json is null) return;

      var message = SidecarMessage.Deserialize(json);
      if (message is null)
      {
        await WriteAsync(writer, new SidecarMessage(MessageType.Error) { Message = "Invalid message" }, cancellationToken);
        return;
      }

      switch (message.Type)
      {
        case MessageType.Start:
          await HandleStartAsync(message, writer, cancellationToken);
          break;

        case MessageType.Stop:
          await HandleStopAsync(message, writer, cancellationToken);
          break;

        case MessageType.Status:
          await HandleStatusAsync(message, writer, cancellationToken);
          break;

        case MessageType.Logs:
          await HandleLogsAsync(message, writer, cancellationToken);
          break;

        default:
          await WriteAsync(writer, new SidecarMessage(MessageType.Error) { Message = $"Unknown message type: {message.Type}" }, cancellationToken);
          break;
      }
    }
    catch (OperationCanceledException) { /* client disconnected or cancelled */ }
    catch (Exception ex)
    {
      try
      {
        await WriteAsync(writer, new SidecarMessage(MessageType.Error) { Message = ex.Message }, cancellationToken);
      }
      catch { /* best-effort */ }
    }
  }

  private async Task HandleStartAsync(SidecarMessage message, StreamWriter writer, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(message.Project) ||
        string.IsNullOrWhiteSpace(message.Path) ||
        string.IsNullOrWhiteSpace(message.Executable))
    {
      await WriteAsync(writer, new SidecarMessage(MessageType.Error) { Message = "project, path, and executable are required" }, cancellationToken);
      return;
    }

    // Stop any existing process for this project before starting a new one.
    if (_processes.TryRemove(message.Project, out var existing))
      await existing.DisposeAsync();

    var executablePath = System.IO.Path.Combine(message.Path, message.Executable);
    var process = ManagedProcess.Start(message.Project, executablePath, message.Path);
    _processes[message.Project] = process;

    await WriteAsync(writer, new SidecarMessage(MessageType.Started) { Pid = process.Pid }, cancellationToken);
  }

  private async Task HandleStopAsync(SidecarMessage message, StreamWriter writer, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(message.Project))
    {
      await WriteAsync(writer, new SidecarMessage(MessageType.Error) { Message = "project is required" }, cancellationToken);
      return;
    }

    if (_processes.TryRemove(message.Project, out var process))
      await process.DisposeAsync();

    await WriteAsync(writer, new SidecarMessage(MessageType.Stopped), cancellationToken);
  }

  private async Task HandleStatusAsync(SidecarMessage message, StreamWriter writer, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(message.Project))
    {
      await WriteAsync(writer, new SidecarMessage(MessageType.Error) { Message = "project is required" }, cancellationToken);
      return;
    }

    if (_processes.TryGetValue(message.Project, out var process))
    {
      var running = process.State == Process.ProcessState.Running;
      await WriteAsync(writer, new SidecarMessage(MessageType.StatusResult)
      {
        Running = running,
        Pid = process.Pid
      }, cancellationToken);
    }
    else
    {
      await WriteAsync(writer, new SidecarMessage(MessageType.StatusResult) { Running = false }, cancellationToken);
    }
  }

  private async Task HandleLogsAsync(SidecarMessage message, StreamWriter writer, CancellationToken cancellationToken)
  {
    if (string.IsNullOrWhiteSpace(message.Project))
    {
      await WriteAsync(writer, new SidecarMessage(MessageType.Error) { Message = "project is required" }, cancellationToken);
      return;
    }

    if (!_processes.TryGetValue(message.Project, out var process))
    {
      await WriteAsync(writer, new SidecarMessage(MessageType.Error) { Message = $"No process found for project '{message.Project}'" }, cancellationToken);
      return;
    }

    // Stream buffered + live log entries until the client disconnects or is cancelled.
    var buffer = process.LogBuffer;

    while (!cancellationToken.IsCancellationRequested)
    {
      var (entries, signal) = buffer.GetFrom(message.From);

      foreach (var entry in entries)
      {
        await WriteAsync(writer, new SidecarMessage(MessageType.LogLine)
        {
          Line = entry.Line,
          Offset = entry.Offset
        }, cancellationToken);
      }

      // Advance the offset past what we just sent.
      if (entries.Count > 0)
        message = message with { From = entries[^1].Offset + 1 };

      try
      {
        await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException) { break; }
    }
  }

  private static async Task WriteAsync(StreamWriter writer, SidecarMessage message, CancellationToken cancellationToken) =>
    await writer.WriteLineAsync(message.Serialize().AsMemory(), cancellationToken);

  public async ValueTask DisposeAsync()
  {
    foreach (var process in _processes.Values)
      await process.DisposeAsync();
    _processes.Clear();

    if (File.Exists(SocketPath))
      File.Delete(SocketPath);
  }
}
