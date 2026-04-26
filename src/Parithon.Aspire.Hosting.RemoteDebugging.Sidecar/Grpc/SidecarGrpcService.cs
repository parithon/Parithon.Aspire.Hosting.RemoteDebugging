using Parithon.Aspire.Hosting.RemoteDebugging.Sidecar.Application;
using Parithon.Aspire.Hosting.RemoteDebugging.Sidecar.Domain;
using Parithon.Aspire.Hosting.RemoteDebugging.Sidecar.Infrastructure;
using Grpc.Core;

namespace Parithon.Aspire.Hosting.RemoteDebugging.Sidecar.Grpc;

/// <summary>
/// gRPC service implementation.  Bridges the AppHost to the
/// <see cref="IProcessManager"/> and <see cref="ConnectionMonitor"/>.
/// </summary>
/// <remarks>
/// Every method calls <see cref="ConnectionMonitor.RecordActivity"/> before doing any
/// work so that the inactivity timer is reset on each incoming RPC.
/// </remarks>
internal sealed class SidecarGrpcService(
  IProcessManager processManager,
  ConnectionMonitor connectionMonitor,
  ILogger<SidecarGrpcService> logger) : SidecarService.SidecarServiceBase
{
  private static readonly string Version =
    typeof(SidecarGrpcService).Assembly.GetName().Version?.ToString() ?? "0.0.0";

  // ── Ping ──────────────────────────────────────────────────────────────────

  public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
  {
    connectionMonitor.RecordActivity();

    var processes     = processManager.ListProcesses();
    var runningCount  = processes.Count(p => p.State == ProcessState.Running);

    return Task.FromResult(new PingResponse
    {
      Version             = Version,
      ActiveProcessCount  = runningCount
    });
  }

  // ── StartProcess ──────────────────────────────────────────────────────────

  public override async Task<StartProcessResponse> StartProcess(
    StartProcessRequest request,
    ServerCallContext context)
  {
    connectionMonitor.RecordActivity();

    if (string.IsNullOrWhiteSpace(request.Name))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "'name' is required."));
    if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "'working_directory' is required."));
    if (string.IsNullOrWhiteSpace(request.EntryPoint))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "'entry_point' is required."));

    try
    {
      var (pid, alreadyRunning) = await processManager.StartProcessAsync(
        request.Name,
        request.WorkingDirectory,
        request.EntryPoint,
        request.Environment,
        context.CancellationToken,
        string.IsNullOrWhiteSpace(request.Executable) ? null : request.Executable).ConfigureAwait(false);

      return new StartProcessResponse { Pid = pid, AlreadyRunning = alreadyRunning };
    }
    catch (Exception ex) when (ex is not RpcException and not OperationCanceledException)
    {
      logger.LogError(ex, "Failed to start process '{Name}'.", request.Name);
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

  // ── StopProcess ───────────────────────────────────────────────────────────

  public override async Task<StopProcessResponse> StopProcess(
    StopProcessRequest request,
    ServerCallContext context)
  {
    connectionMonitor.RecordActivity();

    if (string.IsNullOrWhiteSpace(request.Name))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "'name' is required."));

    var success = await processManager
      .StopProcessAsync(request.Name, context.CancellationToken)
      .ConfigureAwait(false);

    return new StopProcessResponse { Success = success };
  }

  // ── ListProcesses ─────────────────────────────────────────────────────────

  public override Task<ListProcessesResponse> ListProcesses(
    ListProcessesRequest request,
    ServerCallContext context)
  {
    connectionMonitor.RecordActivity();

    var response = new ListProcessesResponse();
    foreach (var (name, pid, state) in processManager.ListProcesses())
    {
      response.Processes.Add(new ProcessInfo
      {
        Name  = name,
        Pid   = pid,
        State = state.ToString()
      });
    }

    return Task.FromResult(response);
  }

  // ── StreamLogs ────────────────────────────────────────────────────────────

  public override async Task StreamLogs(
    StreamLogsRequest request,
    IServerStreamWriter<LogLine> responseStream,
    ServerCallContext context)
  {
    if (string.IsNullOrWhiteSpace(request.Name))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "'name' is required."));

    var buffer = processManager.GetLogBuffer(request.Name);
    if (buffer is null)
      throw new RpcException(new Status(
        StatusCode.NotFound, $"Process '{request.Name}' not found."));

    connectionMonitor.OnStreamingStarted();
    logger.LogDebug("Log streaming started for '{Name}'.", request.Name);

    try
    {
      await foreach (var line in buffer.StreamAsync(request.ReplayCached, context.CancellationToken))
        await responseStream.WriteAsync(line, context.CancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      logger.LogDebug("Log streaming for '{Name}' cancelled by client.", request.Name);
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Unexpected error streaming logs for '{Name}'.", request.Name);
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
    finally
    {
      connectionMonitor.OnStreamingEnded();
      logger.LogDebug("Log streaming ended for '{Name}'.", request.Name);
    }
  }

  // ── Reset ─────────────────────────────────────────────────────────────────

  public override async Task<ResetResponse> Reset(
    ResetRequest request,
    ServerCallContext context)
  {
    connectionMonitor.RecordActivity();
    logger.LogInformation("Reset RPC received — stopping all processes from previous session.");

    var stopped = await processManager.StopAllAsync(context.CancellationToken).ConfigureAwait(false);
    logger.LogInformation("Reset complete: {Stopped} process(es) stopped.", stopped);

    return new ResetResponse { ProcessesStopped = stopped };
  }

  // ── Shutdown ──────────────────────────────────────────────────────────────

  public override async Task<ShutdownResponse> Shutdown(
    ShutdownRequest request,
    ServerCallContext context)
  {
    connectionMonitor.RecordActivity();
    logger.LogInformation("Shutdown RPC received from AppHost.");

    var success = await connectionMonitor.ShutdownAsync(context.CancellationToken)
      .ConfigureAwait(false);

    return new ShutdownResponse { Success = success };
  }
}
