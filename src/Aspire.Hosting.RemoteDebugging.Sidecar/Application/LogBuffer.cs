using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;

namespace Aspire.Hosting.RemoteDebugging.Sidecar.Application;

/// <summary>
/// Thread-safe, fan-out log buffer for a single managed process.
/// </summary>
/// <remarks>
/// <para>
/// Every append writes to the internal retention cache (pruning entries beyond the window)
/// and synchronously fans out to all registered subscriber channels.
/// </para>
/// <para>
/// Each call to <see cref="StreamAsync"/> gets its own <see cref="Channel{T}"/> so multiple
/// concurrent gRPC streaming callers each receive every line independently.
/// </para>
/// </remarks>
internal sealed class LogBuffer : ILogBuffer, IDisposable
{
  private readonly object _lock = new();
  private readonly List<CachedLogEntry> _cache = [];
  private readonly List<ChannelWriter<LogLine>> _subscribers = [];
  private readonly TimeSpan _retentionWindow;
  private bool _disposed;

  public LogBuffer(TimeSpan retentionWindow) => _retentionWindow = retentionWindow;

  /// <inheritdoc/>
  public void Append(string content, bool isError)
  {
    var entry = new CachedLogEntry(DateTimeOffset.UtcNow, content, isError);
    lock (_lock)
    {
      _cache.Add(entry);
      PruneOldEntries();

      var logLine = ToLogLine(entry);
      foreach (var writer in _subscribers)
        writer.TryWrite(logLine); // non-blocking; channel is unbounded
    }
  }

  /// <inheritdoc/>
  public async IAsyncEnumerable<LogLine> StreamAsync(
    bool replayCached,
    [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    var channel = Channel.CreateUnbounded<LogLine>(new UnboundedChannelOptions
    {
      SingleReader = true,
      SingleWriter = false,
      AllowSynchronousContinuations = false
    });

    lock (_lock)
    {
      if (replayCached)
        foreach (var cached in _cache)
          channel.Writer.TryWrite(ToLogLine(cached));

      _subscribers.Add(channel.Writer);
    }

    try
    {
      await foreach (var line in channel.Reader.ReadAllAsync(cancellationToken))
        yield return line;
    }
    finally
    {
      lock (_lock)
        _subscribers.Remove(channel.Writer);

      channel.Writer.TryComplete();
    }
  }

  /// <inheritdoc/>
  public IReadOnlyList<CachedLogEntry> GetSnapshot()
  {
    lock (_lock)
      return [.. _cache];
  }

  public void Dispose()
  {
    lock (_lock)
    {
      if (_disposed) return;
      _disposed = true;

      foreach (var writer in _subscribers)
        writer.TryComplete();

      _subscribers.Clear();
    }
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private void PruneOldEntries()
  {
    var cutoff = DateTimeOffset.UtcNow - _retentionWindow;
    while (_cache.Count > 0 && _cache[0].Timestamp < cutoff)
      _cache.RemoveAt(0);
  }

  private static LogLine ToLogLine(CachedLogEntry entry) => new()
  {
    Timestamp = Timestamp.FromDateTimeOffset(entry.Timestamp),
    Content   = entry.Content,
    IsError   = entry.IsError
  };
}
