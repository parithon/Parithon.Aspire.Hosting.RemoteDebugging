using Aspire.Hosting.RemoteDebugging.Sidecar.Application;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.RemoteDebugging.Sidecar.Infrastructure;

/// <summary>
/// Background service that monitors gRPC connectivity and triggers a graceful shutdown
/// when the AppHost has been unreachable for longer than <see cref="SidecarOptions.ConnectionTimeout"/>.
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
/// <b>Timeout sequence:</b>
/// <list type="number">
///   <item>Save cached logs from all processes to a dump file.</item>
///   <item>Stop all managed child processes.</item>
///   <item>Call <see cref="IHostApplicationLifetime.StopApplication"/>.</item>
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

      logger.LogError(
        "No gRPC activity for {Elapsed:mm\\:ss} (threshold: {Timeout}). " +
        "AppHost appears to be permanently disconnected.",
        elapsed, options.Value.ConnectionTimeout);
      await HandleTimeoutAsync().ConfigureAwait(false);
      return;
    }
  }

  // ── Private ───────────────────────────────────────────────────────────────

  private async Task HandleTimeoutAsync()
  {
    var processes = processManager.GetAllProcesses();

    // 1. Dump cached logs to disk for post-mortem analysis.
    await persistence.SaveAsync(processes).ConfigureAwait(false);

    // 2. Stop all child processes.
    foreach (var process in processes)
    {
      try
      {
        await process.StopAsync(CancellationToken.None).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Error stopping '{Name}' during timeout shutdown.", process.Name);
      }
    }

    // 3. Shut down the sidecar itself.
    logger.LogInformation("Connection timeout handled. Requesting application shutdown.");
    lifetime.StopApplication();
  }
}
