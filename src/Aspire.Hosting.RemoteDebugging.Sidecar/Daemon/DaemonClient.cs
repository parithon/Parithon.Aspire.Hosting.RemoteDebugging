using System.Net.Sockets;

namespace Aspire.Hosting.RemoteDebugging.Sidecar.Daemon;

/// <summary>
/// Connects to a running <see cref="SidecarDaemon"/> over its Unix domain socket
/// and exposes typed request/response methods for each sidecar command.
/// </summary>
internal sealed class DaemonClient
{
  private readonly string _socketPath;

  public DaemonClient(string? socketPath = null)
  {
    _socketPath = socketPath ?? SidecarDaemon.SocketPath;
  }

  public async Task<int> StartProjectAsync(string project, string path, string executable, CancellationToken cancellationToken)
  {
    var request = new SidecarMessage(MessageType.Start)
    {
      Project = project,
      Path = path,
      Executable = executable,
    };

    var response = await SendAsync(request, cancellationToken);
    return response.Type == MessageType.Started
      ? response.Pid
      : throw new InvalidOperationException($"Failed to start project '{project}': {response.Message}");
  }

  public async Task StopProjectAsync(string project, CancellationToken cancellationToken)
  {
    var request = new SidecarMessage(MessageType.Stop) { Project = project };
    var response = await SendAsync(request, cancellationToken);

    if (response.Type == MessageType.Error)
      throw new InvalidOperationException($"Failed to stop project '{project}': {response.Message}");
  }

  public async Task<(bool Running, int Pid)> GetStatusAsync(string project, CancellationToken cancellationToken)
  {
    var request = new SidecarMessage(MessageType.Status) { Project = project };
    var response = await SendAsync(request, cancellationToken);

    if (response.Type == MessageType.Error)
      throw new InvalidOperationException($"Failed to get status for project '{project}': {response.Message}");

    return (response.Running, response.Pid);
  }

  /// <summary>
  /// Opens a streaming connection to the daemon and writes each log line to
  /// <paramref name="onLine"/> until the connection is closed or cancelled.
  /// </summary>
  public async Task StreamLogsAsync(string project, long fromOffset, Func<string, long, Task> onLine, CancellationToken cancellationToken)
  {
    var endpoint = new UnixDomainSocketEndPoint(_socketPath);
    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

    await using var stream = new NetworkStream(socket, ownsSocket: false);
    using var reader = new StreamReader(stream, leaveOpen: true);
    await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };

    var request = new SidecarMessage(MessageType.Logs) { Project = project, From = fromOffset };
    await writer.WriteLineAsync(request.Serialize().AsMemory(), cancellationToken);

    while (!cancellationToken.IsCancellationRequested)
    {
      var json = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
      if (json is null) break;

      var message = SidecarMessage.Deserialize(json);
      if (message is null) continue;

      if (message.Type == MessageType.Error)
        throw new InvalidOperationException(message.Message);

      if (message.Type == MessageType.LogLine && message.Line is not null)
        await onLine(message.Line, message.Offset);
    }
  }

  private async Task<SidecarMessage> SendAsync(SidecarMessage request, CancellationToken cancellationToken)
  {
    var endpoint = new UnixDomainSocketEndPoint(_socketPath);
    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

    await using var stream = new NetworkStream(socket, ownsSocket: false);
    using var reader = new StreamReader(stream, leaveOpen: true);
    await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };

    await writer.WriteLineAsync(request.Serialize().AsMemory(), cancellationToken);

    var json = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
      ?? throw new InvalidOperationException("Daemon closed the connection without responding.");

    return SidecarMessage.Deserialize(json)
      ?? throw new InvalidOperationException("Daemon returned an invalid response.");
  }
}
