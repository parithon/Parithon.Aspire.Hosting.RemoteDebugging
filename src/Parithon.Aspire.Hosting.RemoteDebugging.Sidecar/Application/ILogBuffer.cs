namespace Parithon.Aspire.Hosting.RemoteDebugging.Sidecar.Application;

/// <summary>
/// A timestamped log line held in the in-memory cache while no streaming client is connected.
/// </summary>
internal readonly record struct CachedLogEntry(
  DateTimeOffset Timestamp,
  string Content,
  bool IsError);

/// <summary>
/// Fan-out log buffer for a single managed process.
/// <list type="bullet">
///   <item>All output lines are always appended (with pruning of entries older than the retention window).</item>
///   <item>Each active <see cref="StreamAsync"/> subscriber gets its own channel and receives all
///         future lines.  Callers may also request replay of cached lines before live output.</item>
///   <item>When no subscriber is active, lines accumulate in the cache up to the retention window.</item>
/// </list>
/// </summary>
internal interface ILogBuffer
{
  /// <summary>Appends a new log line and fans it out to all active streaming subscribers.</summary>
  void Append(string content, bool isError);

  /// <summary>
  /// Streams log lines to the caller.  If <paramref name="replayCached"/> is <see langword="true"/>
  /// the current cache snapshot is yielded first, followed by live lines as they arrive.
  /// The stream completes when <paramref name="cancellationToken"/> is cancelled.
  /// </summary>
  IAsyncEnumerable<LogLine> StreamAsync(bool replayCached, CancellationToken cancellationToken);

  /// <summary>
  /// Returns a point-in-time snapshot of all lines currently in the retention cache.
  /// Used by <see cref="Infrastructure.LogCachePersistence"/> before shutdown.
  /// </summary>
  IReadOnlyList<CachedLogEntry> GetSnapshot();
}
