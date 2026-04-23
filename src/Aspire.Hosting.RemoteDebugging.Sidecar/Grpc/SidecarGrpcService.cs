using Aspire.Hosting.RemoteDebugging.Sidecar.Process;
using Aspire.Hosting.RemoteDebugging.Sidecar.Proto;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using System.Collections.Concurrent;
using System.Reflection;

namespace Aspire.Hosting.RemoteDebugging.Sidecar.Grpc;

/// <summary>
/// gRPC service implementation for the aspire-sidecar daemon.
/// Manages one <see cref="ManagedProcess"/> per project and serves
/// log streaming requests from reconnecting AppHost clients.
/// </summary>
internal sealed class SidecarGrpcService : SidecarService.SidecarServiceBase, IAsyncDisposable
{
  private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new();

  private static readonly string Version =
    typeof(SidecarGrpcService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? typeof(SidecarGrpcService).Assembly.GetName().Version?.ToString()
    ?? "1.0.0";

  public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context) =>
    Task.FromResult(new PingResponse { Version = Version });

  public override async Task<StartResponse> Start(StartRequest request, ServerCallContext context)
  {
    if (string.IsNullOrWhiteSpace(request.Project) ||
        string.IsNullOrWhiteSpace(request.Path) ||
        string.IsNullOrWhiteSpace(request.Executable))
    {
      throw new RpcException(new Status(StatusCode.InvalidArgument, "project, path, and executable are required"));
    }

    // Stop any existing process for this project before starting a new one.
    if (_processes.TryRemove(request.Project, out var existing))
      await existing.DisposeAsync();

    var executablePath = Path.Combine(request.Path, request.Executable);
    var process = ManagedProcess.Start(request.Project, executablePath, request.Path);
    _processes[request.Project] = process;

    return new StartResponse { Pid = process.Pid };
  }

  public override async Task<StopResponse> Stop(StopRequest request, ServerCallContext context)
  {
    if (string.IsNullOrWhiteSpace(request.Project))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "project is required"));

    if (_processes.TryRemove(request.Project, out var process))
      await process.DisposeAsync();

    return new StopResponse();
  }

  public override Task<StatusResponse> GetStatus(StatusRequest request, ServerCallContext context)
  {
    if (string.IsNullOrWhiteSpace(request.Project))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "project is required"));

    if (_processes.TryGetValue(request.Project, out var process))
    {
      return Task.FromResult(new StatusResponse
      {
        Running = process.State == ProcessState.Running,
        Pid     = process.Pid,
      });
    }

    return Task.FromResult(new StatusResponse { Running = false });
  }

  public override async Task StreamLogs(StreamLogsRequest request, IServerStreamWriter<LogLine> responseStream, ServerCallContext context)
  {
    if (string.IsNullOrWhiteSpace(request.Project))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "project is required"));

    if (!_processes.TryGetValue(request.Project, out var process))
      throw new RpcException(new Status(StatusCode.NotFound, $"No process found for project '{request.Project}'"));

    var buffer = process.LogBuffer;
    var ct = context.CancellationToken;
    var fromOffset = request.FromOffset;

    while (!ct.IsCancellationRequested)
    {
      var (entries, signal) = buffer.GetFrom(fromOffset);

      foreach (var entry in entries)
      {
        await responseStream.WriteAsync(new LogLine
        {
          Line   = entry.Line,
          Offset = entry.Offset,
        }, ct);
      }

      if (entries.Count > 0)
        fromOffset = entries[^1].Offset + 1;

      try
      {
        await signal.WaitAsync(ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException) { break; }
    }
  }

  public async ValueTask DisposeAsync()
  {
    foreach (var process in _processes.Values)
      await process.DisposeAsync();
    _processes.Clear();
  }
}
