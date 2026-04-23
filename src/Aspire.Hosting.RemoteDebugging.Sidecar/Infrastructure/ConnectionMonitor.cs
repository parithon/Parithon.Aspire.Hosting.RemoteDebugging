using Aspire.Hosting.RemoteDebugging.Sidecar.Application;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.RemoteDebugging.Sidecar.Infrastructure;

/// <summary>
/// Background service that monitors gRPC connectivity and triggers a graceful shutdown
/// when the AppHost has been unreachable for longer than <see cref="SidecarOptions.ConnectionTimeout"/>
/// <em>and</em> no managed child processes are running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connection model:</b>  The sidecar considers itself "active" when either:
/// <list type="bullet">
///   <item>At least one <c>StreamLogs</c> call is in progress (<see cref="_activeStreamCount"/> &gt; 0), or</item>
///   <item>Any gRPC method was called within the connection-timeout window
///         (tracked via <see cref="RecordActivity"/>).</item>
/// </list>
/// </para>
/// <para>
/// <b>Timeout behaviour:</b>
/// <list type="bullet">
///   <item>Timeout elapsed + child processes still running → log a warning and stay alive.
///         The sidecar re-checks every <c>CheckInterval</c> so it will self-terminate once
///         all processes have exited, or resume normal operation if the AppHost reconnects.</item>
///   <item>Timeout elapsed + no child processes → proceed with graceful shutdown:
///         save cached logs, stop all processes, call <see cref="IHostApplicationLifetime.StopApplication"/>.</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class ConnectionMonitor(
  ILogger<ConnectionMonitor> logger,
  IOptions<SidecarOptions> options,
  IProcessManager processManager,
  LogCachePersistence persistence,
  IHostApplicationLifetime lifetime) : BackgroundService
{
  private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

  private int _activeStreamCount;

  // Stored as UTC ticks; accessed via Interlocked for thread-safety.
  // Initialised to MaxValue so the timer never fires before the first connection.
  private long _lastActivityTicks = DateTimeOffset.MaxValue.UtcTicks;
  private bool _hasEverConnected;

  // ── Public API called by SidecarGrpcService ───────────────────────────────

  /// <summary>
  /// Records that gRPC activity occurred.  Resets the inactivity timer.
  /// Must be called at the start of every RPC method handler.
  /// </summary>
  public void RecordActivity()
  {
    _hasEverConnected = true;
    Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
  }

  /// <summary>Signals that a <c>StreamLogs</c> call has started.</summary>
  public void OnStreamingStarted()
  {
    RecordActivity();
    Interlocked.Increment(ref _activeStreamCount);
    logger.LogDebug("Streaming started. Active streams: {Count}.", _activeStreamCount);
  }

  /// <summary>Signals that a <c>StreamLogs</c> call has ended (cancelled or completed).</summary>
  public void OnStreamingEnded()
  {
    var remaining = Interlocked.Decrement(ref _activeStreamCount);
    if (remaining <= 0)
    {
      _activeStreamCount = 0; // clamp — should not go negative
      logger.LogWarning(
        "All streaming connections closed. Inactivity timeout of {Timeout} begins now.",
        options.Value.ConnectionTimeout);
    }

    logger.LogDebug("Streaming ended. Active streams: {Count}.", _activeStreamCount);
  }

  /// <summary>
  /// Stops all managed processes, dumps cached logs, and requests application shutdown.
  /// Called both by the inactivity timeout path and by the gRPC <c>Shutdown</c> RPC.
  /// Returns <see langword="true"/> when all processes stopped without error.
  /// </summary>
  public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
  {
    logger.LogInformation("Graceful shutdown initiated.");
    return await HandleShutdownAsync(cancellationToken).ConfigureAwait(false);
  }

  // ── BackgroundService ─────────────────────────────────────────────────────

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    logger.LogInformation(
      "ConnectionMonitor started. Inactivity timeout: {Timeout}.",
      options.Value.ConnectionTimeout);

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        break;
      }

      // Do not start the countdown until at least one client has ever connected.
      if (!_hasEverConnected)
        continue;

      // An active stream is a heartbeat — no timeout while streaming.
      if (_activeStreamCount > 0)
        continue;

      var elapsed = DateTimeOffset.UtcNow -
        new DateTimeOffset(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);
      if (elapsed < options.Value.ConnectionTimeout)
        continue;

      // Connection timeout has elapsed. If child processes are still running, keep the sidecar
      // alive so their state is preserved for a potential AppHost reconnect. Re-check every
      // interval — when all processes have exited the sidecar will self-terminate.
      var runningProcesses = processManager.ListProcesses();
      if (runningProcesses.Count > 0)
      {
        logger.LogWarning(
          "AppHost appears disconnected (idle for {Elapsed:mm\\:ss}) but {Count} managed " +
          "process(es) are still running — keeping sidecar alive for reconnect.",
          elapsed, runningProcesses.Count);
        continue;
      }

      logger.LogError(
        "No gRPC activity for {Elapsed:mm\\:ss} (threshold: {Timeout}) and no child " +
        "processes are running. AppHost appears to be permanently disconnected. Shutting down.",
        elapsed, options.Value.ConnectionTimeout);
      await HandleShutdownAsync(CancellationToken.None).ConfigureAwait(false);
      return;
    }
  }

  // ── Private ───────────────────────────────────────────────────────────────

  private async Task<bool> HandleShutdownAsync(CancellationToken cancellationToken)
  {
    // 1. Dump cached logs to disk for post-mortem analysis.
    await persistence.SaveAsync(processManager.GetAllProcesses()).ConfigureAwait(false);

    // 2. Stop and remove all child processes.
    var stopped = await processManager.StopAllAsync(
      cancellationToken == default ? CancellationToken.None : cancellationToken)
      .ConfigureAwait(false);

    // 3. Shut down the sidecar itself.
    logger.LogInformation("Shutdown complete ({Stopped} process(es) stopped). Requesting application shutdown.", stopped);
    lifetime.StopApplication();
    return stopped >= 0; // StopAllAsync throws on hard failures; any return value here means "we tried"
  }
}
